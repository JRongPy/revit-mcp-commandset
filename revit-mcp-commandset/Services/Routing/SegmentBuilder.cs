using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Utils.Routing;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitMCPCommandSet.Services.Routing
{
    internal static class SegmentBuilder
    {
        // =============== 簡易檔案日誌 ===============
        private static readonly string _logDir = @"D:\MCP_Log";
        private static readonly string _logFile = Path.Combine(_logDir, "RoutePipesByWaypoints.log");

        private static void WriteLog(string msg)
        {
            try
            {
                if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
                File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}\n");
            }
            catch { /* ignore IO/permission errors */ }
        }


        /// <summary> 
        /// 從 fromConn 朝 to 點：方向一致→一段，否則折彎+彎頭。 
        /// 回傳最後一段 Pipe 的 ElementId。 
        /// </summary>
        public static ElementId CreatePipeSegmentAlignedOrBent(
            Document doc, RoutingContext ctx, Connector currentConnector,
            XYZ fromPoint, XYZ toPoint, double minSegmentLen_ft, double tol_ft, List<ElementId> acc)
        {
            var start = (currentConnector?.Origin ?? fromPoint);
            var dirWanted = (toPoint - start).Normalize();

            var connDir = currentConnector?.CoordinateSystem?.BasisZ?.Normalize();

            bool aligned = connDir != null && Math.Abs(connDir.DotProduct(dirWanted)) > 0.99;
            bool isPipe = currentConnector?.Owner is Pipe;
            // 四種狀況處理：是否為管/方向是否對齊
            if (isPipe)
            {
                Pipe currentPipe = currentConnector.Owner as Pipe;

                if (!aligned)
                {
                    // 方向不一致但接頭來自 Pipe → 直接打一段到目標
                    var newPipe = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, start, toPoint);
                    PipeUtils.SetPipeDiameter(newPipe, ctx.Diameter_ft);
                    acc?.Add(newPipe.Id);
                    // 取得newPipe來自start端的connector
                    Connector newPipeStartConn = ConnectorUtils.GetConnectors(newPipe)
                        .OrderBy(c => c.Origin.DistanceTo(start)).First();
                    // 放彎頭
                    Connector targetCurrentConn = ConnectorUtils.GetConnectors(currentPipe)
                        .OrderBy(c => c.Origin.DistanceTo(start)).First();
                    try {
                        var elbow = doc.Create.NewElbowFitting(targetCurrentConn, newPipeStartConn);
                        acc?.Add(elbow.Id);
                    }catch (Exception ex)
                    {
                        WriteLog($"[SegmentBuilder] [ERROR] {ex}");
                    }

                    return newPipe.Id;
                }
                else
                {
                    // 最近的接頭直接換成該座標
                    currentConnector.Origin = toPoint;
                    var p = currentConnector?.Owner;
                    return p.Id;
                }
            }
            else  // 非 Pipe 接頭
            {
                if (!aligned && connDir != null)
                {
                    // 先沿接頭方向推出一小段，再轉向目標
                    var kickEnd = start + connDir.Multiply(minSegmentLen_ft);
                    var p1 = Pipe.Create(doc, ctx.PipeTypeId, ctx.LevelId, currentConnector, kickEnd);
                    PipeUtils.SetPipeDiameter(p1, ctx.Diameter_ft);
                    acc?.Add(p1.Id);

                    // 第二段朝目標
                    var p2 = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, kickEnd, toPoint);
                    PipeUtils.SetPipeDiameter(p2, ctx.Diameter_ft);
                    acc?.Add(p2.Id);

                    // 在兩段相接點放「彎頭」
                    var c1 = ConnectorUtils.GetConnectors(p1).OrderBy(c => c.Origin.DistanceTo(kickEnd)).Last();
                    var c2 = ConnectorUtils.GetConnectors(p2).OrderBy(c => c.Origin.DistanceTo(kickEnd)).First();
                    var elbow = doc.Create.NewElbowFitting(c1, c2);
                    acc?.Add(elbow.Id);

                    return p2.Id;
                }
                else
                {
                    // 方向一致：直接打一段
                    var p = Pipe.Create(doc, ctx.PipeTypeId, ctx.LevelId, currentConnector, toPoint);
                    PipeUtils.SetPipeDiameter(p, ctx.Diameter_ft);
                    acc?.Add(p.Id);
                    return p.Id;
                }
            }

          
        }


        public static void ConnectToTargetEnd(
            Document doc, RoutingContext ctx, Connector lastConnector,
            RoutingAnchor end, List<ElementId> acc, double tol_ft)
        {
            if (end.AnchorConnector != null)
            {
                // 與終點connector距離遠 → 補一小段再用彎頭連接；近 → 直接彎頭
                var near = lastConnector;
                var far = end.AnchorConnector;

                if (near.Origin.DistanceTo(far.Origin) > tol_ft)
                {
                    var p = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, near.Origin, far.Origin);
                    PipeUtils.SetPipeDiameter(p, ctx.Diameter_ft);
                    acc?.Add(p.Id);

                    var pNear = ConnectorUtils.GetConnectors(p)
                        .OrderBy(c => c.Origin.DistanceTo(near.Origin)).First();
                    doc.Create.NewElbowFitting(near, pNear);

                    var pFar = ConnectorUtils.GetFarConnector(p, pNear.Origin);
                    if (pFar != null) doc.Create.NewElbowFitting(pFar, far);
                }
                else
                {
                    doc.Create.NewElbowFitting(near, far);
                }
                return;
            }

            // 終點無connector → 打最後一段到 anchor point（必要時後續再行處理轉接）
            var finalPipe = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, lastConnector.Origin, end.AnchorPoint);
            PipeUtils.SetPipeDiameter(finalPipe, ctx.Diameter_ft);
            acc?.Add(finalPipe.Id);
        }


        /// <summary>
        /// 在幹管 host 的 projPt 位置建立分支。
        /// pref = "Takeoff" → 以 Takeoff 吸附；"Tee" → 切斷後以 Tee 連接。
        /// 回傳：新建的分支 Pipe（供後續繼續佈管使用）。
        /// 注意：需在 Transaction 內呼叫。
        /// </summary>
        public static Pipe CreateBranchAt(
            Document doc, RoutingContext ctx, Pipe host, XYZ projPt, XYZ targetPt, string pref)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (!(host.Location is LocationCurve lc) || lc.Curve == null)
                throw new InvalidOperationException("Host pipe has no valid LocationCurve.");

            // 1) 將 projPt 投影到幹管中心線，確保在曲線上
            var curve = lc.Curve;
            var res = curve.Project(projPt);
            var onCrv = (res != null) ? res.XYZPoint : curve.Evaluate(0.5, true);

            // 3) 建立分支管
            var branchStart = onCrv;
            var branchEnd = targetPt;

            var branch = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, branchStart, branchEnd);
            PipeUtils.SetPipeDiameter(branch, ctx.Diameter_ft);

            // 4) 依偏好策略與幹管連接
            string prefLower = pref?.ToLowerInvariant();
            if (prefLower == "tee")
            {
                // 4A) 以 Tee 連接：先把幹管在 onCrv 切斷
                //     Revit 會回傳新產生的另一段管的 ElementId
                ElementId newId = PlumbingUtils.BreakCurve(doc, host.Id, onCrv);
                var host2 = doc.GetElement(newId) as Pipe;

                // 取兩段幹管在切點附近的端頭接頭（各取一端）
                var cHostA = ConnectorUtils.GetNearConnector(host, onCrv);
                var cHostB = ConnectorUtils.GetNearConnector(host2, onCrv);

                // 分支管取「外端」接頭
                var cBranch = ConnectorUtils.GetConnectors(branch)
                    .OrderBy(c => c.Origin.DistanceTo(onCrv)).FirstOrDefault();

                // 放三通
                doc.Create.NewTeeFitting(cHostA, cHostB, cBranch);
            }
            else
            {
                // 4B) 以 Takeoff 連接（預設）
                //     直接將分支外端接頭吸附到幹管
                var cBranch = ConnectorUtils.GetConnectors(branch)
                    .OrderByDescending(c => c.Origin.DistanceTo(onCrv)).FirstOrDefault();

                // NewTakeoffFitting(connector, trunkCurve)
                doc.Create.NewTakeoffFitting(cBranch, host);
            }

            return branch;
        }
    }
}

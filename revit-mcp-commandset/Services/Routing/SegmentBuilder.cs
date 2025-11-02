using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
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
                    SetPipeDiameter(newPipe, ctx.Diameter_ft);
                    acc?.Add(newPipe.Id);
                    // 取得newPipe來自start端的connector
                    Connector newPipeStartConn = ConnectorUtils.GetPipeConnectors(newPipe)
                        .OrderBy(c => c.Origin.DistanceTo(start)).First();
                    // 放彎頭
                    Connector targetCurrentConn = ConnectorUtils.GetPipeConnectors(currentPipe)
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
                    SetPipeDiameter(p1, ctx.Diameter_ft);
                    acc?.Add(p1.Id);

                    // 第二段朝目標
                    var p2 = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, kickEnd, toPoint);
                    SetPipeDiameter(p2, ctx.Diameter_ft);
                    acc?.Add(p2.Id);

                    // 在兩段相接點放「彎頭」
                    var c1 = ConnectorUtils.GetPipeConnectors(p1).OrderBy(c => c.Origin.DistanceTo(kickEnd)).Last();
                    var c2 = ConnectorUtils.GetPipeConnectors(p2).OrderBy(c => c.Origin.DistanceTo(kickEnd)).First();
                    var elbow = doc.Create.NewElbowFitting(c1, c2);
                    acc?.Add(elbow.Id);

                    return p2.Id;
                }
                else
                {
                    // 方向一致：直接打一段
                    var p = Pipe.Create(doc, ctx.PipeTypeId, ctx.LevelId, currentConnector, toPoint);
                    SetPipeDiameter(p, ctx.Diameter_ft);
                    acc?.Add(p.Id);
                    return p.Id;
                }
            }

          
        }


        public static void ConnectToTargetEnd(
            Document doc, RoutingContext ctx, Connector lastConnector,
            RoutingServices.AttachPoint end, List<ElementId> acc, double tol_ft)
        {
            if (end.Connector != null)
            {
                // 與終點connector距離遠 → 補一小段再用彎頭連接；近 → 直接彎頭
                var near = lastConnector;
                var far = end.Connector;

                if (near.Origin.DistanceTo(far.Origin) > tol_ft)
                {
                    var p = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, near.Origin, far.Origin);
                    SetPipeDiameter(p, ctx.Diameter_ft);
                    acc?.Add(p.Id);

                    var pNear = ConnectorUtils.GetPipeConnectors(p)
                        .OrderBy(c => c.Origin.DistanceTo(near.Origin)).First();
                    doc.Create.NewElbowFitting(near, pNear);

                    var pFar = ConnectorUtils.GetFarEndConnector(p, pNear.Origin);
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
            SetPipeDiameter(finalPipe, ctx.Diameter_ft);
            acc?.Add(finalPipe.Id);
        }

        private static void SetPipeDiameter(Pipe p, double diameterFt)
        {
            if (diameterFt > 0)
            {
                var diaParam = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diaParam != null && !diaParam.IsReadOnly)
                    diaParam.Set(diameterFt);
            }
        }
    }
}

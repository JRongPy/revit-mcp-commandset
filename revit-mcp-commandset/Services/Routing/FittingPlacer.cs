using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMCPCommandSet.Services.Routing
{
    /// <summary>
    /// 放置彎頭、Tee/Takeoff 的具體實作（MVP 版）
    /// - CreateBranchAt：在幹管 host 的 projPt 位置建立分支管，並以 Tee/Takeoff 連接
    /// </summary>
    public static class FittingPlacer
    {
        /// <summary>
        /// 在幹管 host 的 projPt 位置建立分支。
        /// pref = "Takeoff" → 以 Takeoff 吸附；"Tee" → 切斷後以 Tee 連接。
        /// 回傳：新建的分支 Pipe（供後續繼續佈管使用）。
        /// 注意：需在 Transaction 內呼叫。
        /// </summary>
        public static Pipe CreateBranchAt(
            Document doc, RoutingContext ctx, Pipe host, XYZ projPt, bool isStart, string pref)
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

            // 2) 取得幹管方向（曲線切向）
            var der = curve.ComputeDerivatives(0.5, true);
            var hostDir = der?.BasisX?.Normalize() ?? (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();

            // 3) 選擇分支方向：盡量取水平面內與幹管正交
            var branchDir = SafePerpUnit(hostDir);
            // 仍可能有極端垂直邊界，最後再兜底
            if (branchDir.IsZeroLength()) branchDir = XYZ.BasisY;

            // 4) 建立一小段分支管（stub）
            double stubLen = 0.3; // ~0.3 ft ≈ 91 mm；可視需要調大（例如 0.5 ft）
            var branchStart = onCrv;
            var branchEnd = onCrv + branchDir.Multiply(stubLen);

            var branch = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, branchStart, branchEnd);
            SetPipeDiameter(branch, ctx.Diameter_ft);

            // 5) 依偏好策略與幹管連接
            string prefLower = pref?.ToLowerInvariant();
            if (prefLower == "tee")
            {
                // 5A) 以 Tee 連接：先把幹管在 onCrv 切斷
                //     Revit 會回傳新產生的另一段管的 ElementId
                ElementId newId = PlumbingUtils.BreakCurve(doc, host.Id, onCrv);
                var host2 = doc.GetElement(newId) as Pipe;

                // 取兩段幹管在切點附近的端頭接頭（各取一端）
                var cHostA = NearestEndConnector(host, onCrv);
                var cHostB = NearestEndConnector(host2, onCrv);

                // 分支管取「外端」接頭
                var cBranch = ConnectorUtils.GetPipeConnectors(branch)
                    .OrderByDescending(c => c.Origin.DistanceTo(onCrv)).FirstOrDefault();

                // 放三通
                doc.Create.NewTeeFitting(cHostA, cHostB, cBranch);
            }
            else
            {
                // 5B) 以 Takeoff 連接（預設）
                //     直接將分支外端接頭吸附到幹管
                var cBranch = ConnectorUtils.GetPipeConnectors(branch)
                    .OrderByDescending(c => c.Origin.DistanceTo(onCrv)).FirstOrDefault();

                // NewTakeoffFitting(connector, trunkCurve)
                doc.Create.NewTakeoffFitting(cBranch, host);
            }

            return branch;
        }

        // ====================== Helpers ======================

        /// <summary>
        /// 由幹管方向取「水平面上的正交單位向量」；盡量避免 Z 分量（保持水平）
        /// </summary>
        private static XYZ SafePerpUnit(XYZ hostDir)
        {
            // 先嘗試水平正交：hostDir × Z
            var perp = hostDir.CrossProduct(XYZ.BasisZ);
            if (!perp.IsZeroLength())
                return perp.Normalize();

            // 若幹管幾乎是垂直的 → 改用 X 或 Y 兜底
            perp = XYZ.BasisX.CrossProduct(hostDir);
            if (!perp.IsZeroLength())
                return perp.Normalize();

            perp = XYZ.BasisY.CrossProduct(hostDir);
            if (!perp.IsZeroLength())
                return perp.Normalize();

            return XYZ.BasisY; // 最終兜底
        }

        /// <summary>
        /// 設定管徑（英尺）
        /// </summary>
        private static void SetPipeDiameter(Pipe p, double diameterFt)
        {
            if (p == null || diameterFt <= 0) return;
            var diaParam = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diaParam != null && !diaParam.IsReadOnly)
                diaParam.Set(diameterFt);
        }

        /// <summary>
        /// 取得「最靠近某點」的端頭連接器（通常在 BreakCurve 切點附近）
        /// </summary>
        private static Connector NearestEndConnector(Pipe pipe, XYZ nearPoint)
        {
            return ConnectorUtils.GetPipeConnectors(pipe)
                .OrderBy(c => c.Origin.DistanceTo(nearPoint))
                .FirstOrDefault();
        }
    }
}

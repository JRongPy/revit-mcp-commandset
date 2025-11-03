// Services/Routing/RoutingServices.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils.Routing;
using System.IO;

namespace RevitMCPCommandSet.Services.Routing
{
    public static class RoutingServices
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

        public static string Pt(XYZ p) => p == null ? "null" : $"({p.X:F3},{p.Y:F3},{p.Z:F3})";

        // ================= 型別定義 ========================
        public enum ElementKind { Pipe, FamilyInstance, Other }

        // --- Step1: 分類
        public static ElementKind Classify(Element e)
        {
            var kind = (e is Pipe) ? ElementKind.Pipe :
                       (e is FamilyInstance) ? ElementKind.FamilyInstance :
                       ElementKind.Other;
            WriteLog($"[Classify] {e?.Id} -> {kind}");
            return kind;
        }

        // --- Step3: 推斷/取得必要資訊
        public static RoutingContext InferRoutingContext(Document doc, Element s, Element e, RouteTask task)
        {
            WriteLog($"[InferCtx][IN] S={s?.Id}, E={e?.Id}, Override?={(task?.Override != null)}");
            var ctx = new RoutingContext { Tolerance_ft = task.Tolerance_mm / 304.8 };

            try
            {
                // PipeType & Level & Diameter（優先從已存在管件）
                Pipe p = s as Pipe ?? e as Pipe ?? FindFirstNeighborPipe(doc, s, e);
                WriteLog($"[InferCtx] NeighborPipe={(p != null ? p.Id.ToString() : "null")}");

                if (p != null)
                {
                    ctx.PipeTypeId = p.PipeType?.Id;
                    ctx.LevelId = p.ReferenceLevel?.Id;
                    if (p.Diameter > 0) ctx.Diameter_ft = p.Diameter;
                }

                // SystemType
                if (p?.MEPSystem != null)
                {
                    ctx.SystemTypeId = p.MEPSystem.GetTypeId();
                }
                else
                {
                    ctx.SystemTypeId = FindSystemTypeIdFallback(doc);
                }

                // 若從 family 端取直徑
                if (ctx.Diameter_ft <= 0)
                {
                    var d = ConnectorUtils.TryGetConnectorDiameterFt(s);
                    if (d > 0) ctx.Diameter_ft = d;
                }

                // Level 補救
                if (ctx.LevelId == null || ctx.LevelId == ElementId.InvalidElementId)
                    ctx.LevelId = TryGetLevelId(doc, s);

                // Override 覆寫
                if (task.Override != null)
                {
                    if (task.Override.PipeTypeId.HasValue) ctx.PipeTypeId = new ElementId(task.Override.PipeTypeId.Value);
                    if (task.Override.SystemTypeId.HasValue) ctx.SystemTypeId = new ElementId(task.Override.SystemTypeId.Value);
                    if (task.Override.LevelId.HasValue) ctx.LevelId = new ElementId(task.Override.LevelId.Value);
                    if (task.Override.Diameter_mm.HasValue) ctx.Diameter_ft = task.Override.Diameter_mm.Value / 304.8;
                }

                if (ctx.PipeTypeId == null || ctx.SystemTypeId == null || ctx.LevelId == null)
                    throw new InvalidOperationException("無法推斷必要的 SystemType/PipeType/Level");

                if (ctx.Diameter_ft <= 0)
                    throw new InvalidOperationException("無法推斷管徑");

                WriteLog($"[InferCtx][OUT] SysType={ctx.SystemTypeId.IntegerValue}, PipeType={ctx.PipeTypeId.IntegerValue}, Level={ctx.LevelId.IntegerValue}, Dia={ctx.Diameter_ft * 304.8:F1}mm, Tol={ctx.Tolerance_ft * 304.8:F1}mm");
                return ctx;
            }
            catch (Exception ex)
            {
                WriteLog($"[InferCtx][ERR] {ex}");
                throw;
            }
        }
        // --- Step5: 產生世界座標路徑
        public static List<XYZ> BuildPathWorldPoints(XYZ start, List<JZPoint> mids, XYZ end)
        {
            var pts = new List<XYZ> { start };
            pts.AddRange(mids.Select(m => new XYZ(m.X / 304.8, m.Y / 304.8, m.Z / 304.8)));
            pts.Add(end);
            WriteLog($"[BuildPath] {string.Join(" -> ", pts.Select(Pt))}");
            return pts;
        }



        // --- Step6: 逐段建模與接頭
        public static IList<ElementId> CreateSegmentsAndFittings(
            Document doc, RoutingContext ctx, RoutingAnchor start, RoutingAnchor end,
            List<XYZ> pathPts, double minSegmentLen_ft, string routingPref, double tol_ft)
        {
            WriteLog($"[CreateSegments][IN] pts={pathPts?.Count ?? 0}, minLen={minSegmentLen_ft * 304.8:F1}mm, pref={routingPref}, tol={tol_ft * 304.8:F1}mm");
            var created = new List<ElementId>();
            var currentConnector = start.AnchorConnector;

            try
            {
                for (int i = 1; i < pathPts.Count; i++)
                {
                    var from = (i == 1) ? start.AnchorPoint : pathPts[i - 1];
                    var to = pathPts[i];
                    WriteLog($"[CreateSegments] Seg#{i} {Pt(from)} -> {Pt(to)}");

                    var segId = SegmentBuilder.CreatePipeSegmentAlignedOrBent(doc, ctx, currentConnector, from, to, minSegmentLen_ft, tol_ft, created);
                    WriteLog($"[CreateSegments] Seg#{i} LastPipeId={segId}");
                    Pipe seg = doc.GetElement(segId) as Pipe;
                    currentConnector = ConnectorUtils.GetFarConnector(seg, currentConnector.Origin);
                    WriteLog($"[CreateSegments] Seg#{i} NextConn@{Pt(currentConnector?.Origin)}");
                }

                WriteLog($"[CreateSegments] ConnectToEnd pref={routingPref}, EndKind={end.Kind}, EndAnchor={Pt(end.AnchorPoint)}");
                SegmentBuilder.ConnectToTargetEnd(doc, ctx, currentConnector, end, created, tol_ft);

                WriteLog($"[CreateSegments][OUT] Created={string.Join(",", created.Select(x => x))}");
                return created;
            }
            catch (Exception ex)
            {
                WriteLog($"[CreateSegments][ERR] {ex}");
                throw;
            }
        }

        /// <summary>
        /// 找出與起/訖其中之一「最近的」或「已連接的」第一支 Pipe。
        /// 先從元素的 Connector 出發找相連管；不行再退而求其次找最近的管（幾何距離）。
        /// </summary>
        public static Pipe FindFirstNeighborPipe(Document doc, Element s, Element e)
        {
            // 1) 從 s 的 connectors 追相鄰管
            var p = FindNeighborPipeFromElement(s);
            if (p != null) return p;

            // 2) 從 e 的 connectors 追相鄰管
            p = FindNeighborPipeFromElement(e);
            if (p != null) return p;

            // 3) 幾何最近（s / e → 最近的管）
            var cand = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>()
                .ToList();

            XYZ ps = GetOrigin(s);
            XYZ pe = GetOrigin(e);
            if (cand.Count == 0) return null;

            Pipe nearestToS = cand.OrderBy(pi => DistanceToCurve(ps, pi)).FirstOrDefault();
            Pipe nearestToE = cand.OrderBy(pi => DistanceToCurve(pe, pi)).FirstOrDefault();

            // 選兩者中更近的
            double ds = (nearestToS != null) ? DistanceToCurve(ps, nearestToS) : double.MaxValue;
            double de = (nearestToE != null) ? DistanceToCurve(pe, nearestToE) : double.MaxValue;
            return ds <= de ? nearestToS : nearestToE;

            // local helpers
            Pipe FindNeighborPipeFromElement(Element el)
            {
                foreach (var c in ConnectorUtils.GetConnectors(el))
                {
                    foreach (Connector refc in c.AllRefs.Cast<Connector>())
                    {
                        if (refc?.Owner is Pipe pp) return pp;
                    }
                }
                return null;
            }
            double DistanceToCurve(XYZ p0, Pipe pi)
            {
                var lc = pi.Location as LocationCurve;
                var crv = lc?.Curve;
                if (crv == null) return double.MaxValue;
                var proj = crv.Project(p0);
                return (proj == null) ? double.MaxValue : proj.Distance;
            }
        }
        /// <summary>
        /// 從文件中找一個可用的 Piping 系統型別。優先：PipingSystemType；否則從現有 Pipe 的 MEPSystem 推回。
        /// </summary>
        public static ElementId FindSystemTypeIdFallback(Document doc)
        {
            var sys = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault();

            if (sys != null) return sys.Id;

            // 退回：隨機找一支管的 system type
            var anyPipe = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>()
                .FirstOrDefault();

            if (anyPipe?.MEPSystem != null)
                return anyPipe.MEPSystem.GetTypeId();

            return ElementId.InvalidElementId;
        }

        
        /// <summary>
        /// 嘗試找 Level：Pipe → ReferenceLevel；FamilyInstance → LevelId；其他 → 取 bbox.Z 接近的 Level。
        /// </summary>
        public static ElementId TryGetLevelId(Document doc, Element e)
        {
            if (e is Pipe p && p.ReferenceLevel != null) return p.ReferenceLevel.Id;
            if (e is FamilyInstance fi && fi.LevelId != null && fi.LevelId != ElementId.InvalidElementId) return fi.LevelId;

            var bb = e.get_BoundingBox(null);
            if (bb != null)
            {
                double z = ((bb.Min + bb.Max) * 0.5).Z;
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                if (levels.Count > 0)
                {
                    var near = levels.OrderBy(lv => Math.Abs(lv.Elevation - z)).First();
                    return near.Id;
                }
            }

            // fallback：抓第一個 level
            var any = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
            return any?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>
        /// 元素的代表性原點：LocationPoint / LocationCurve 中點 / bbox 中心
        /// </summary>
        public static XYZ GetOrigin(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            if (e?.Location is LocationCurve lc)
            {
                var c = lc.Curve;
                return c?.Evaluate(0.5, true) ?? XYZ.Zero;
            }
            var bbox = e?.get_BoundingBox(null);
            return (bbox != null) ? (bbox.Min + bbox.Max) * 0.5 : XYZ.Zero;
        }
        public static XYZ GetLocationEnd(Pipe p, bool isStart)
            => (p.Location as LocationCurve).Curve.GetEndPoint(isStart ? 0 : 1);

        public static double ToMM(double ft) => ft * 304.8;


        // 路徑正規化：去重、去超短段、去共線中繼點；若全都重合 → 留單點
        public static List<XYZ> NormalizePathPoints(List<XYZ> pts, double tol_ft, double angTolDeg)
        {
            if (pts == null || pts.Count == 0) return new List<XYZ>();

            // 1) 連續重複點壓縮（依距離閾值）
            var dedup = new List<XYZ>();
            foreach (var p in pts)
            {
                if (dedup.Count == 0 || !NearlyEqual(dedup[^1], p, tol_ft))
                    dedup.Add(p);
            }
            // 若最後一點和第一點也重合，但只有兩點，保留一點
            if (dedup.Count == 2 && NearlyEqual(dedup[0], dedup[1], tol_ft))
                dedup = new List<XYZ> { dedup[0] };

            // 2) 如果全部點都重合，直接回傳單點
            if (AllNearEqual(dedup, dedup[0], tol_ft))
                return new List<XYZ> { dedup[0] };

            // 3) 移除很短的段（把造成 0 長度的中繼點清掉）
            var shortClean = new List<XYZ> { dedup[0] };
            for (int i = 1; i < dedup.Count; i++)
            {
                if (!NearlyEqual(shortClean[^1], dedup[i], tol_ft))
                    shortClean.Add(dedup[i]);
            }

            if (shortClean.Count <= 2) return shortClean;

            // 4) 移除共線中繼點（夾角小於角度容忍；含反向）
            double cosTol = Math.Cos(angTolDeg * Math.PI / 180.0);
            var result = new List<XYZ> { shortClean[0] };
            for (int i = 1; i < shortClean.Count - 1; i++)
            {
                var a = result[^1];
                var b = shortClean[i];
                var c = shortClean[i + 1];

                var v1 = (b - a);
                var v2 = (c - b);
                if (IsZero(v1, tol_ft) || IsZero(v2, tol_ft))
                {
                    // 有 0 長度向量，b 直接丟掉
                    continue;
                }

                var n1 = v1.Normalize();
                var n2 = v2.Normalize();
                var dot = Math.Abs(n1.DotProduct(n2)); // 取絕對值：0° 或 180° 都視為同一直線
                if (dot >= cosTol)
                {
                    // a-b-c 幾乎共線，b 為冗點 → 丟掉
                    continue;
                }

                result.Add(b);
            }
            result.Add(shortClean[^1]);

            // 5) 如果處理完只剩 1 點或 2 點即已充分；2 點相等再壓成 1 點
            if (result.Count == 2 && NearlyEqual(result[0], result[1], tol_ft))
                return new List<XYZ> { result[0] };

            return result;

            // ==== helpers ====
            static bool NearlyEqual(XYZ p1, XYZ p2, double tol) => p1.DistanceTo(p2) <= tol;
            static bool IsZero(XYZ v, double tol) => v.GetLength() <= tol * 0.5; // 更嚴一點避免浮點誤差累積
            static bool AllNearEqual(List<XYZ> list, XYZ refPt, double tol)
            {
                foreach (var p in list)
                    if (p.DistanceTo(refPt) > tol) return false;
                return true;
            }
        }
    }
}

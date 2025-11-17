// Services/Routing/RoutingServices.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Routing;
using RevitMCPSDK.API.Interfaces;
using System.IO;

namespace RevitMCPCommandSet.Services.Routing
{
    public static class RoutingServices
    {
        private static readonly Logger _logger = new Logger();
        public static string Pt(XYZ p) => p == null ? "null" : $"({p.X:F3},{p.Y:F3},{p.Z:F3})";

        // ================= 型別定義 ========================
        public enum ElementKind { Pipe, FamilyInstance, Other }

        // --- Step1: 分類
        public static ElementKind Classify(Element e)
        {
            var kind = (e is Pipe) ? ElementKind.Pipe :
                       (e is FamilyInstance) ? ElementKind.FamilyInstance :
                       ElementKind.Other;
            _logger.Info($"[Classify] {e?.Id} -> {kind}");
            return kind;
        }

        // --- Step3: 推斷/取得必要資訊
        public static RoutingContext InferRoutingContext(Document doc, Element s, Element e, RouteTaskInfo task)
        {
            _logger.Info($"[InferCtx][IN] S={s?.Id}, E={e?.Id}, Override?={(task?.Override != null)}");
            var ctx = new RoutingContext { 
                Tolerance_ft = task.ToleranceMm / 304.8, 
                MinSegmentLength_ft = task.MinSegmentLengthMm / 304.8,
                Tolerance_deg = task.ToleranceDeg};

            try
            {
                // 從start 端取得資訊，不足再從 end 端補 
                var startFreeConn = ConnectorUtils.GetSingleFreeEndConnector(s);
                if (startFreeConn != null)
                {
                    // SystemType（優先以 start 端 free connector 所屬系統）
                    try
                    { 
                        ctx.SystemTypeId = startFreeConn.MEPSystem?.GetTypeId(); 
                    } 
                    catch 
                    { 
                        ctx.SystemTypeId = FindSystemTypeIdFallback(doc); 
                    }
                }
                ctx.LevelId = s.LevelId;
                ctx.Diameter_ft = ConnectorUtils.TryGetConnectorDiameterFt(s);


                Pipe p = s as Pipe ?? e as Pipe ?? FindConnectedPipe(s) ?? FindConnectedPipe(e);
                _logger.Info($"[InferCtx] NeighborPipe={(p != null ? p.Id.ToString() : "null")}");

                if (p != null)
                {
                    ctx.PipeTypeId = p.PipeType?.Id;
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
                    if (task.Override.DiameterMm.HasValue) ctx.Diameter_ft = task.Override.DiameterMm.Value / 304.8;
                }

                if (ctx.PipeTypeId == null || ctx.SystemTypeId == null || ctx.LevelId == null)
                    throw new InvalidOperationException("無法推斷必要的 SystemType/PipeType/Level");

                if (ctx.Diameter_ft <= 0)
                    throw new InvalidOperationException("無法推斷管徑");

                _logger.Info($"[InferCtx][OUT] SysType={ctx.SystemTypeId}, PipeType={ctx.PipeTypeId}, Level={ctx.LevelId}, Dia={ctx.Diameter_ft * 304.8:F1}mm, Tol={ctx.Tolerance_ft * 304.8:F1}mm");
                return ctx;
            }
            catch (Exception ex)
            {
                _logger.Error($"[InferCtx][ERR] {ex}");
                throw;
            }
        }

        /// <summary>
        /// Infer waypoint(s) when task has none.
        /// Rules:
        /// - Pipe vs Pipe (or any MEPCurve): use midpoint of nearest connector pair
        ///   (parallel, non-parallel, or colinear all fallback to nearest-connectors midpoint).
        /// - FamilyInstance:
        ///   * If both ends free OR both ends connected => throw (ambiguous).
        ///   * If exactly one free connector => push forward a small offset along its direction.
        /// - Mixed cases (Pipe/MEPCurve vs FamilyInstance): midpoint of nearest connectors.
        /// </summary>
        public static List<XYZ> InferWaypointsIfEmpty(
            Document doc,
            Element startEle,
            Element endEle,
            RoutingContext ctx
        )
        {
            var result = new List<XYZ>();
            var startCons = ConnectorUtils.GetConnectors(startEle).ToList();
            var endCons = ConnectorUtils.GetConnectors(endEle).ToList();

            if (!startCons.Any())
                throw new InvalidOperationException($"Start element {startEle.Id} has no MEP connectors.");
            if (!endCons.Any())
                throw new InvalidOperationException($"End element {endEle.Id} has no MEP connectors.");

            bool startIsFI = startEle is FamilyInstance;
            bool endIsFI = endEle is FamilyInstance;
            
            // --- FamilyInstance validation rules ---
            if (startIsFI)
            {
                int free = startCons.Count(c => !c.IsConnected);
                if (free == 0 || free == startCons.Count)
                    throw new InvalidOperationException("FamilyInstance(start) connectors are either all connected or all free; ambiguous.");
            }
            if (endIsFI)
            {
                int free = endCons.Count(c => !c.IsConnected);
                if (free == 0 || free == endCons.Count)
                    throw new InvalidOperationException("FamilyInstance(end) connectors are either all connected or all free; ambiguous.");
            }



            // Small forward offset (at least MinSegmentLength)
            double forward_ft = ctx.MinSegmentLength_ft;

            if (startEle is Pipe sPipe && endEle is Pipe ePipe)
            {
                var pair = NearestPair(startCons, endCons);
                if (pair.a == null || pair.b == null)
                    throw new InvalidOperationException("Failed to find nearest connectors.");

                
                bool isPorjS = PipeUtils.TryProjectPointOnPipe(startEle as Pipe, pair.b.Origin, out var projS, out _, clampToSegment: true);
                bool isProjE = PipeUtils.TryProjectPointOnPipe(endEle as Pipe, pair.a.Origin, out var projE, out _, clampToSegment: true);
                if (!isPorjS || !isProjE)
                    throw new InvalidOperationException("Failed to find nearest connectors.");

                var mid = Mid(projS, projE);
                result.Add(mid);
            }
            else
            {
                // --- Case: FI with exactly one free-end => push forward along direction ---
                bool startOneFree = startIsFI && startCons.Count(c => !c.IsConnected) == 1;
                bool endOneFree = endIsFI && endCons.Count(c => !c.IsConnected) == 1;                  
                if (startOneFree)
                {
                    var free = startCons.First(c => !c.IsConnected);
                    var dir = ConnectorUtils.GetConnectorDirection(free);
                    result.Add(free.Origin + dir.Multiply(forward_ft));
                }
                if (endOneFree)
                {
                    var free = endCons.First(c => !c.IsConnected);
                    var dir = ConnectorUtils.GetConnectorDirection(free);
                    result.Add(free.Origin + dir.Multiply(forward_ft));
                }
            }
            return result;
        }

        // --- Helper: nearest connector pair across two elements ---
        private static (Connector a, Connector b, double d) NearestPair(List<Connector> A, List<Connector> B)
        {
            Connector bestA = null, bestB = null;
            double best = double.MaxValue;
            foreach (var ca in A)
                foreach (var cb in B)
                {
                    var d = ca.Origin.DistanceTo(cb.Origin);
                    if (d < best)
                    {
                        best = d; bestA = ca; bestB = cb;
                    }
                }
            return (bestA, bestB, best);
        }

        /// <summary>
        /// Midpoint of two XYZ points.
        /// </summary>
        private static XYZ Mid(XYZ a, XYZ b) => new XYZ(
            0.5 * (a.X + b.X),
            0.5 * (a.Y + b.Y),
            0.5 * (a.Z + b.Z)
        );

        // --- Step5: 產生世界座標路徑
        public static List<XYZ> BuildPathWorldPoints(XYZ start, List<JZPoint> mids, XYZ end)
        {
            var pts = new List<XYZ> { start };
            pts.AddRange(mids.Select(m => new XYZ(m.X / 304.8, m.Y / 304.8, m.Z / 304.8)));
            pts.Add(end);
            _logger.Info($"[BuildPath][Oirgin] {string.Join(" -> ", pts.Select(Pt))}");
            return pts;
        }



        // --- Step6: 逐段建模與接頭
        public static IList<ElementId> CreateSegmentsAndFittings(
            Document doc, RoutingContext ctx, RoutingAnchor start, RoutingAnchor end,
            List<XYZ> pathPts, double minSegmentLen_ft, string routingPref, double tol_ft)
        {
            _logger.Info($"[CreateSegments][IN] pts={pathPts?.Count ?? 0}, minLen={minSegmentLen_ft * 304.8:F1}mm, pref={routingPref}, tol={tol_ft * 304.8:F1}mm");
            var created = new List<ElementId>();
            var currentConnector = start.AnchorConnector;

            try
            {
                for (int i = 1; i < pathPts.Count; i++)
                {
                    var from = (i == 1) ? start.AnchorPoint : pathPts[i - 1];
                    var to = pathPts[i];
                    _logger.Info($"[CreateSegments] Seg#{i} {Pt(from)} -> {Pt(to)}");

                    var segId = SegmentBuilder.CreatePipeSegmentAlignedOrBent(doc, ctx, currentConnector, from, to, minSegmentLen_ft, tol_ft, created);
                    _logger.Info($"[CreateSegments] Seg#{i} LastPipeId={segId}");
                    Pipe seg = doc.GetElement(segId) as Pipe;
                    currentConnector = ConnectorUtils.GetNearConnector(seg, to);
                    _logger.Info($"[CreateSegments] Seg#{i} NextConn@{Pt(currentConnector?.Origin)}");
                }

                _logger.Info($"[CreateSegments] ConnectToEnd pref={routingPref}, EndKind={end.Kind}, EndAnchor={Pt(end.AnchorPoint)}");
                PipeUtils.TryCreateElbow(doc, currentConnector.Owner as Pipe, end.AnchorElement as Pipe, end.AnchorPoint);

                _logger.Info($"[CreateSegments][OUT] Created={string.Join(",", created.Select(x => x))}");
                return created;
            }
            catch (Exception ex)
            {
                _logger.Info($"[CreateSegments][ERR] {ex}");
                throw;
            }
        }

        /// <summary>
        /// 只沿著 Connector 網找第一支相連的 Pipe；超過 maxDepth 找不到就回傳 null。
        /// - 僅查詢、不需 Transaction
        /// - systemTypeId 可為 null 表示不過濾系統型別
        /// - 會略過 Free connectors，並避免循環
        /// </summary>
        public static Pipe FindConnectedPipe(
            Element start,
            ElementId systemTypeId = null,
            int maxDepth = 6
        )
        {
            if (start == null) return null;

            var q = new Queue<(Element el, int depth)>();
            var visited = new HashSet<ElementId>();
            if (start.Id != ElementId.InvalidElementId) visited.Add(start.Id);
            q.Enqueue((start, 0));

            while (q.Count > 0)
            {
                var (el, depth) = q.Dequeue();
                if (depth > maxDepth) continue;

                foreach (var c in ConnectorUtils.GetConnectors(el))
                {
                    // 只在「已連接」的端點上擴張
                    if (!c.IsConnected) continue;

                    foreach (Connector refc in c.AllRefs.Cast<Connector>())
                    {
                        if (refc == null) continue;
                        var owner = refc.Owner;
                        if (owner == null) continue;

                        // 命中 Pipe
                        if (owner is Pipe pipe)
                        {
                            if (systemTypeId == null || systemTypeId == ElementId.InvalidElementId)
                                return pipe;

                            var pid = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
                            if (pid == systemTypeId) return pipe;

                            // 若系統不符，繼續擴張（因為可能跨配件後才接到目標系統）
                        }

                        // 非 Pipe：向外擴張，但避免循環
                        if (!visited.Contains(owner.Id))
                        {
                            visited.Add(owner.Id);
                            q.Enqueue((owner, depth + 1));
                        }
                    }
                }
            }
            // 超過 maxDepth 或找不到
            return null;
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

        }

        /// <summary>
        /// 把與起點/終點重疊（在 tol_mm 以內）的 waypoint 移除
        /// </summary>
        public static void RemoveNearEndpointsInPlace(List<XYZ> ftPts, XYZ startFt, XYZ endFt, double tol_ft)
        {
            if (ftPts == null || ftPts.Count == 0) return;
            ftPts.RemoveAll(p => NearlyEqual(p, startFt, tol_ft) || NearlyEqual(p, endFt, tol_ft));
        }

        //========== helpers =============
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

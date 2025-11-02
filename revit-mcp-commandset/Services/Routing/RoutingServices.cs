// Services/Routing/RoutingServices.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Models.Common;
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
        private static string Ptm(JZPoint p) => p == null ? "null" : $"({p.X:F1},{p.Y:F1},{p.Z:F1})mm";

        // ================= 型別定義 ========================
        public enum ElementKind { Pipe, FamilyInstance, Other }

        public class AttachPoint
        {
            public Connector Connector;     // 若最後落在元件的 connector
            public XYZ AnchorPoint;         // 真的要從哪裡出發/終止（可能是管上投影點）
            public Element HostElement;     // 對應元素（管或族）
            public ElementKind Kind;
            /// <summary>
            /// 依 AnchorPoint 重新抓取最合理的 Connector，並更新本物件的 Connector 欄位。
            /// 回傳取得的 Connector；若取不到則回傳 null。
            /// </summary>
            public Connector RefreshConnector()
            {
                if (HostElement == null || AnchorPoint == null) return null;
                try
                {
                    switch (Kind)
                    {
                        case RoutingServices.ElementKind.Pipe:
                            {
                                var pipe = HostElement as Autodesk.Revit.DB.Plumbing.Pipe;
                                var conns = ConnectorUtils.GetPipeConnectors(pipe);
                                var nearest = conns
                                    .OrderBy(c => c.Origin.DistanceTo(AnchorPoint))
                                    .FirstOrDefault();
                                Connector = nearest;
                                return Connector;
                            }
                        case RoutingServices.ElementKind.FamilyInstance:
                            {
                                var fi = HostElement as FamilyInstance;
                                if (fi == null) break;

                                var nearest = ConnectorUtils.FindNearestConnector(fi, AnchorPoint);

                                Connector = nearest;
                                return Connector;

                            }
                    }
                }
                catch
                {
                    // ignore & fall through to return null
                }
                return null;
            }
        }

        // --- Step1: 分類
        public static ElementKind Classify(Element e)
        {
            var kind = (e is Pipe) ? ElementKind.Pipe :
                       (e is FamilyInstance) ? ElementKind.FamilyInstance :
                       ElementKind.Other;
            WriteLog($"[Classify] {e?.Id} -> {kind}");
            return kind;
        }

        // --- Step2: family 必須能提供連接器
        public static void EnsureHasConnectors(FamilyInstance fi)
        {
            var has = fi?.MEPModel?.ConnectorManager?.Connectors?.Cast<Connector>()?.Any() ?? false;
            WriteLog($"[EnsureHasConnectors] FI:{fi?.Id} Has={has}");
            if (!has) throw new InvalidOperationException($"族 {fi.Id.Value} 無 MEP 連接器");
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
                    var d = TryGetConnectorDiameterFt(s);
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

        // --- Step4: 解析 AttachPoint（含：就近 connector？或管上投影+Tee/Takeoff）
        public static AttachPoint ResolveAttachPoint(Document doc, Element host, RouteTask task, bool isStart, RoutingContext ctx)
        {
            WriteLog($"[ResolveAttach][IN] Host={host?.Id.IntegerValue}, Kind={Classify(host)}, isStart={isStart}");
            var kind = Classify(host);

            try
            {
                if (kind == ElementKind.FamilyInstance)
                {
                    var wpt = (isStart ? task.Waypoints.FirstOrDefault() : task.Waypoints.LastOrDefault());
                    var target = wpt == null ? GetOrigin(host) : new XYZ(wpt.X / 304.8, wpt.Y / 304.8, wpt.Z / 304.8);

                    var nearest = ConnectorUtils.FindNearestConnector((FamilyInstance)host, target);
                    if (nearest == null) throw new InvalidOperationException($"找不到 {host.Id.IntegerValue} 的接點");

                    var ap = new AttachPoint { Connector = nearest, AnchorPoint = nearest.Origin, HostElement = host, Kind = kind };
                    WriteLog($"[ResolveAttach][FI] Conn@{Pt(nearest.Origin)}");
                    return ap;
                }
                else if (kind == ElementKind.Pipe)
                {
                    var pipe = (Pipe)host;
                    var wpt = (isStart ? task.Waypoints.FirstOrDefault() : task.Waypoints.LastOrDefault());
                    if (wpt == null)
                    {
                        var endPt = GetLocationEnd(pipe, isStart);
                        wpt = new JZPoint { X = ToMM(endPt.X), Y = ToMM(endPt.Y), Z = ToMM(endPt.Z) };
                    }

                    var p = new XYZ(wpt.X / 304.8, wpt.Y / 304.8, wpt.Z / 304.8);
                    var curve = (host.Location as LocationCurve).Curve;
                    var proj = curve.Project(p);
                    var projPt = proj?.XYZPoint ?? curve.Evaluate(0.5, true);
                    WriteLog($"[ResolveAttach][Pipe] Wpt={Ptm(wpt)} -> Proj={Pt(projPt)}");

                    // 近端 connector 直接用
                    var pipeConn = ConnectorUtils.GetPipeConnectors(pipe);
                    var nearConn = pipeConn?.OrderBy(c => c.Origin.DistanceTo(projPt)).FirstOrDefault();
                    if (nearConn != null && nearConn.Origin.DistanceTo(projPt) <= ctx.Tolerance_ft)
                    {
                        var ap = new AttachPoint { Connector = nearConn, AnchorPoint = nearConn.Origin, HostElement = host, Kind = kind };
                        WriteLog($"[ResolveAttach][Pipe] UseNearConn Dist={nearConn.Origin.DistanceTo(projPt):F4}ft");
                        return ap;
                    }

                    // 否則分支（注意：需在 Transaction 內呼叫本方法）
                    if (!doc.IsModifiable)
                        WriteLog("[ResolveAttach][WARN] CreateBranchAt called when doc not modifiable (ensure transaction started before calling).");

                    var branch = FittingPlacer.CreateBranchAt(doc, ctx, pipe, projPt, isStart, "Tee");
                    var ap2 = new AttachPoint { Connector = ConnectorUtils.GetSingleFreeEndConnector(branch), AnchorPoint = ConnectorUtils.GetFreeEndPoint(branch), HostElement = branch, Kind = ElementKind.Pipe };
                    WriteLog($"[ResolveAttach][Pipe] BranchCreated BranchId={branch?.Id.IntegerValue}, Anchor={Pt(ap2.AnchorPoint)}");
                    return ap2;
                }

                throw new InvalidOperationException("僅支援 Pipe / FamilyInstance 作為起訖端");
            }
            catch (Exception ex)
            {
                WriteLog($"[ResolveAttach][ERR] Host={host?.Id.IntegerValue} {ex}");
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
            Document doc, RoutingContext ctx, AttachPoint start, AttachPoint end,
            List<XYZ> pathPts, double minSegmentLen_ft, string routingPref, double tol_ft)
        {
            WriteLog($"[CreateSegments][IN] pts={pathPts?.Count ?? 0}, minLen={minSegmentLen_ft * 304.8:F1}mm, pref={routingPref}, tol={tol_ft * 304.8:F1}mm");
            var created = new List<ElementId>();
            var currentConnector = start.Connector;

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
                    currentConnector = ConnectorUtils.GetFarEndConnector(seg, currentConnector.Origin);
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
        /// 嘗試從元素取得可用的「圓管直徑（ft）」。Pipe → p.Diameter；FamilyInstance → 取圓形 connector 的 Radius*2 最大值。
        /// </summary>
        public static double TryGetConnectorDiameterFt(Element e)
        {
            if (e is Pipe p && p.Diameter > 0) return p.Diameter;

            double best = 0.0;
            foreach (var c in ConnectorUtils.GetConnectors(e))
            {
                try
                {
                    // 圓形管徑：Radius（ft）
                    double d = 2.0 * c.Radius;
                    if (d > best) best = d;
                }
                catch { /* 某些 connector 可能不是 round */ }
            }
            return best;
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
      

        /// <summary>
        /// 若起/終點為 Pipe，且第一/最後一個中繼點（waypoint）與端點 connector 方向近似同向，
        /// 則把該 waypoint 吸收為新的 AnchorPoint，並從 pathPts 中移除，避免後續再特判。
        /// </summary>
        public static void NormalizeMidsByEndpointDirection(
            AttachPoint start, AttachPoint end, List<XYZ> pathPts,
            double angTolDeg, double tol_ft=0.001)
        {
            if (pathPts == null || pathPts.Count < 3) return; // 至少要有 start, mid, end

            // 角度/距離門檻
            double minSpan = Math.Max(tol_ft * 2.0, 1e-4);
            double angTol = Deg2Rad(angTolDeg);

            // ---------- 起點側：若起點為 Pipe，且第一個 mid 與起點方向同向，吸收該 mid ----------
            if (start?.Kind == ElementKind.Pipe && start.Connector != null)
            {
                var firstMid = pathPts[1];                 // pathPts[0] 是 start.AnchorPoint
                var v = firstMid - start.AnchorPoint;

                if (v.GetLength() >= minSpan)
                {
                    var d = start.Connector.CoordinateSystem.BasisZ;
                    if (!d.IsZeroLength())
                    {
                        double ang = AngleBetween(d, v);   // 同向
                        if (ang <= angTol)
                        {
                            WriteLog($"[Normalize][Start] absorb mid {Pt(firstMid)} as new start.AnchorPoint");
                            // 吸收：把起點 AnchorPoint 前推到 firstMid，並移除該 mid
                            start.AnchorPoint = firstMid;
                            start.Connector.Origin = firstMid;
                            pathPts.RemoveAt(0);
                        }
                    }
                }
            }

            // ---------- 終點側：若終點為 Pipe，且最後一個 mid 與終點方向同向，吸收該 mid ----------
            // 注意：pathPts 可能已在上面移除第一個 mid，所以重新取末端 index
            if (end?.Kind == ElementKind.Pipe && end.Connector != null)
            {
                int lastMidIdx = pathPts.Count - 2;
                var lastMid = pathPts[lastMidIdx];
                var v = lastMid - end.AnchorPoint;

                if (v.GetLength() >= minSpan)
                {
                    var d = end.Connector.CoordinateSystem.BasisZ;
                    if (!d.IsZeroLength())
                    {
                        double ang = AngleBetween(d, v);   // 同向（因為我們會從終點往外拉到 lastMid）
                        if (ang <= angTol)
                        {
                            WriteLog($"[Normalize][End] absorb mid {Pt(lastMid)} as new end.AnchorPoint");
                            // 吸收：把終點 AnchorPoint 前推到 lastMid，並移除該 mid
                            end.AnchorPoint = lastMid;
                            end.Connector.Origin = lastMid;
                            pathPts.RemoveAt(lastMidIdx+1);
                        }
                    }
                }
            }
        }

        private static double AngleBetween(XYZ a, XYZ b)
        {
            if (a == null || b == null) return Math.PI;
            if (a.IsZeroLength() || b.IsZeroLength()) return Math.PI;

            var a1 = a.Normalize();
            var b1 = b.Normalize();
            double dot = a1.DotProduct(b1);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            return Math.Acos(dot);
        }
        private static double Deg2Rad(double d) => d * Math.PI / 180.0;

    }
}

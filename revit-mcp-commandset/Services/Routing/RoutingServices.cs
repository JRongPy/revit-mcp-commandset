// Services/Routing/RoutingServices.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services.Routing
{
    public static class RoutingServices
    {
        public enum ElementKind { Pipe, FamilyInstance, Other }

        public class RoutingContext
        {
            public ElementId SystemTypeId;
            public ElementId PipeTypeId;
            public ElementId LevelId;
            public double Diameter_ft;
            public double Tolerance_ft;
        }

        public class AttachPoint
        {
            public Connector Connector;     // 若最後落在元件的 connector
            public XYZ AnchorPoint;         // 真的要從哪裡出發/終止（可能是管上投影點）
            public Element HostElement;     // 對應元素（管或族）
            public ElementKind Kind;
        }

        // --- Step1: 分類
        public static ElementKind Classify(Element e)
        {
            if (e is Pipe) return ElementKind.Pipe;
            if (e is FamilyInstance) return ElementKind.FamilyInstance;
            return ElementKind.Other;
        }

        // --- Step2: family 必須能提供連接器
        public static void EnsureHasConnectors(FamilyInstance fi)
        {
            var mep = fi.MEPModel;
            if (mep == null || !mep.ConnectorManager.Connectors.Cast<Connector>().Any())
                throw new InvalidOperationException($"族 {fi.Id.IntegerValue} 無 MEP 連接器");
        }

        // --- Step3: 推斷/取得必要資訊
        public static RoutingContext InferRoutingContext(Document doc, Element s, Element e, RouteTask task)
        {
            // 1) SystemTypeId: 從起點元素抓（若是管/或其系統），否則查拓樸第一個管
            // 2) PipeTypeId:   若任一端是管 -> 用該管的 PipeType
            // 3) LevelId:      取起點 Level
            // 4) Diameter:     優先取起點 connector 直徑；task.override 可覆寫
            // （此處示意，實作時請把取得系統與拓樸的邏輯封裝完整）

            var ctx = new RoutingContext { Tolerance_ft = task.Tolerance_mm / 304.8 };

            // PipeType
            Pipe p = s as Pipe ?? e as Pipe ?? FindFirstNeighborPipe(doc, s, e);
            if (p != null)
            {
                ctx.PipeTypeId = p.PipeType.Id;
                ctx.LevelId = p.ReferenceLevel.Id;
                if (p.Diameter > 0) ctx.Diameter_ft = p.Diameter;
            }

            // SystemType
            if (p != null && p.MEPSystem != null)
                ctx.SystemTypeId = p.MEPSystem.GetTypeId();
            else
                ctx.SystemTypeId = FindSystemTypeIdFallback(doc); // TODO: 依你的專案預設

            // connector 直徑（若起點為 family）
            if (ctx.Diameter_ft <= 0)
            {
                var d = TryGetConnectorDiameterFt(s);
                if (d > 0) ctx.Diameter_ft = d;
            }

            // Level（若沒從管取到）
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

            return ctx;
        }

        // --- Step4: 解析 AttachPoint（含：就近 connector？或管上投影+Tee/Takeoff）
        public static AttachPoint ResolveAttachPoint(Document doc, Element host, RouteTask task, bool isStart, RoutingContext ctx)
        {
            var kind = Classify(host);

            if (kind == ElementKind.FamilyInstance)
            {
                var wpt = (isStart ? task.Waypoints.FirstOrDefault() : task.Waypoints.LastOrDefault());
                var target = wpt == null ? GetOrigin(host) : new XYZ(wpt.X / 304.8, wpt.Y / 304.8, wpt.Z / 304.8);

                var nearest = FindNearestConnector((FamilyInstance)host, target);
                if (nearest == null) throw new InvalidOperationException($"找不到 {host.Id.IntegerValue} 的接點");

                return new AttachPoint { Connector = nearest, AnchorPoint = nearest.Origin, HostElement = host, Kind = kind };
            }
            else if (kind == ElementKind.Pipe)
            {
                var wpt = (isStart ? task.Waypoints.FirstOrDefault() : task.Waypoints.LastOrDefault());
                if (wpt == null) wpt = new JZPoint { X = ToMM(GetLocationEnd((Pipe)host, isStart).X), Y = ToMM(GetLocationEnd((Pipe)host, isStart).Y), Z = ToMM(GetLocationEnd((Pipe)host, isStart).Z) };

                var p = new XYZ(wpt.X / 304.8, wpt.Y / 304.8, wpt.Z / 304.8);
                var curve = ((host.Location as LocationCurve).Curve);
                var proj = curve.Project(p); // UV on curve
                var projPt = proj.XYZPoint;

                // 如果投影點與任一 connector 很近 -> 直接用該 connector
                var pipeConn = GetPipeConnectors((Pipe)host);
                var nearConn = pipeConn.OrderBy(c => c.Origin.DistanceTo(projPt)).FirstOrDefault();
                if (nearConn != null && nearConn.Origin.DistanceTo(projPt) <= ctx.Tolerance_ft)
                {
                    return new AttachPoint { Connector = nearConn, AnchorPoint = nearConn.Origin, HostElement = host, Kind = kind };
                }

                // 否則在投影處開 T / Takeoff，返回新管段端點作為 AnchorPoint
                var branch = CreateBranchAt(doc, ctx, (Pipe)host, projPt, isStart, /*routingPref*/ "Tee");
                return new AttachPoint { Connector = GetSingleFreeEndConnector(branch), AnchorPoint = GetFreeEndPoint(branch), HostElement = branch, Kind = ElementKind.Pipe };
            }
            throw new InvalidOperationException("僅支援 Pipe / FamilyInstance 作為起訖端");
        }

        // --- Step5: 產生世界座標的路徑（S -> P1..Pn -> E）
        public static List<XYZ> BuildPathWorldPoints(XYZ start, List<JZPoint> mids, XYZ end)
        {
            var pts = new List<XYZ> { start };
            pts.AddRange(mids.Select(m => new XYZ(m.X / 304.8, m.Y / 304.8, m.Z / 304.8)));
            pts.Add(end);
            return pts;
        }

        // --- Step6: 逐段建模與接頭
        public static IList<ElementId> CreateSegmentsAndFittings(
            Document doc, RoutingContext ctx, AttachPoint start, AttachPoint end,
            List<XYZ> pathPts, double minSegmentLen_ft, string routingPref, double tol_ft)
        {
            var created = new List<ElementId>();
            var currentConnector = start.Connector;

            for (int i = 1; i < pathPts.Count; i++)
            {
                var from = (i == 1) ? start.AnchorPoint : pathPts[i - 1];
                var to = pathPts[i];

                // 檢查方向是否與 currentConnector 方向一致，不一致時：補最短段＋彎頭
                var seg = CreatePipeSegmentAlignedOrBent(doc, ctx, currentConnector, from, to, minSegmentLen_ft, tol_ft, created);

                // 取得這段管的另一端 connector 當作下一段的出發點
                currentConnector = GetFarEndConnector(seg, currentConnector);
            }

            // 收尾：把最後段與 end 連起來（若 end 有 connector 就直接 Connect；否則在終點做彎頭/Tee）
            ConnectToTargetEnd(doc, ctx, currentConnector, end, created, tol_ft, routingPref);

            return created;
        }

        // ======== 下方為你可按專案實作的具體細節（省略具體碼，給出關鍵點） ========
        static Pipe FindFirstNeighborPipe(Document doc, Element s, Element e) { /* TODO */ return null; }
        static ElementId FindSystemTypeIdFallback(Document doc) { /* TODO */ return new ElementId(BuiltInParameter.INVALID); }
        static double TryGetConnectorDiameterFt(Element e) { /* TODO */ return 0; }
        static ElementId TryGetLevelId(Document doc, Element e) { /* TODO */ return ElementId.InvalidElementId; }
        static Connector FindNearestConnector(FamilyInstance fi, XYZ target) { /* TODO */ return null; }
        static XYZ GetOrigin(Element e) { /* TODO */ return XYZ.Zero; }
        static XYZ GetLocationEnd(Pipe p, bool isStart) { /* TODO */ return (p.Location as LocationCurve).Curve.GetEndPoint(isStart ? 0 : 1); }
        static double ToMM(double ft) => ft * 304.8;
        static IEnumerable<Connector> GetPipeConnectors(Pipe p) { /* TODO */ yield break; }
        static Pipe CreateBranchAt(Document doc, RoutingContext ctx, Pipe host, XYZ projPt, bool isStart, string pref) { /* TODO: 新開 Tee/Takeoff */ return null; }
        static Connector GetSingleFreeEndConnector(Pipe p) { /* TODO */ return null; }
        static XYZ GetFreeEndPoint(Pipe p) { /* TODO */ return XYZ.Zero; }
        static ElementId CreatePipeSegmentAlignedOrBent(Document doc, RoutingContext ctx, Connector fromConn, XYZ from, XYZ to, double minLen, double tol, List<ElementId> acc) { /* TODO: 延伸或 ㄇ 字補段 + 彎頭 */ return ElementId.InvalidElementId; }
        static Connector GetFarEndConnector(ElementId pipeId, Connector near) { /* TODO */ return null; }
        static void ConnectToTargetEnd(Document doc, RoutingContext ctx, Connector last, AttachPoint end, List<ElementId> acc, double tol, string pref) { /* TODO */ }
    }
}

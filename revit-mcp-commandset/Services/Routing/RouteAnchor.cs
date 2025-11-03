using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using RevitMCPCommandSet.Models.Common;
using static RevitMCPCommandSet.Services.Routing.RoutingServices;
using System.Threading.Tasks;
using System.Collections;
using RevitMCPCommandSet.Utils.Routing;

namespace RevitMCPCommandSet.Services.Routing
{
    /// <summary>
    /// RoutingAnchor：將任意「起/終點元素」正規化為「可從 Pipe 端點出發」的錨點。
    /// 建構流程（皆需在 Transaction 內呼叫）：
    /// 1) 依 isStart 取對應 waypoint 作為 target（start→首點；end→末點；若無 waypoint 則以另一端 Anchor 為 target）。
    /// 2) 若為 Pipe：
    ///    - 對幹管做投影；近端點且端點 free → 退化為端點；
    ///    - 否則在投影點開 Tee/Takeoff 分支，將分支遠端作為 Anchor。
    ///    若為 FamilyInstance：抓最近 Connector。
    /// 3) 若 connector 朝向與 (target-connector) 不一致 → 補一小段 stub 讓方向對齊。
    /// </summary>
    public class RoutingAnchor
    {
        public Connector AnchorConnector { get; private set; } // 正式用於後續連接的Connector
        public XYZ AnchorPoint { get; set; }  // 正式用於後續連接的位置，原則上是 AnchorConnector.Origin，但當點位調整時用來重新錨定新的 AnchorConnector
        public Element AnchorElement { get; private set; }  // 錨定物件，可能是 HostElement 也可能其他物件
        public Element HostElement { get; private set; } //輸入的主物件
        public ElementKind Kind { get; private set; }  // HostElement 的型別
        public List<ElementId> CreatedElementIds { get; } = new List<ElementId>();

        public ElementId PipeTypeId { get; }
        public ElementId SystemTypeId { get; }
        public ElementId LevelId { get; }
        public double Diameter_ft { get; }

        private readonly Document _doc;
        private readonly RoutingContext _ctx;
        private readonly double _tol_ft;
        private readonly double _tol_deg;
        private readonly string _routingPref;
        private readonly double _minSegLen_ft; // 最小管段長度（英尺）
        private readonly RouteTask _task;

        public RoutingAnchor(Document doc, Element host, RouteTask task, bool isStart, RoutingContext ctx)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _tol_ft = Math.Max(_ctx.Tolerance_ft, 1e-4);
            _task = task ?? throw new ArgumentNullException(nameof(task));
            _tol_deg = task.angleTolerance_deg;
            _minSegLen_ft = task.MinSegmentLength_mm / 304.8;
            _routingPref = task?.RoutingPreference ?? "Tee";

            HostElement = host ?? throw new ArgumentNullException(nameof(host));
            Kind = Classify(HostElement);

            PipeTypeId = ctx.PipeTypeId;
            SystemTypeId = ctx.SystemTypeId;
            LevelId = ctx.LevelId;
            Diameter_ft = ctx.Diameter_ft;

            BuildAnchorElement(isStart);
        }

        // ---- Public helpers -------------------------------------------------

        /// <summary>
        /// 更新 AnchorPoint或 HostElement 變動後，重取當前最近的 Connector。
        /// </summary>
        public Connector RefreshConnector()
        {
            switch (Kind)
            {
                case ElementKind.Pipe:
                    {
                        var p = HostElement as Pipe;
                        var conns = ConnectorUtils.GetConnectors(p);
                        AnchorConnector = conns?            
                            .OrderBy(c => c.Origin.DistanceTo(AnchorPoint))
                            .FirstOrDefault();
                        break;
                    }
                case ElementKind.FamilyInstance:
                    {
                        var fi = HostElement as FamilyInstance;
                        AnchorConnector = ConnectorUtils.GetNearConnector(fi, AnchorPoint);
                        break;
                    }
                default:
                    AnchorConnector = null;
                    break;
            }
            return AnchorConnector;
        }

        /// <summary>
        /// 建構AnchorElement與AnchorConnector
        /// </summary>
        private void BuildAnchorElement(bool isStart)
        {
            var targetPoint = ComputeTargetPoint(_task, isStart);
            // 依照類型建構 AnchorElement 並找出 AnchorConnector（Pipe → 端點或投影；FI → 最近 conn）
            switch (Kind)
            {
                case ElementKind.Pipe:
                    ResolveOnPipe(isStart, targetPoint);
                    break;
                case ElementKind.FamilyInstance:
                    ResolveOnFamily(targetPoint);
                    break;
                default:
                    throw new InvalidOperationException($"不支援的起訖元素型別：{HostElement?.GetType().Name}");
            }

            if (AnchorConnector == null)
                throw new InvalidOperationException("RoutingAnchor 無法取得有效 Connector");

        }

        /// <summary>
        /// HostElement 為 Pipe 時的 Anchor 建構邏輯
        /// </summary>
        private void ResolveOnPipe(bool isStart, XYZ targetPoint)
        {
            // 邏輯：
            // 1) 如果 targetPoint 接近 connector 且 connector -> targetPoint == connector dir ，則管延伸直接延伸到 targetPoint ，並修正 targetPoint 到下一個點上；
            // 2) 如果 targetPoint 接近 connector 且 connector -> targetPoint != connector dir ，則直接標記 AnchorConnector, AnchorElement, AnchorOrigin 等；
            // 3) 如果 targetPoint 不接近 connector ，則在 targetPoint 投影點建立 Tee/Takeoff 分支，並將 AnchorElement 為分支管， AnchorConnector 與 AnchorOrigin 指向分支遠端，並更新 targetPoint。
            var pipe = HostElement as Pipe ?? throw new InvalidOperationException("HostElement 不是 Pipe");
            var lc = pipe.Location as LocationCurve;
            var crv = lc?.Curve ?? throw new InvalidOperationException("Pipe 無有效 LocationCurve");

            // 以 targetPoint 對幹管投影，判斷端/中段
            var proj = crv.Project(targetPoint);
            var projPt = proj?.XYZPoint ?? targetPoint;

            var end0 = crv.GetEndPoint(0);
            var end1 = crv.GetEndPoint(1);
            bool nearEnd0 = projPt.DistanceTo(end0) <= _tol_ft;
            bool nearEnd1 = projPt.DistanceTo(end1) <= _tol_ft;

            // 先抓本管的兩端 connector（端點情境會用得到）
            var pipeConns = ConnectorUtils.GetConnectors(pipe)?.ToList() ?? new List<Connector>();

            // ============== 情境 A：投影接近端點 ==============
            if (nearEnd0 || nearEnd1)
            {
                var endConn = pipeConns
                    .OrderBy(c => c.Origin.DistanceTo(projPt))
                    .FirstOrDefault();
                if (endConn != null && !endConn.IsConnected)
                {
                    if (!IsColinearToTarget(endConn, targetPoint))
                    {
                        // 不共線：維持端點接法
                        AnchorElement = pipe;             // 仍為原幹管
                        AnchorConnector = endConn;          // 端點 connector
                        AnchorPoint = endConn.Origin;   // 端點位置
                        return;
                    }
                    else
                    {
                        // 共線：將管子端點「延伸/移動」到 TargetPoint
                        endConn.Origin = targetPoint;  // 直接延伸到目標點
                        AnchorElement = pipe;             // 仍為原幹管
                        AnchorPoint = targetPoint;          // 端點 connector
                        // 端點更新後需要重新取用
                        var refreshed = ConnectorUtils.GetConnectors(pipe)
                                        .OrderBy(c => c.Origin.DistanceTo(targetPoint))
                                        .FirstOrDefault();
                        if (refreshed == null)
                            throw new InvalidOperationException($"Pipe {pipe.Id} 幾何更新後無法取得端點 Connector。");
                        AnchorConnector = refreshed;
                        return;
                    }
                }
                else
                {
                    // 拋錯，端點已被占用或根本不存在
                    throw new InvalidOperationException($"Pipe{HostElement.Id} 端點已被占用或無法取得端點 Connector，無法作為 Anchor。");
                }

            }

            // ============== 情境 B：投影不接近端點 或 端點被占用（中段開 Tee/Takeoff） ==============
            // 依偏好在投影點開 Tee/Takeoff，回傳新建「分支管」
            var branch = SegmentBuilder.CreateBranchAt(_doc, _ctx, pipe, projPt, targetPoint, _routingPref);
            CreatedElementIds.Add(branch.Id);
            if (branch == null)
                throw new InvalidOperationException("CreateBranchAt 失敗，無法建立分支管");

            // 將 Anchor 轉移至新分支管的自由端
            AnchorElement = branch;
            Kind = ElementKind.Pipe;

            AnchorConnector = ConnectorUtils.GetSingleFreeEndConnector(branch)
                              ?? ConnectorUtils.GetConnectors(branch)
                                   .OrderBy(c => c.AllRefs.Cast<Connector>().Any() ? 1 : 0) // 優先挑沒被連接的
                                   .FirstOrDefault();

            AnchorPoint = AnchorConnector?.Origin;
        }

        /// <summary>
        /// HostElement 為 FamilyInstance 時的 Anchor 建構邏輯
        /// </summary>
        private void ResolveOnFamily(XYZ targetPoint)
        {
            var fi = HostElement as FamilyInstance
                     ?? throw new InvalidOperationException("ResolveOnFamily 失敗：HostElement 並非 FamilyInstance");

            // 1) 取最近的 family connector 作為出發點
            var cons = ConnectorUtils.GetConnectors(fi)?.ToList();
            if (cons == null || cons.Count == 0)
                throw new InvalidOperationException($"Family {fi.Id} 無可用 Connector");

            var fiConnector = cons.OrderBy(c => c.Origin.DistanceTo(targetPoint)).First();

            var origin = fiConnector.Origin;
            var toTarget = targetPoint - origin;
            // 若共點則直接報錯
            if (toTarget.IsZeroLength())
            {
                throw new InvalidOperationException($"元件{fi.Id} 的接點與路徑點{targetPoint.ToString()}共點，請調整路徑");
            }

            var connDir = fiConnector.CoordinateSystem.BasisZ.Normalize();
            var tgtDir = toTarget.Normalize();

            double dot = connDir.DotProduct(tgtDir);
            double cosTol = Math.Cos(_tol_deg * Math.PI / 180.0);

            // 2) 同向？（含反向不視為同向，因為需外擴）
            bool aligned = (dot >= cosTol);

            // 3) 產生第一段直管
            XYZ pStart = origin;
            XYZ pEnd;

            if (aligned)
            {
                // 同向：直拉到 targetPoint；若太短則至少拉出 minSegLen
                double len = origin.DistanceTo(targetPoint);
                pEnd = (len < _minSegLen_ft) ? origin + connDir * _minSegLen_ft : targetPoint;
            }
            else
            {
                // 不同向：先沿 connectorDir 拉一段最小管長，後續由路由器處理轉折
                pEnd = origin + connDir * _minSegLen_ft;
            }

            // 4) 建管
            var pipe = Pipe.Create(_doc, _ctx.PipeTypeId, _ctx.LevelId, fiConnector, pEnd);
            PipeUtils.SetPipeDiameter(pipe, _ctx.Diameter_ft);

            // 5) 將新管作為 Anchor，取離targetPoint 近端 connector
            AnchorElement = pipe;
            AnchorConnector = ConnectorUtils.GetConnectors(pipe).OrderBy(c => c.Origin.DistanceTo(targetPoint)).First();
            AnchorPoint = AnchorConnector?.Origin;

            if (AnchorConnector == null)
                throw new InvalidOperationException("ResolveOnFamily 失敗：無法取得新建管段的自由端 Connector");
        }



        // ---- Utilities ------------------------------------------------------

        /// <summary>
        /// 判斷 Connector 朝向是否與 指向 target 點的方向「共線」
        /// </summary>
        private bool IsColinearToTarget(Connector conn, XYZ target)
        {
            if (conn == null || target == null) return false; 
            var dir = (target - conn.Origin);
            if (dir.IsZeroLength()) return true; 
            dir = dir.Normalize();
            var axis = conn.CoordinateSystem?.BasisZ;
            if (axis == null) return false;
            var dot = axis.Normalize().DotProduct(dir);
            var ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dot))) * 180.0 / Math.PI;
            return ang <= _tol_deg;
        }

        /// <summary>
        /// 決定目前端點要「指向哪裡」
        /// 規則：
        /// 1) 有 Waypoints：
        ///    - 起點從第一個往後找
        ///    - 終點從最後一個往前找
        ///    - 需保證選到的點「不在 HostElement 上」，否則繼續找下一個；若全失敗→丟例外。
        /// 2) 無 Waypoints：
        ///    - 從「另一端元素」推導一個候選點（Pipe: 自身中心投影到對方曲線；FI: 對方最近 Connector）
        ///    - 若候選點在 HostElement 上→丟例外（無下一個可退）。
        /// </summary>
        private XYZ ComputeTargetPoint(RouteTask task, bool isStart)
        {
            // 1) 有 Waypoints：由近端往外掃描，挑不在 Host 上的第一個
            if (task?.Waypoints != null && task.Waypoints.Count > 0)
            {
                if (isStart)
                {
                    for (int i = 0; i < task.Waypoints.Count; i++)
                    {
                        var p = task.Waypoints[i];
                        var wp = new XYZ(p.X / 304.8, p.Y / 304.8, p.Z / 304.8);
                        if (!IsOnHostElement(HostElement, wp, _tol_ft))
                            return wp;
                    }
                    throw new InvalidOperationException("ComputeTargetPoint 失敗：所有起點側的 Waypoints 都落在 HostElement 上，無法作為目標點。");
                }
                else
                {
                    for (int i = task.Waypoints.Count - 1; i >= 0; i--)
                    {
                        var p = task.Waypoints[i];
                        var wp = new XYZ(p.X / 304.8, p.Y / 304.8, p.Z / 304.8);
                        if (!IsOnHostElement(HostElement, wp, _tol_ft))
                            return wp;
                    }
                    throw new InvalidOperationException("ComputeTargetPoint 失敗：所有終點側的 Waypoints 都落在 HostElement 上，無法作為目標點。");
                }
            }

            // 2) 無 Waypoints：必須從另一端推一個候選點
            var otherId = isStart ? task.EndElementId : task.StartElementId;
            var otherEle = _doc.GetElement(new ElementId(otherId))
                           ?? throw new InvalidOperationException(
                                $"ComputeTargetPoint 失敗：找不到另一端元素 (ElementId={otherId})");

            var selfCenter = GetFallbackTargetFromHost(HostElement);

            XYZ candidate;
            if (otherEle is Pipe otherPipe)
            {
                var lc = otherPipe.Location as LocationCurve
                          ?? throw new InvalidOperationException(
                                $"ComputeTargetPoint 失敗：另一端 Pipe (Id={otherPipe.Id}) 無 LocationCurve");
                var crv = lc.Curve
                          ?? throw new InvalidOperationException(
                                $"ComputeTargetPoint 失敗：另一端 Pipe (Id={otherPipe.Id}) 的 Curve 為空");
                var proj = crv.Project(selfCenter)
                           ?? throw new InvalidOperationException(
                                $"ComputeTargetPoint 失敗：無法把自身中心投影到另一端 Pipe (Id={otherPipe.Id}) 曲線上");
                // === 兩根管線平行 → 直接報錯（此情境難以客觀判斷目標點）===
                if (HostElement is Pipe hostPipe && AreCurvesParallel(hostPipe, otherPipe, _tol_deg))
                    throw new InvalidOperationException(
                        $"ComputeTargetPoint 失敗：Host Pipe (Id={hostPipe.Id}) 與另一端 Pipe (Id={otherPipe.Id}) 平行，無法可靠推導目標點");
                candidate = proj.XYZPoint;
            }
            else if (otherEle is FamilyInstance fi)
            {
                var cons = ConnectorUtils.GetConnectors(fi);
                if (cons == null || !cons.Any())
                    throw new InvalidOperationException(
                        $"ComputeTargetPoint 失敗：另一端族 (Id={fi.Id}) 無任何可用 Connector");

                candidate = cons.OrderBy(c => c.Origin.DistanceTo(selfCenter))
                                .First().Origin;
            }
            else
            {
                throw new InvalidOperationException(
                    $"ComputeTargetPoint 失敗：另一端元素 (Id={otherEle.Id}, Type={otherEle.GetType().Name}) 不支援推導目標點");
            }
            return candidate;
        }

        /// <summary>
        /// 判斷一個世界座標點是否「落在」HostElement 上（避免選到自己的管線/接點）
        /// Pipe：距離 LocationCurve 投影距離 < tol 視為在該管上
        /// FamilyInstance：距離任一 Connector < tol 視為在該族上
        /// 其他：採用 bbox 中心距離 < tol 的鬆判（可依需要擴充）
        /// </summary>
        private static bool IsOnHostElement(Element host, XYZ point, double tol_ft)
        {
            if (host == null || point == null) return false;

            if (host is Pipe p)
            {
                var lc = p.Location as LocationCurve;
                var crv = lc?.Curve;
                if (crv == null) return false;
                var proj = crv.Project(point);
                if (proj == null) return false;

                // 在曲線上且距離很小 → 視為點落在此 Pipe 上
                return proj.Distance <= tol_ft;
            }
            if (host is FamilyInstance fi)
            {
                var cons = ConnectorUtils.GetConnectors(fi);
                foreach (var c in cons)
                {
                    if (c.Origin.DistanceTo(point) <= tol_ft)
                        return true;
                }
                return false;
            }

            // 其他型別：保守判定（可依你的專案擴充）
            var bb = host.get_BoundingBox(null);
            if (bb != null)
            {
                // 判斷點是否落在 bbox 內或非常靠近（簡化）
                bool inside =
                    point.X >= bb.Min.X - tol_ft && point.X <= bb.Max.X + tol_ft &&
                    point.Y >= bb.Min.Y - tol_ft && point.Y <= bb.Max.Y + tol_ft &&
                    point.Z >= bb.Min.Z - tol_ft && point.Z <= bb.Max.Z + tol_ft;
                if (inside) return true;
            }

            return false;
        }

        private static XYZ GetFallbackTargetFromHost(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            if (e?.Location is LocationCurve lc) return lc.Curve?.Evaluate(0.5, true) ?? XYZ.Zero;
            var bb = e?.get_BoundingBox(null);
            return (bb != null) ? (bb.Min + bb.Max) * 0.5 : XYZ.Zero;
        }
        private static bool AreCurvesParallel(Pipe a, Pipe b, double angTolDeg)
        {
            var ca = (a?.Location as LocationCurve)?.Curve;
            var cb = (b?.Location as LocationCurve)?.Curve;
            if (ca == null || cb == null) return false;

            var va = (ca.GetEndPoint(1) - ca.GetEndPoint(0)).Normalize();
            var vb = (cb.GetEndPoint(1) - cb.GetEndPoint(0)).Normalize();

            if (va.IsZeroLength() || vb.IsZeroLength()) return false;

            // 允許反向：取絕對值
            double dot = Math.Abs(va.DotProduct(vb));
            double cosTol = Math.Cos(angTolDeg * Math.PI / 180.0);
            return dot >= cosTol;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing;
using RevitMCPCommandSet.Utils.Routing;

namespace RevitMCPCommandSet.Services.Routing.Conduits
{
    /// <summary>
    /// ConduitRoutingAnchor：
    /// 將 tray / endpoint / 既有 conduit 正規化成
    /// 「後續路由可以直接接上的 conduit 端點」。
    ///
    /// 設計目標是跟 RoutingAnchor 結構類似：
    /// - HostElement：輸入的元素 (Tray / Panel / Conduit / ...）
    /// - AnchorElement：實際用來接後續路徑的 conduit
    /// - AnchorConnector / AnchorPoint：實際路由起點 / 終點
    /// - CreatedElementIds：在建 Anchor 時新增的元素列表
    /// </summary>
    public class ConduitRoutingAnchor
    {
        public enum HostKind
        {
            CableTray,
            Conduit,
            FamilyInstance,
            Other
        }

        public Connector AnchorConnector { get; private set; }
        public XYZ AnchorPoint { get; private set; }
        public Element AnchorElement { get; private set; }

        /// <summary>使用者傳進來的原始 host（Tray / Panel / Conduit / ...）</summary>
        public Element HostElement { get; private set; }
        public HostKind Kind { get; private set; }

        /// <summary>建 Anchor 過程中額外產生的元素（stub conduit 等）</summary>
        public List<ElementId> CreatedElementIds { get; } = new List<ElementId>();

        // 這裡直接沿用 RoutingContext，PipeTypeId 當作 ConduitTypeId
        public ElementId ConduitTypeId { get; }
        public ElementId LevelId { get; }
        public double DiameterFt { get; }

        private readonly Document _doc;
        private readonly ConduitRoutingContext _ctx;
        private readonly ConduitRouteTaskInfo _task;

        private readonly double _tol_ft;
        private readonly double _tol_deg;
        private readonly double _minSegLenFt;

        public ConduitRoutingAnchor(
            Document doc,
            Element host,
            ConduitRouteTaskInfo task,
            bool isStart,
            ConduitRoutingContext ctx)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _task = task ?? throw new ArgumentNullException(nameof(task));

            HostElement = host ?? throw new ArgumentNullException(nameof(host));
            Kind = ClassifyHost(HostElement);

            ConduitTypeId = ctx.ConduitTypeId;   // 先共用 PipeTypeId 當 conduit type
            LevelId = ctx.LevelId;
            DiameterFt = ctx.DiameterFt;

            _tol_ft = Math.Max(ctx.ToleranceFt, 1e-4);
            _tol_deg = ctx.ToleranceDeg;
            _minSegLenFt = ctx.MinSegmentLengthFt;

            BuildAnchorElement(isStart);
        }

        // ---------------------------------------------------------------------
        // 公開 helper：若後面有移動 anchorPoint，可呼叫刷新 connector
        // ---------------------------------------------------------------------
        public Connector RefreshConnector()
        {
            if (AnchorElement is Conduit conduit)
            {
                var conns = ConnectorUtils.GetConnectors(conduit);
                AnchorConnector = conns?
                    .OrderBy(c => c.Origin.DistanceTo(AnchorPoint))
                    .FirstOrDefault();
            }
            else if (AnchorElement is FamilyInstance fi)
            {
                AnchorConnector = ConnectorUtils.GetNearConnector(fi, AnchorPoint);
            }

            return AnchorConnector;
        }

        // ---------------------------------------------------------------------
        // 內部主流程
        // ---------------------------------------------------------------------
        private void BuildAnchorElement(bool isStart)
        {
            var targetPoint = ComputeTargetPoint(_task, isStart);

            switch (Kind)
            {
                case HostKind.CableTray:
                    ResolveOnTray(targetPoint);
                    break;

                case HostKind.Conduit:
                    ResolveOnConduit(targetPoint);
                    break;

                case HostKind.FamilyInstance:
                    ResolveOnFamily(targetPoint);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"ConduitRoutingAnchor：不支援的 HostElement 型別：{HostElement?.GetType().Name}");
            }

            if (AnchorConnector == null)
            {
                throw new InvalidOperationException(
                    "ConduitRoutingAnchor 無法取得有效的 AnchorConnector。");
            }

            AnchorPoint = AnchorConnector.Origin;
        }

        // ---------------------------------------------------------------------
        // Host = CableTray：從 tray 上某點「長出」第一段 conduit（不直接跟 tray 連接）
        // ---------------------------------------------------------------------
        private void ResolveOnTray(XYZ targetPoint)
        {
            var tray = HostElement as CableTray
                       ?? throw new InvalidOperationException("HostElement 不是 CableTray");

            var lc = tray.Location as LocationCurve
                      ?? throw new InvalidOperationException("CableTray 無 LocationCurve");
            var crv = lc.Curve
                      ?? throw new InvalidOperationException("CableTray LocationCurve 無效");

            // 先把目標點投影到 tray 中心線上，當作起點附近
            var proj = crv.Project(targetPoint);
            var pOnTray = proj?.XYZPoint ?? GetCenterOfElement(tray);

            // 取得 tray 的 X 方向作為「從托盤側邊伸出」的基準方向
            var xConn = tray.ConnectorManager?.Lookup(0);
            XYZ xDir = xConn?.CoordinateSystem?.BasisX;
            if (xDir == null || xDir.IsZeroLength())
                xDir = XYZ.BasisX; // fallback，理論上不太會用到

            // 目標相對方向（tray 中心線 → target）
            var toTargetFromCenter = targetPoint - pOnTray;
            if (toTargetFromCenter.IsZeroLength())
            {
                // 若靠得太近，就先假設往 X 方向拉
                toTargetFromCenter = xDir;
            }

            // 判斷 xDir 跟目標方向的正反向
            xDir = xDir.Normalize();
            XYZ baseDir = (xDir.DotProduct(toTargetFromCenter) >= 0)
                ? xDir
                : xDir.Negate();   // 避免往「背對 target」的方向長

            double trayWidth = tray.Width;

            // 從 tray 邊緣當作 conduit 起點
            var pStart = pOnTray + baseDir * (trayWidth / 2.0);

            // ===== 比照 FamilyInstance 的邏輯 =====
            // 若 baseDir 與 (targetPoint - pStart) 方向一致，就直接拉到 targetPoint（至少 _minSegLenFt）
            // 否則只拉出一段最小長度 stub，之後交給路由器再轉折
            var toTargetFromStart = targetPoint - pStart;
            if (toTargetFromStart.IsZeroLength())
            {
                // targetPoint 剛好落在 tray 邊緣附近，先往 baseDir 拉一段
                toTargetFromStart = baseDir;
            }

            var tgtDir = toTargetFromStart.Normalize();

            double dot = baseDir.DotProduct(tgtDir);
            double cosTol = Math.Cos(_tol_deg * Math.PI / 180.0);
            bool aligned = dot >= cosTol;

            XYZ pEnd;
            if (aligned)
            {
                // 方向一致：直接拉到 targetPoint，但長度至少 _minSegLenFt
                double len = pStart.DistanceTo(targetPoint);
                pEnd = (len < _minSegLenFt)
                    ? pStart + baseDir * _minSegLenFt
                    : targetPoint;
            }
            else
            {
                // 方向不一致：只沿 baseDir 拉出一段最小長度，後續再轉折
                pEnd = pStart + baseDir * _minSegLenFt;
            }

            var conduit = Conduit.Create(_doc, ConduitTypeId, pStart, pEnd, LevelId);
            ConduitUtils.SetConduitDiameter(conduit, DiameterFt);    // 或改成 ConduitUtils.SetConduitDiameter(...)
            CreatedElementIds.Add(conduit.Id);

            AnchorElement = conduit;
            AnchorConnector = ConnectorUtils
                .GetConnectors(conduit)
                .OrderBy(c => c.Origin.DistanceTo(targetPoint))
                .FirstOrDefault();

            if (AnchorConnector == null)
                throw new InvalidOperationException("ResolveOnTray 失敗：無法取得 stub conduit 的 Connector");
        }

        // ---------------------------------------------------------------------
        // Host = 既有 Conduit：直接用「最靠近 target 的端點 connector」當 Anchor
        // 目前先不在中段打 Tee / Bend，後續若要支援再補 SegmentBuilder for conduit。
        // ---------------------------------------------------------------------
        private void ResolveOnConduit(XYZ targetPoint)
        {
            var conduit = HostElement as Conduit
                          ?? throw new InvalidOperationException("HostElement 不是 Conduit");

            var conns = ConnectorUtils.GetConnectors(conduit)?.ToList();
            if (conns == null || conns.Count == 0)
                throw new InvalidOperationException($"Conduit {conduit.Id} 無任何 Connector");

            AnchorElement = conduit;
            AnchorConnector = conns
                .OrderBy(c => c.Origin.DistanceTo(targetPoint))
                .First();
        }

        // ---------------------------------------------------------------------
        // Host = FamilyInstance（panel / box / 機具等）：
        // 從最近的 connector 延伸一小段 conduit，作為後續路由 Anchor。
        // ---------------------------------------------------------------------
        private void ResolveOnFamily(XYZ targetPoint)
        {
            var fi = HostElement as FamilyInstance
                     ?? throw new InvalidOperationException("HostElement 並非 FamilyInstance");

            var cons = ConnectorUtils.GetConnectors(fi)?.ToList();
            if (cons == null || cons.Count == 0)
                throw new InvalidOperationException($"Family {fi.Id} 無任何 Connector");

            var fiConnector = cons
                .OrderBy(c => c.Origin.DistanceTo(targetPoint))
                .First();

            var origin = fiConnector.Origin;
            var toTarget = targetPoint - origin;
            if (toTarget.IsZeroLength())
            {
                // 如果 target 跟 connector 共點，就改用 connector 的 Z 方向
                toTarget = fiConnector.CoordinateSystem?.BasisZ ?? XYZ.BasisZ;
            }

            var connDir = fiConnector.CoordinateSystem?.BasisZ?.Normalize() ?? XYZ.BasisZ;
            var tgtDir = toTarget.Normalize();

            double dot = connDir.DotProduct(tgtDir);
            double cosTol = Math.Cos(_tol_deg * Math.PI / 180.0);
            bool aligned = dot >= cosTol;

            XYZ pStart = origin;
            XYZ pEnd;

            if (aligned)
            {
                var len = origin.DistanceTo(targetPoint);
                pEnd = (len < _minSegLenFt)
                    ? origin + connDir * _minSegLenFt
                    : targetPoint;
            }
            else
            {
                // 不同向 → 先沿 connector 方向拉出一段最小長度，之後再交給路由器轉折
                pEnd = origin + connDir * _minSegLenFt;
            }

            var conduit = Conduit.Create(_doc, ConduitTypeId, pStart, pEnd, LevelId);
            ConduitUtils.SetConduitDiameter(conduit, DiameterFt);
            CreatedElementIds.Add(conduit.Id);

            AnchorElement = conduit;
            AnchorConnector = ConnectorUtils
                .GetConnectors(conduit)
                .OrderBy(c => c.Origin.DistanceTo(targetPoint))
                .FirstOrDefault();

            if (AnchorConnector == null)
                throw new InvalidOperationException("ResolveOnFamily 失敗：無法取得 stub conduit 的 Connector");
        }

        // ---------------------------------------------------------------------
        // 目標點推導：先走 Waypoints，沒有時用「另一端元素中心」
        // （這邊先做簡版，之後要像 RoutingAnchor 那麼完整再一起升級）
        // ---------------------------------------------------------------------
        private XYZ ComputeTargetPoint(ConduitRouteTaskInfo task, bool isStart)
        {
            // 1) 有 Waypoints 時：
            if (task?.Waypoints != null && task.Waypoints.Count > 0)
            {
                JZPoint p = isStart ? task.Waypoints.First() : task.Waypoints.Last();
                return new XYZ(p.X / 304.8, p.Y / 304.8, p.Z / 304.8);
            }

            // 2) 無 Waypoints，就往「另一端元素」的幾何中心推一個點
            var otherId = isStart ? task.EndElementId : task.StartElementId;
            var otherEl = _doc.GetElement(new ElementId(otherId))
                         ?? throw new InvalidOperationException(
                             $"ComputeTargetPoint 失敗：找不到另一端元素 (ElementId={otherId})");

            return GetCenterOfElement(otherEl);
        }

        // ---------------------------------------------------------------------
        // 小工具
        // ---------------------------------------------------------------------
        private static HostKind ClassifyHost(Element e)
        {
            if (e is CableTray) return HostKind.CableTray;
            if (e is Conduit) return HostKind.Conduit;
            if (e is FamilyInstance) return HostKind.FamilyInstance;
            return HostKind.Other;
        }

        private static XYZ GetCenterOfElement(Element e)
        {
            if (e?.Location is LocationPoint lp)
                return lp.Point;

            if (e?.Location is LocationCurve lc)
                return lc.Curve?.Evaluate(0.5, true) ?? XYZ.Zero;

            var bb = e?.get_BoundingBox(null);
            if (bb != null)
                return (bb.Min + bb.Max) * 0.5;

            return XYZ.Zero;
        }
    }
}

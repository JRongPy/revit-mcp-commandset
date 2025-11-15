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
using Autodesk.Revit.DB.Electrical;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Routing.Conduits
{
    /// <summary>
    /// Conduit 版本的 Anchor：
    /// 目前只包住「HostElement + AnchorConduit + AnchorConnector」，
    /// 未來可以再加方向向量、直角首段等資訊。
    /// </summary>
    public class ConduitRouteAnchor
    {
        public Element HostElement { get; }
        public Conduit AnchorConduit { get; }
        public Connector AnchorConnector { get; }
        public ConduitAnchorKind Kind { get; }

        public XYZ AnchorPoint => AnchorConnector?.Origin ?? XYZ.Zero;

        public ConduitRouteAnchor(
            Element host,
            Conduit conduit,
            Connector connector,
            ConduitAnchorKind kind)
        {
            HostElement = host ?? throw new ArgumentNullException(nameof(host));
            AnchorConduit = conduit ?? throw new ArgumentNullException(nameof(conduit));
            AnchorConnector = connector ?? throw new ArgumentNullException(nameof(connector));
            Kind = kind;
        }
    }

    /// <summary>
    /// 專門負責從 tray / endpoint 產生「第一段 Conduit Anchor」的 resolver。
    /// ConduitRoutingCore 只要呼叫這裡，不用管細節。
    /// </summary>
    public static class ConduitAnchorResolver
    {
        // 目前先用固定 300mm 作為 anchor 段長度，可之後用參數/RouteTask 控制
        private const double AnchorLengthMm = 300.0;

        public static ConduitRouteAnchor CreateTrayAnchor(
            Document doc,
            Element tray,
            Element endpoint,
            ILogger logger)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (tray == null) throw new ArgumentNullException(nameof(tray));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));

            var origin = GetRepresentativePoint(tray);
            var target = GetRepresentativePoint(endpoint);
            var dir = (target - origin);
            if (dir.IsZeroLength())
                dir = XYZ.BasisX;
            dir = dir.Normalize();

            var lengthFt = AnchorLengthMm / 304.8;
            var p0 = origin;
            var p1 = origin + dir * lengthFt;

            var (conduitTypeId, levelId) =
                ResolveConduitTypeAndLevel(doc, tray, endpoint, logger);

            var conduit = Conduit.Create(doc, conduitTypeId, p0, p1, levelId);

            var conn = ConnectorUtils.GetNearConnector(conduit, target);
            if (conn == null)
                throw new InvalidOperationException("Tray Anchor 建立成功，但無法取得 Connector。");

            logger.Info($"[TrayAnchor] Conduit={conduit.Id.IntegerValue}, Connector at {conn.Origin}");

            return new ConduitRouteAnchor(tray, conduit, conn, ConduitAnchorKind.Tray);
        }

        public static ConduitRouteAnchor CreateEndpointAnchor(
            Document doc,
            Element tray,
            Element endpoint,
            ILogger logger)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (tray == null) throw new ArgumentNullException(nameof(tray));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));

            var origin = GetRepresentativePoint(endpoint);
            var target = GetRepresentativePoint(tray);
            var dir = (target - origin);
            if (dir.IsZeroLength())
                dir = XYZ.BasisX;
            dir = dir.Normalize();

            var lengthFt = AnchorLengthMm / 304.8;
            var p0 = origin;
            var p1 = origin + dir * lengthFt;

            var (conduitTypeId, levelId) =
                ResolveConduitTypeAndLevel(doc, endpoint, tray, logger);

            var conduit = Conduit.Create(doc, conduitTypeId, p0, p1, levelId);

            var conn = ConnectorUtils.GetNearConnector(conduit, target);
            if (conn == null)
                throw new InvalidOperationException("Endpoint Anchor 建立成功，但無法取得 Connector。");

            logger.Info($"[EndpointAnchor] Conduit={conduit.Id.IntegerValue}, Connector at {conn.Origin}");

            return new ConduitRouteAnchor(endpoint, conduit, conn, ConduitAnchorKind.Endpoint);
        }

        // ---- helpers ----

        private static (ElementId conduitTypeId, ElementId levelId)
            ResolveConduitTypeAndLevel(Document doc, Element primary, Element secondary, ILogger logger)
        {
            // 1) ConduitType
            var conduitType = new FilteredElementCollector(doc)
                .OfClass(typeof(ConduitType))
                .Cast<ConduitType>()
                .FirstOrDefault();

            if (conduitType == null)
                throw new InvalidOperationException("專案中找不到任何 ConduitType，無法建立 Conduit。");


            // 3) Level：
            //    先試 primary，失敗再用 secondary，最後抓任一 Level 當 fallback。
            ElementId levelId = TryGetLevelId(primary)
                                ?? TryGetLevelId(secondary)
                                ?? new FilteredElementCollector(doc)
                                        .OfClass(typeof(Level))
                                        .Cast<Level>()
                                        .FirstOrDefault()?.Id
                                ?? ElementId.InvalidElementId;

            if (levelId == ElementId.InvalidElementId)
                throw new InvalidOperationException("無法判斷 Conduit 的 Level。");

            logger.Info($"[ConduitType] ConduitType={conduitType.Id}, Level={levelId}");

            return (conduitType.Id, levelId);
        }

        private static ElementId TryGetLevelId(Element e)
        {
            if (e == null) return null;

            // FamilyInstance.LevelId
            if (e is FamilyInstance fi)
                return fi.LevelId;

            // MEP curve / tray reference level
            if (e is MEPCurve mep)
                return mep.ReferenceLevel?.Id;

            // 通用 Level 參數
            var p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.RBS_END_LEVEL_PARAM);
            if (p != null && p.HasValue)
                return p.AsElementId();

            return null;
        }

        private static XYZ GetRepresentativePoint(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            if (e?.Location is LocationCurve lc) return lc.Curve?.Evaluate(0.5, true) ?? XYZ.Zero;
            var bb = e?.get_BoundingBox(null);
            return (bb != null) ? (bb.Min + bb.Max) * 0.5 : XYZ.Zero;
        }

        private static bool IsZeroLength(this XYZ v)
        {
            return v == null || v.GetLength() < 1e-6;
        }
    }
}

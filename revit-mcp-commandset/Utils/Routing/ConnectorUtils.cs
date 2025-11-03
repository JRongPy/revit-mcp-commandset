using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMCPCommandSet.Utils.Routing
{
    public static class ConnectorUtils
    {
        /// <summary>
        /// Get all connectors from an element (Pipe, FamilyInstance with MEPModel, or any element with ConnectorManager).
        /// </summary>
        public static IEnumerable<Connector> GetConnectors(Element e)
        {
            ConnectorSet connectors = null;

            switch (e)
            {
                case FamilyInstance fi when fi.MEPModel != null:
                    connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                    break;
                case MEPCurve curve:
                    connectors = curve.ConnectorManager?.Connectors;
                    break;
            }

            return connectors?.Cast<Connector>() ?? Enumerable.Empty<Connector>();
        }

        /// <summary>
        /// Find the connector closest to a given XYZ point.
        /// Works for both Pipe and FamilyInstance.
        /// </summary>
        public static Connector GetNearConnector(Element e, XYZ target)
        {
            return GetConnectors(e)
                .OrderBy(c => c.Origin.DistanceTo(target))
                .FirstOrDefault();
        }

        /// <summary>
        /// Get the opposite connector of a linear element given one point.
        /// </summary>
        public static Connector GetFarConnector(Element e, XYZ point)
        {
            var cons = GetConnectors(e).ToList();
            if (cons.Count != 2) return null;
            return cons[0].Origin.IsAlmostEqualTo(point) ? cons[1] : cons[0];
        }

        /// <summary>
        /// Get the first unconnected (free) connector of an element.
        /// </summary>
        public static Connector GetSingleFreeEndConnector(Element e)
        {
            return GetConnectors(e).FirstOrDefault(c => !c.IsConnected);
        }

        /// <summary>
        /// Ensure that a FamilyInstance has at least one MEP connector.
        /// Throws InvalidOperationException if no connector is found.
        /// </summary>
        public static void EnsureHasConnectors(FamilyInstance fi)
        {
            var has = fi?.MEPModel?.ConnectorManager?.Connectors?.Cast<Connector>()?.Any() ?? false;
            if (!has) throw new InvalidOperationException($"族 {fi.Id.Value} 無 MEP 連接器");
        }

        /// <summary>
        /// 嘗試從元素取得可用的「圓管直徑（ft）」。Pipe → p.Diameter；FamilyInstance → 取圓形 connector 的 Radius*2 最大值。
        /// </summary>
        public static double TryGetConnectorDiameterFt(Element e)
        {
            if (e is Pipe p && p.Diameter > 0) return p.Diameter;

            double best = 0.0;
            foreach (var c in GetConnectors(e))
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
        /// Get a safe direction vector from a connector (normalized).
        /// Uses CoordinateSystem.BasisZ.
        /// </summary>
        public static XYZ GetConnectorDirection(Connector c)
        {
            return c.CoordinateSystem?.BasisZ.Normalize() ?? XYZ.Zero;
        }
    }

}

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMCPCommandSet.Services.Routing
{
    internal static class ConnectorUtils
    {
        public static IEnumerable<Connector> GetConnectors(Element e)
        {
            if (e is FamilyInstance fi)
            {
                var cons = fi.MEPModel?.ConnectorManager?.Connectors;
                return cons?.Cast<Connector>() ?? Enumerable.Empty<Connector>();
            }
            if (e is Pipe p)
            {
                var cons = p.ConnectorManager?.Connectors;
                return cons?.Cast<Connector>() ?? Enumerable.Empty<Connector>();
            }
            return Enumerable.Empty<Connector>();
        }

        public static IEnumerable<Connector> GetPipeConnectors(Pipe pipe)
            => pipe?.ConnectorManager?.Connectors?.Cast<Connector>() ?? Enumerable.Empty<Connector>();

        public static Connector FindNearestConnector(FamilyInstance fi, XYZ target)
        {
            return GetConnectors(fi)
                .OrderBy(c => c.Origin.DistanceTo(target))
                .FirstOrDefault();
        }

        public static Connector GetFarEndConnector(Pipe pipe, Connector knownEnd)
        {
            var cons = GetPipeConnectors(pipe).ToList();
            if (cons.Count != 2) return null;
            return cons[0].Id == knownEnd?.Id ? cons[1] : cons[0];
        }

        public static Connector GetSingleFreeEndConnector(Pipe p)
        {
            // 回傳未連接的一端（Connected == false）
            return GetPipeConnectors(p).FirstOrDefault(c => !c.IsConnected);
        }

        public static XYZ GetFreeEndPoint(Pipe p)
        {
            return GetSingleFreeEndConnector(p)?.Origin ?? XYZ.Zero;
        }
    }
}

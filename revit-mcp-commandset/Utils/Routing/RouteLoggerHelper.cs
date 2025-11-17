using RevitMCPCommandSet.Services.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Utils.Routing
{
    public static class RouteLoggerHelper
    {
        // =============== 日誌輔助 =======================

        public static string  SerializeTask(RouteTaskInfo t)
        {
            if (t == null) return "null";
            var wp = (t.Waypoints == null || t.Waypoints.Count == 0)
                ? "[]"
                : string.Join(";", t.Waypoints.Select(p => $"({p.X:F1},{p.Y:F1},{p.Z:F1})"));
            return $"Start={t.StartElementId}, End={t.EndElementId}, Waypoints={wp}, MinLen={t.MinSegmentLengthMm}mm, Pref={t.RoutingPreference}";
        }

        public static string DescribeElement(Element e)
        {
            if (e == null) return "null";
            string cat = e.Category?.Name ?? "NoCategory";
            return $"{e.Id} [{e.GetType().Name}] ({cat})";
        }
    }
}

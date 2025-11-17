// Services/Routing/RouteTask.cs
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services.Routing
{
    public class RouteTaskInfo
    {
        public int StartElementId { get; set; }
        public int EndElementId { get; set; }
        public List<JZPoint> Waypoints { get; set; } = new List<JZPoint>();
        public double MinSegmentLengthMm { get; set; } = 100;
        public string RoutingPreference { get; set; } = "Tee"; // or Takeoff
        public double ToleranceMm { get; set; } = 10;
        public double ToleranceDeg { get; set; } = 5.0;
        public OverrideDTO Override { get; set; }

        public class OverrideDTO
        {
            public int? SystemTypeId { get; set; }
            public int? PipeTypeId { get; set; }
            public int? LevelId { get; set; }
            public double? DiameterMm { get; set; }
        }
    }
}

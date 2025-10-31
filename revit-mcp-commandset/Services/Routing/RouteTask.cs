// Services/Routing/RouteTask.cs
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services.Routing
{
    public class RouteTask
    {
        public int StartElementId { get; set; }
        public int EndElementId { get; set; }
        public List<JZPoint> Waypoints { get; set; } = new List<JZPoint>();
        public double MinSegmentLength_mm { get; set; } = 100;
        public string RoutingPreference { get; set; } = "Tee"; // or Takeoff
        public double Tolerance_mm { get; set; } = 10;
        public OverrideDTO Override { get; set; }

        public class OverrideDTO
        {
            public int? SystemTypeId { get; set; }
            public int? PipeTypeId { get; set; }
            public int? LevelId { get; set; }
            public double? Diameter_mm { get; set; }
        }
    }
}

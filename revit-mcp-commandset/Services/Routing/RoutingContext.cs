using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.Routing
{
    /// <summary>
    /// 推斷後的上下文（管型、系統、標高、直徑、公差）
    /// </summary>
    public class RoutingContext
    {
        public ElementId SystemTypeId { get; set; }
        public ElementId PipeTypeId { get; set; }
        public ElementId LevelId { get; set; }
        public double Diameter_ft { get; set; }
        public double Tolerance_ft { get; set; }
    }
}

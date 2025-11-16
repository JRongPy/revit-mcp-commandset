using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Services.Routing.Conduits
{
    /// <summary>
    /// 推斷後的上下文（管型、標高、直徑、公差）
    /// </summary>
    public class ConduitRoutingContext
    {
        public ElementId ConduitTypeId { get; set; }
        public ElementId LevelId { get; set; }
        public double DiameterFt { get; set; }
        public double ToleranceFt { get; set; }
        public double MinSegmentLengthFt { get; set; }
        public double ToleranceDeg { get; set; }
    }

}

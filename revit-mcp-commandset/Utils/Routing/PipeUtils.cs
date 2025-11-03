using Autodesk.Revit.DB.Plumbing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RevitMCPCommandSet.Services.Routing;

namespace RevitMCPCommandSet.Utils.Routing
{
    public static class PipeUtils
    {
        /// <summary>
        /// 設定管徑（英尺）
        /// </summary>
        public static void SetPipeDiameter(Pipe pipe, double diameterFt)
        {
            try
            {
                if (pipe == null || diameterFt <= 0) return;
                var prm = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (prm != null && !prm.IsReadOnly)
                    prm.Set(diameterFt);
            }
            catch { /* 忽略設定失敗，不影響主流程 */ }
        }          

    }
}

// Commands/RoutePipesByWaypointsCommand.cs
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.RoutePipesByWaypoints;
using RevitMCPCommandSet.Services.Routing;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.RoutePipesByWaypoints
{
    public class RoutePipesByWaypointsCommand : ExternalEventCommandBase
    {
        private RoutePipesByWaypointsEventHandler _handler => (RoutePipesByWaypointsEventHandler)Handler;

        public override string CommandName => "route_pipes_by_waypoints";

        public RoutePipesByWaypointsCommand(UIApplication uiApp)
            : base(new RoutePipesByWaypointsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var task = parameters["task"]?.ToObject<RouteTask>();
                if (task == null) throw new ArgumentNullException(nameof(task), "AI傳入資料為空");

                _handler.SetTask(task);

                if (RaiseAndWaitForCompletion(120000)) // 路由較長，放寬 timeout
                    return _handler.Result;
                else
                    throw new TimeoutException("路由建模操作逾時");
            }
            catch (Exception ex)
            {
                throw new Exception($"路由建模失敗: {ex.Message}");
            }
        }
    }
}

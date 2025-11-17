// Commands/RoutePipesByWaypointsCommand.cs
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Routing;
using RevitMCPSDK.API.Base;
using System;
using System.Collections.Generic;

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
                // 1) 批次優先：若有 tasks 就跑批次
                var tasksToken = parameters["tasks"];
                if (tasksToken != null && tasksToken.Type == JTokenType.Array)
                {
                    var tasks = tasksToken.ToObject<List<RouteTaskInfo>>() ?? new List<RouteTaskInfo>();
                    if (tasks.Count == 0) throw new ArgumentException("參數 tasks 為空，請提供至少一筆任務。");

                    _handler.SetTasks(tasks);

                    // 動態逾時：每筆給 120 秒，上限 10 分鐘（依專案調整）
                    int perItemMs = 120_000;
                    int maxTotalMs = 600_000;
                    int timeoutMs = Math.Min(maxTotalMs, Math.Max(perItemMs, tasks.Count * perItemMs));

                    if (RaiseAndWaitForCompletion(timeoutMs))
                    {
                        // 批次模式回傳 BatchResult（內含每筆的成功/失敗與建立的元素）
                        return _handler.BatchResult;
                    }
                    else
                    {
                        throw new TimeoutException($"批次路由逾時（共 {tasks.Count} 筆，逾時 {timeoutMs / 1000} 秒）。");
                    }
                }

                // 2) 單筆任務：沿用舊行為
                var taskToken = parameters["task"];
                if (taskToken == null || taskToken.Type == JTokenType.Null)
                    throw new ArgumentNullException(nameof(parameters), "請提供 task 或 tasks。");

                var task = taskToken.ToObject<RouteTaskInfo>();
                if (task == null) throw new ArgumentNullException(nameof(task), "AI 傳入的 task 內容為空或格式不正確。");

                _handler.SetTask(task);

                // 原本單筆的寬鬆逾時
                if (RaiseAndWaitForCompletion(120_000))
                    return _handler.Result;

                throw new TimeoutException("路由建模操作逾時（120 秒）。");
            }
            catch (Exception ex)
            {
                // 建議：保留原始例外資訊（方便上層紀錄 stack trace）
                // 若你的框架需要回傳 AIResult 也可在這改為回傳物件而非 throw
                throw new Exception($"路由建模失敗：{ex.Message}", ex);
            }
        }
    }
}

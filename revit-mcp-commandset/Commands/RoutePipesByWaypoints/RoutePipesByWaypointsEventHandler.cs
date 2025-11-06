using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Routing;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static RevitMCPCommandSet.Services.Routing.RoutingServices;

namespace RevitMCPCommandSet.Commands.RoutePipesByWaypoints
{
    public class RoutePipesByWaypointsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;

        private List<RouteTask> _batchTasks;   // 批次任務（可為 null 表示單筆）
        public AIResult<List<BatchRouteResult>> BatchResult { get; private set; } // 批次回傳

        public class BatchRouteResult
        {
            public RouteTask Task { get; set; }
            public bool Success { get; set; }
            public string Message { get; set; }
            public List<int> CreatedElementIds { get; set; } = new();
        }

        private readonly ManualResetEvent _reset = new(false);
        public AIResult<List<int>> Result { get; private set; }
        private RouteTask _task;

        private ILogger _logger;

        public RoutePipesByWaypointsEventHandler()
        {
            try
            {
                _logger = new Logger();
            }
            catch { /* ignore permission errors */ }
        }

        public void SetTasks(List<RouteTask> tasks)
        {
            _batchTasks = tasks ?? new List<RouteTask>();
            _task = null; // 避免混用
            _reset.Reset();
        }

        public void SetTask(RouteTask task)
        {
            _task = task;
            _reset.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            _logger = _logger ?? new Logger(); // 你自己的通用 logger
            var doc = _uiApp.ActiveUIDocument?.Document;

            try
            {
                if (doc == null)
                    throw new InvalidOperationException("沒有可用的 ActiveUIDocument，請先開啟一個專案或視圖。");

                if (_batchTasks != null && _batchTasks.Count > 0)
                {
                    var results = ExecuteBatchWithTx(doc, _batchTasks, _logger);

                    BatchResult = new AIResult<List<BatchRouteResult>>
                    {
                        Success = results.All(r => r.Success),
                        Message = $"批次完成：成功 {results.Count(r => r.Success)}，失敗 {results.Count(r => !r.Success)}",
                        Response = results
                    };
                }
                else
                {
                    var created = ExecuteSingleWithTx(doc, _task, _logger);

                    Result = new AIResult<List<int>>
                    {
                        Success = true,
                        Message = $"路由完成，生成 {created.Count} 個元素",
                        Response = created.Select(e => e.IntegerValue).ToList()
                    };
                }
            }
            catch (Exception ex)
            {
                // 建議用 logger 記錄，避免 TaskDialog 阻斷自動化；UI 情境再選擇顯示
                _logger?.Error($"[Execute][ERROR] {ex}");

                Result = new AIResult<List<int>>
                {
                    Success = false,
                    Message = $"任務執行失敗：{ex.Message}",
                    Response = new List<int>()
                };
            }
            finally
            {
                _reset.Set(); // 確保事件解除阻塞
            }
        }


        // 單筆：包交易 → 呼叫 RoutingCore 核心
        private List<ElementId> ExecuteSingleWithTx(Document doc, RouteTask task, ILogger logger)
        {
            if (doc.IsModifiable)
            {
                logger.Info("Using SubTransaction for single task.");
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    var created = RoutingCore.RoutePipesTask(doc, task, logger);
                    sub.Commit();
                    return created;
                }
            }
            else
            {
                logger.Info("Using Transaction for single task.");
                using (var tx = new Transaction(doc, "Route pipes by waypoints"))
                {
                    tx.Start();
                    var created = RoutingCore.RoutePipesTask(doc, task, logger);
                    tx.Commit();
                    return created;
                }
            }
        }

        // 批次：逐筆包交易（每筆各自成功/失敗；最彈性）
        private List<BatchRouteResult> ExecuteBatchWithTx(Document doc, IEnumerable<RouteTask> tasks, ILogger logger)
        {
            var results = new List<BatchRouteResult>();
            logger.Info(GetName() + " - Batch Execution Start");    
            foreach (var t in tasks ?? Enumerable.Empty<RouteTask>())
            {
                try
                {
                    List<ElementId> created;

                    if (doc.IsModifiable)
                    {
                        logger.Info("Using SubTransaction for batch item.");
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            created = RoutingCore.RoutePipesTask(doc, t, logger);
                            sub.Commit();
                        }
                    }
                    else
                    {
                        logger.Info("Using Transaction for batch item.");
                        using (var tx = new Transaction(doc, "Route pipes by waypoints (batch item)"))
                        {
                            tx.Start();
                            created = RoutingCore.RoutePipesTask(doc, t, logger);
                            tx.Commit();
                        }
                    }

                    results.Add(new BatchRouteResult
                    {
                        Task = t,
                        Success = true,
                        Message = $"路由完成，生成 {created.Count} 個元素",
                        CreatedElementIds = created.Select(id => id.IntegerValue).ToList()
                    });
                    logger.Info($"[BATCH ITEM SUCCESS] Created {created.Count} elements.");
                }
                catch (Exception ex)
                {
                    logger?.Error($"[BATCH ITEM ERROR] {ex}");
                    results.Add(new BatchRouteResult
                    {
                        Task = t,
                        Success = false,
                        Message = $"路由失敗：{ex.Message}"
                    });
                }
            }
            return results;
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 120000) => _reset.WaitOne(timeoutMilliseconds);
        public string GetName() => "Route Pipes by Waypoints";
    }
}

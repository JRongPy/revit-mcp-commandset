// Commands/RouteConduitsByWaypoints/RouteConduitsByWaypointsEventHandler.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing.Conduits;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Commands.RouteConduitsByWaypoints
{
    /// <summary>
    /// 實際在 Revit Transaction 裡面跑的 handler。
    /// 單筆支援：ConduitAnchorTask
    /// 批次支援：List&lt;ConduitAnchorTask&gt;
    /// </summary>
    public class RouteConduitsByWaypointsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;

        private List<ConduitRouteTask> _batchTasks;
        private ConduitRouteTask _task;

        public class BatchConduitRouteResult
        {
            public ConduitRouteTask Task { get; set; }
            public bool Success { get; set; }
            public string Message { get; set; }
            public List<int> CreatedElementIds { get; set; } = new();
        }

        private readonly ManualResetEvent _reset = new(false);

        public AIResult<List<int>> Result { get; private set; }
        public AIResult<List<BatchConduitRouteResult>> BatchResult { get; private set; }

        private ILogger _logger;

        public RouteConduitsByWaypointsEventHandler()
        {
            try
            {
                _logger = new Logger();
            }
            catch { /* ignore */ }
        }

        public void SetTasks(List<ConduitRouteTask> tasks)
        {
            _batchTasks = tasks ?? new List<ConduitRouteTask>();
            _task = null;
            _reset.Reset();
        }

        public void SetTask(ConduitRouteTask task)
        {
            _task = task;
            _batchTasks = null;
            _reset.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            _logger ??= new Logger();
            var doc = _uiApp.ActiveUIDocument?.Document;

            try
            {
                if (doc == null)
                    throw new InvalidOperationException("沒有可用的 ActiveUIDocument，請先開啟一個專案或視圖。");

                if (_batchTasks != null && _batchTasks.Count > 0)
                {
                    var results = ExecuteBatchWithTx(doc, _batchTasks, _logger);
                    BatchResult = new AIResult<List<BatchConduitRouteResult>>
                    {
                        Success = results.All(r => r.Success),
                        Message = $"Conduit 批次完成：成功 {results.Count(r => r.Success)}，失敗 {results.Count(r => !r.Success)}",
                        Response = results
                    };
                }
                else
                {
                    var created = ExecuteSingleWithTx(doc, _task, _logger);
                    Result = new AIResult<List<int>>
                    {
                        Success = created.Count > 0,
                        Message = $"Conduit 任務完成，建立 {created.Count} 個元素。",
                        Response = created.Select(id => id.IntegerValue).ToList()
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.Error($"[RouteConduits] 任務執行失敗：{ex}");
                Result = new AIResult<List<int>>
                {
                    Success = false,
                    Message = $"任務執行失敗：{ex.Message}",
                    Response = new List<int>()
                };
            }
            finally
            {
                _reset.Set();
            }
        }

        private List<ElementId> ExecuteSingleWithTx(Document doc, ConduitRouteTask task, ILogger logger)
        {
            if (task == null)
                return new List<ElementId>();

            if (doc.IsModifiable)
            {
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    var created = ConduitRoutingCore.RouteConduitsFromTrayTask(doc, task, logger);
                    sub.Commit();
                    return created;
                }
            }
            else
            {
                using (var tx = new Transaction(doc, "Route conduits from tray"))
                {
                    tx.Start();
                    var created = ConduitRoutingCore.RouteConduitsFromTrayTask(doc, task, logger);
                    tx.Commit();
                    return created;
                }
            }
        }

        private List<BatchConduitRouteResult> ExecuteBatchWithTx(
             Document doc,
             List<ConduitRouteTask> tasks,
             ILogger logger)
        {
            var results = new List<BatchConduitRouteResult>();

            using (var tx = new Transaction(doc, "Route conduits from tray (Batch)"))
            {
                tx.Start();

                foreach (var t in tasks)
                {
                    try
                    {
                        var created = ConduitRoutingCore.RouteConduitsFromTrayTask(doc, t, logger);
                        results.Add(new BatchConduitRouteResult
                        {
                            Task = t,
                            Success = created.Count > 0,
                            Message = $"成功建立 {created.Count} 個 Conduit 元件。",
                            CreatedElementIds = created.Select(id => id.IntegerValue).ToList()
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"[RouteConduits][Batch] 單筆任務失敗：{ex}");
                        results.Add(new BatchConduitRouteResult
                        {
                            Task = t,
                            Success = false,
                            Message = $"路由失敗：{ex.Message}"
                        });
                    }
                }

                tx.Commit();
            }
            return results;
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 120000)
            => _reset.WaitOne(timeoutMilliseconds);

        public string GetName() => "Route Conduits From Tray";
    }
}

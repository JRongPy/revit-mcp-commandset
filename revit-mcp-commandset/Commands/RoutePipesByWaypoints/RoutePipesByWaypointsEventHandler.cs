using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing;
using RevitMCPCommandSet.Utils.Routing;
using RevitMCPSDK.API.Interfaces;
using static RevitMCPCommandSet.Services.Routing.RoutingServices;

namespace RevitMCPCommandSet.Commands.RoutePipesByWaypoints
{
    public class RoutePipesByWaypointsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private UIDocument _uiDoc => _uiApp.ActiveUIDocument;
        private Document _doc => _uiDoc.Document;

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

        private readonly string _logDir = @"D:\MCP_Log";
        private readonly string _logFile;

        public RoutePipesByWaypointsEventHandler()
        {
            _logFile = Path.Combine(_logDir, "RoutePipesByWaypoints.log");
            try
            {
                if (!Directory.Exists(_logDir))
                    Directory.CreateDirectory(_logDir);
            }
            catch { /* ignore permission errors */ }
        }

        public void SetTasks(List<RouteTask> tasks)
        {
            _batchTasks = tasks ?? new List<RouteTask>();
            _task = null; // 避免混用
            _reset.Reset();
            WriteLog($"[TASKS SET] Count={_batchTasks.Count}");
        }

        public void SetTask(RouteTask task)
        {
            _task = task;
            _reset.Reset();
            WriteLog($"[TASK SET] StartID={task.StartElementId}, EndID={task.EndElementId}, Waypoints={task.Waypoints?.Count ?? 0}");
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;

            try
            {
                if (_batchTasks != null && _batchTasks.Count > 0)
                {
                    ExecuteBatch();
                    return;
                }

                // === 單筆流程（沿用你現有的 Execute 內容） ===
                ExecuteSingle(_task);
            }
            catch (Exception ex)
            {
                WriteLog($"[Execute][ERROR] {ex}");
                try
                {
                    TaskDialog.Show("RoutePipesByWaypoints", $"任務執行失敗：{ex.Message}");
                }
                catch { /* 可能 Revit UI 不可用時避免再崩 */ }

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

        private void ExecuteBatch()
        {
            var results = new List<BatchRouteResult>();
            WriteLog("===== BATCH EXECUTION START =====");

            foreach (var tsk in _batchTasks)
            {
                try
                {
                    var created = ExecuteSingle(tsk, suppressDialog: true); // 回傳建立的 ElementId 清單
                    results.Add(new BatchRouteResult
                    {
                        Task = tsk,
                        Success = true,
                        Message = $"路由完成，生成 {created.Count} 個元素",
                        CreatedElementIds = created.Select(id => id.IntegerValue).ToList()
                    });
                }
                catch (Exception ex)
                {
                    WriteLog($"[BATCH ITEM ERROR] {ex}");
                    results.Add(new BatchRouteResult
                    {
                        Task = tsk,
                        Success = false,
                        Message = $"路由失敗：{ex.Message}"
                    });
                }
            }

            BatchResult = new AIResult<List<BatchRouteResult>>
            {
                Success = results.All(r => r.Success),
                Message = $"批次完成：成功 {results.Count(r => r.Success)}，失敗 {results.Count(r => !r.Success)}",
                Response = results
            };

            WriteLog($"[BATCH SUMMARY] {BatchResult.Message}");
            WriteLog("===== BATCH EXECUTION END =====\n");
            _reset.Set();
        }



        private List<ElementId> ExecuteSingle(RouteTask task, bool suppressDialog = false)
        {
            var created = new List<ElementId>();
            List<Element> unions = new(); // 若你後續還要清掉暫時接頭

            WriteLog("===== EXECUTION START (Single) =====");
            WriteLog($"Document: {_doc.Title}, Task: {SerializeTask(task)}");

            // === 1) 解析起訖 ===
            var startEle = _doc.GetElement(new ElementId(task.StartElementId));
            var endEle = _doc.GetElement(new ElementId(task.EndElementId));
            if (startEle == null || endEle == null)
                throw new InvalidOperationException("起點或終點元素不存在");
            WriteLog($"Start Element: {DescribeElement(startEle)}");
            WriteLog($"End   Element: {DescribeElement(endEle)}");

            var startKind = Classify(startEle);
            var endKind = Classify(endEle);
            if (startKind == ElementKind.FamilyInstance) ConnectorUtils.EnsureHasConnectors((FamilyInstance)startEle);
            if (endKind == ElementKind.FamilyInstance) ConnectorUtils.EnsureHasConnectors((FamilyInstance)endEle);

            var ctx = InferRoutingContext(_doc, startEle, endEle, task);
            WriteLog($"[CONTEXT] SystemTypeId={ctx.SystemTypeId}, PipeTypeId={ctx.PipeTypeId}, LevelId={ctx.LevelId}, Diameter={ctx.Diameter_ft * 304.8:F1} mm");

            // 3.5) 若沒 Waypoints，自動推論（你原本的行為）
            if (task.Waypoints == null || task.Waypoints.Count == 0)
            {
                var wp = InferWaypointsIfEmpty(_doc, startEle, endEle, ctx);
                WriteLog($"[Waypoints] Inferred {wp.Count} waypoint(s).");
                if (wp.Count == 0)
                    throw new InvalidOperationException("未提供路由途經點，且無法推論，請重新執行路由指令。");
                task.Waypoints.AddRange(wp.Select(p => new JZPoint(p.X * 304.8, p.Y * 304.8, p.Z * 304.8)));
                WriteLog($"[Waypoints] Inferred:newTask: {SerializeTask(task)}");
            }

            using (var t = new Transaction(_doc, "Route Pipes by Waypoints"))
            {
                t.Start();
                var startAnchor = new RoutingAnchor(_doc, startEle, task, true, ctx);
                var endAnchor = new RoutingAnchor(_doc, endEle, task, true, ctx);
                if (startAnchor.CreatedElementIds.Count > 0) created.AddRange(startAnchor.CreatedElementIds);
                if (endAnchor.CreatedElementIds.Count > 0) created.AddRange(endAnchor.CreatedElementIds);

                var path = BuildPathWorldPoints(startAnchor.AnchorPoint, task.Waypoints, endAnchor.AnchorPoint);
                path = NormalizePathPoints(path, ctx.Tolerance_ft, task.Tolerance_deg);

                if (path.Count == 1)
                {
                    try
                    {
                        var eid = PipeUtils.TryCreateElbow(_doc, startAnchor.AnchorElement as Pipe, endAnchor.AnchorElement as Pipe, startAnchor.AnchorPoint);
                        created.Add(eid);
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"[CreateSegments][ERROR] {ex}");
                    }
                }
                else if (path.Count == 2)
                {
                    var segId = SegmentBuilder.CreatePipeSegmentAlignedOrBent(
                        _doc, ctx, startAnchor.AnchorConnector, path[0], path[1],
                        task.MinSegmentLength_mm / 304.8, task.Tolerance_mm / 304.8, created
                    );
                    Pipe pipe = _doc.GetElement(segId) as Pipe;
                    PipeUtils.TryCreateElbow(_doc, pipe, endAnchor.AnchorElement as Pipe, endAnchor.AnchorPoint);
                    created.Add(segId);
                }
                else
                {
                    var segCreated = CreateSegmentsAndFittings(
                        _doc, ctx, startAnchor, endAnchor, path,
                        task.MinSegmentLength_mm / 304.8, task.RoutingPreference, task.Tolerance_mm / 304.8
                    );
                    created.AddRange(segCreated);
                }

                t.Commit();
            }

            WriteLog($"[SUCCESS] Created {created.Count} elements: {string.Join(", ", created)}");
            WriteLog("===== EXECUTION END (Single) =====\n");

            // 單筆模式時把 Result 也一起更新，保持舊 API 行為
            Result = new AIResult<List<int>>
            {
                Success = true,
                Message = $"路由完成，生成 {created.Count} 個元素（管段/彎頭/Tee/Takeoff）",
                Response = created.Select(id => id.IntegerValue).ToList()
            };

            return created;
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 120000) => _reset.WaitOne(timeoutMilliseconds);
        public string GetName() => "Route Pipes by Waypoints";

        // =============== 日誌輔助 =======================
        private void WriteLog(string msg)
        {
            try
            {
                File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {msg}\n");
            }
            catch { /* 忽略可能的權限問題 */ }
        }

        private string SerializeTask(RouteTask t)
        {
            if (t == null) return "null";
            var wp = (t.Waypoints == null || t.Waypoints.Count == 0)
                ? "[]"
                : string.Join(";", t.Waypoints.Select(p => $"({p.X:F1},{p.Y:F1},{p.Z:F1})"));
            return $"Start={t.StartElementId}, End={t.EndElementId}, Waypoints={wp}, MinLen={t.MinSegmentLength_mm}mm, Pref={t.RoutingPreference}";
        }

        private string DescribeElement(Element e)
        {
            if (e == null) return "null";
            string cat = e.Category?.Name ?? "NoCategory";
            return $"{e.Id.IntegerValue} [{e.GetType().Name}] ({cat})";
        }
    }
}

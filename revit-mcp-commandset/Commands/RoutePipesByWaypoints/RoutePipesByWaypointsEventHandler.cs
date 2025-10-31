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
using RevitMCPSDK.API.Interfaces;
using static RevitMCPCommandSet.Services.Routing.RoutingServices;

namespace RevitMCPCommandSet.Commands.RoutePipesByWaypoints
{
    public class RoutePipesByWaypointsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private UIDocument _uiDoc => _uiApp.ActiveUIDocument;
        private Document _doc => _uiDoc.Document;

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

        public void SetTask(RouteTask task)
        {
            _task = task;
            _reset.Reset();
            WriteLog($"[TASK SET] StartID={task.StartElementId}, EndID={task.EndElementId}, Waypoints={task.Waypoints?.Count ?? 0}");
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            var created = new List<int>();

            try
            {
                WriteLog("===== EXECUTION START =====");
                WriteLog($"Document: {_doc.Title}, Task: {SerializeTask(_task)}");

                // 1) 解析起訖
                var startEle = _doc.GetElement(new ElementId(_task.StartElementId));
                var endEle = _doc.GetElement(new ElementId(_task.EndElementId));
                if (startEle == null || endEle == null)
                    throw new InvalidOperationException("起點或終點元素不存在");
                WriteLog($"Start Element: {DescribeElement(startEle)}");
                WriteLog($"End   Element: {DescribeElement(endEle)}");

                // 2) 型別檢查
                var startKind = Classify(startEle);
                var endKind = Classify(endEle);

                if (startKind == ElementKind.FamilyInstance)
                    EnsureHasConnectors((FamilyInstance)startEle);
                if (endKind == ElementKind.FamilyInstance)
                    EnsureHasConnectors((FamilyInstance)endEle);

                // 3) 推斷上下文
                var ctx = InferRoutingContext(_doc, startEle, endEle, _task);
                WriteLog($"[CONTEXT] SystemTypeId={ctx.SystemTypeId.IntegerValue}, PipeTypeId={ctx.PipeTypeId.IntegerValue}, LevelId={ctx.LevelId.IntegerValue}, Diameter={ctx.Diameter_ft * 304.8:F1} mm");

                // 4) 解析 attach point
                var startAttach = ResolveAttachPoint(_doc, startEle, _task, isStart: true, ctx);
                var endAttach = ResolveAttachPoint(_doc, endEle, _task, isStart: false, ctx);
                WriteLog($"StartAttach: {startAttach.AnchorPoint}, EndAttach: {endAttach.AnchorPoint}");

                // 5) 組合路徑
                var path = BuildPathWorldPoints(startAttach.AnchorPoint, _task.Waypoints, endAttach.AnchorPoint);
                WriteLog($"Path Points: {string.Join(" -> ", path.Select(p => p.ToString()))}");

                // 6) 實際建模
                using (var t = new Transaction(_doc, "Route Pipes by Waypoints"))
                {
                    t.Start();
                    var segCreated = CreateSegmentsAndFittings(
                        _doc, ctx, startAttach, endAttach, path,
                        _task.MinSegmentLength_mm / 304.8,
                        _task.RoutingPreference, _task.Tolerance_mm / 304.8
                    );
                    created.AddRange(segCreated.Select(id => id.IntegerValue));
                    t.Commit();
                }

                WriteLog($"[SUCCESS] Created {created.Count} elements: {string.Join(", ", created)}");

                Result = new AIResult<List<int>>
                {
                    Success = true,
                    Message = $"路由完成，生成 {created.Count} 個元素（管段/彎頭/Tee/Takeoff）",
                    Response = created
                };
            }
            catch (Exception ex)
            {
                WriteLog($"[ERROR] {ex}");
                Result = new AIResult<List<int>>
                {
                    Success = false,
                    Message = $"路由失敗：{ex.Message}"
                };
                TaskDialog.Show("Route Pipes", Result.Message);
            }
            finally
            {
                WriteLog("===== EXECUTION END =====\n");
                _reset.Set();
            }
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

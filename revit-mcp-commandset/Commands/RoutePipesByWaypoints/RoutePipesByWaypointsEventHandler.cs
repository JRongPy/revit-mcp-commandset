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
            var created = new List<ElementId>();

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
                    ConnectorUtils.EnsureHasConnectors((FamilyInstance)startEle);
                if (endKind == ElementKind.FamilyInstance)
                    ConnectorUtils.EnsureHasConnectors((FamilyInstance)endEle);

                // 3) 推斷上下文
                var ctx = InferRoutingContext(_doc, startEle, endEle, _task);
                WriteLog($"[CONTEXT] SystemTypeId={ctx.SystemTypeId}, PipeTypeId={ctx.PipeTypeId}, LevelId={ctx.LevelId}, Diameter={ctx.Diameter_ft * 304.8:F1} mm");

                using (var t = new Transaction(_doc, "Route Pipes by Waypoints"))
                {
                    t.Start();
                    // todo 前處理：
                    // 如果 Waypoints == 0 則
                    // 如果兩根管是兩根管，則直接處理(依情況用Elbow/ tee / takeoff等)
                    // 如果兩根管平行則直接報錯，無法判斷
                    // 如果兩根管共線則直接用 NewUnionFitting 連接
                    // 如果是管 + FamilyInstance且平行，則嘗試延伸管直接連接
                    // 如果是管 + FamilyInstance 但不平行，則嘗試從FI生一段管，然後Elbow/ tee / takeoff連接
                    // 如果是都是 FamilyInstance 但不平行，則嘗試從最近的接頭生一段管，然後Elbow/ tee / takeoff連接
                    // 如果是都是 FamilyInstance 且共線，則嘗試從最近的接頭生一段管對接
                    // 否則報錯無法判斷
                    // 如果 Waypoints != 0 則先透過 Waypoints 找出兩端可能的 connector 並且進行 Waypoints 的整理，如果退化到 0 則回到上面邏輯

                    // 4) 建立錨點
                    var startAnchor = new RoutingAnchor(_doc, startEle, _task, true, ctx);
                    var endAnchor = new RoutingAnchor(_doc, endEle, _task, true, ctx);
                    WriteLog($"startAnchor: {startAnchor.AnchorPoint}, endAnchor: {endAnchor.AnchorPoint}");

                    // 5) 組合路徑
                    var path = BuildPathWorldPoints(startAnchor.AnchorPoint, _task.Waypoints, endAnchor.AnchorPoint);
                    WriteLog($"Path Points: {string.Join(" -> ", path.Select(p => p.ToString()))}");

                    // 整理重複的路徑點
                    path = NormalizePathPoints(path, ctx.Tolerance_ft, _task.angleTolerance_deg);
                    WriteLog($"[CreateSegments][NormalizedPts] path point count: {path.Count}");

                    // 6) 實際建模
                    if (path.Count == 1)
                    {
                        // 起訖重合或都被吸收
                        WriteLog("[CreateSegments] path.Count == 1 → 嘗試直接以接頭連接");
                        try
                        {
                            var element = _doc.Create.NewElbowFitting(
                                startAnchor.AnchorConnector,
                                endAnchor.AnchorConnector
                            );
                            created.Add(element.Id);
                        }
                        catch (Exception ex)
                        {
                            WriteLog($"[CreateSegments] [ERROR] {ex}");
                        }
                    }
                    else if (path.Count == 2)
                    {
                        // 最常見：單段直連

                        WriteLog($"[CreateSegments] path.Count == 2 → 直連 {Pt(path[0])} -> {Pt(path[1])}");
                        var segId = SegmentBuilder.CreatePipeSegmentAlignedOrBent(
                            _doc, ctx, startAnchor.AnchorConnector, path[0], path[1],
                            _task.MinSegmentLength_mm / 304.8, _task.Tolerance_mm / 304.8, created // 先建段
                        );
                        Pipe pipe = _doc.GetElement(segId) as Pipe;
                        var lastConn = ConnectorUtils.GetFarConnector(pipe, startAnchor.AnchorPoint);
                        SegmentBuilder.ConnectToTargetEnd(_doc, ctx, lastConn, endAnchor, created, _task.Tolerance_mm / 304.8);
                        created.Add(segId);
                    }
                    else
                    {
                        var segCreated = CreateSegmentsAndFittings(
                             _doc, ctx, startAnchor, endAnchor, path,
                             _task.MinSegmentLength_mm / 304.8,
                             _task.RoutingPreference, _task.Tolerance_mm / 304.8
                         );
                        created.AddRange(segCreated);
                    }

                    t.Commit();
                }

                WriteLog($"[SUCCESS] Created {created.Count} elements: {string.Join(", ", created)}");
                Result = new AIResult<List<int>>
                {
                    Success = true,
                    Message = $"路由完成，生成 {created.Count} 個元素（管段/彎頭/Tee/Takeoff）",
                    Response = created.Select(id => id.IntegerValue).ToList()
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

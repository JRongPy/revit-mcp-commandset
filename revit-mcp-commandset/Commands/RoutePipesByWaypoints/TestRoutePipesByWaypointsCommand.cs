using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMCPCommandSet.Commands.RoutePipesByWaypoints;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Policy;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.RoutePipesByWaypoints
{
    [Transaction(TransactionMode.Manual)]
    public class TestRoutePipesByWaypointsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            var logger = new Logger();

            try
            {
                // 1) 讓使用者選兩個元素 (Pipe 或 Family)
                var (startEl, endEl) = EnsureTwoElements(uidoc);
                XYZ pStart = GetRepresentativePoint(startEl);
                XYZ pEnd = GetRepresentativePoint(endEl);

                // 2) 建立簡單的 RouteTask
                var task = new RouteTaskInfo
                {
                    StartElementId = startEl.Id.IntegerValue,
                    EndElementId = endEl.Id.IntegerValue,
                    Waypoints = new List<JZPoint> {},
                    MinSegmentLengthMm = 1500,
                    RoutingPreference = "Tee",
                    ToleranceMm = 10
                };                   
                if (false)
                {
                    var wp = new JZPoint(x: 10 * 304.8, y: 30 * 304.8, z: pStart.Z * 304.8);
                    task.Waypoints.Add(wp);
                }

                // 3) 執行事件處理器（內含 Transaction）
                var handler = new RoutePipesByWaypointsEventHandler();
                handler.SetTask(task);
                handler.Execute(uiapp);

                // 4) 顯示結果
                var result = handler.Result;
                if (result != null && result.Success)
                {
                    TaskDialog.Show("Route Pipes", $"{result.Message}\n已建立元素: {string.Join(", ", result.Response ?? new List<int>())}");
                    return Result.Succeeded;
                }
                else
                {
                    TaskDialog.Show("Route Pipes", $"路由失敗：{result?.Message ?? "未知錯誤"}");
                    logger.Info($"路由失敗：{result?.Message ?? "未知錯誤"}");
                    return Result.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                logger.Info($"[Route Pipes Test - Exception] {ex}");
                TaskDialog.Show("Route Pipes Test - Exception", message);
                return Result.Failed;
            }
        }

        // ---------- Helpers ----------

        private static (Element, Element) EnsureTwoElements(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var selected = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e is Pipe || e is FamilyInstance)
                .ToList();

            while (selected.Count < 2)
            {
                var sel = uidoc.Selection.PickObject(ObjectType.Element, new PickFilter(),
                    $"請選取第 {selected.Count + 1} 個元素（Pipe 或具 MEP Connector 的 Family）");
                var el = doc.GetElement(sel.ElementId);
                if (el is Pipe || el is FamilyInstance)
                    selected.Add(el);
            }

            return (selected[0], selected[1]);
        }

        private static XYZ GetRepresentativePoint(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            if (e?.Location is LocationCurve lc) return lc.Curve?.Evaluate(0.5, true) ?? XYZ.Zero;
            var bb = e?.get_BoundingBox(null);
            return (bb != null) ? (bb.Min + bb.Max) * 0.5 : XYZ.Zero;
        }

        private class PickFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe || elem is FamilyInstance;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}

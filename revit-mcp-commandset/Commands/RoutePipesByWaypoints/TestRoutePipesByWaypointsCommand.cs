using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMCPCommandSet.Commands.RoutePipesByWaypoints; // 取用你的 EventHandler
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMCPCommandSet.Commands.RoutePipesByWaypoints
{
    /// <summary>
    /// 快速自測用的外部指令（不經 MCP）。
    /// 使用方式：
    /// 1) 在 Revit 選兩個元素（Pipe 或 FamilyInstance）
    /// 2) 執行本指令（或未選滿就依提示點兩個）
    /// 3) 會建立一個 L 形路由（起點 → waypoint(起點+X:1200mm) → 終點），管徑/型別/系統/標高由你的 RoutingServices 推斷或 override。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class TestRoutePipesByWaypointsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            try
            {
                // 1) 取得起訖元素（優先使用目前選取；不足則提示點選）
                var picked = EnsureTwoElements(uidoc);
                var startEl = picked.Item1;
                var endEl = picked.Item2;

                // 2) 建立一個最小可跑的 RouteTask（L 形：往 X 正方向 +1200mm，再轉去終點）
                //    waypoint 的 Z：取起點代表高度（Location 或 BBox 中心）
                XYZ startRef = GetRepresentativePoint(startEl);
                XYZ endRef = GetRepresentativePoint(endEl);

                double zStart_mm = startRef.Z * 304.8;
                var task = new RouteTask
                {
                    StartElementId = startEl.Id.IntegerValue,
                    EndElementId = endEl.Id.IntegerValue,
                    Waypoints = new List<JZPoint>
                    {
                        new JZPoint(
                            x: (startRef.X + (1200.0/304.8)) * 304.8, // 直接給 1200 mm 偏移
                            y: (startRef.Y + (0.0/304.8))   * 304.8,
                            z: zStart_mm
                        )
                    },
                    MinSegmentLength_mm = 100,       // 10 cm
                    RoutingPreference = "Tee",       // 或 "Takeoff"
                    Tolerance_mm = 10,               // 10 mm
                    // 如需強制覆寫可打開下方範例：
                    // Override = new RouteTask.OverrideBlock
                    // {
                    //     PipeTypeId = 7950221,
                    //     SystemTypeId = 7950130,
                    //     LevelId = 1522842,
                    //     Diameter_mm = 100
                    // }
                };

                // 3) 直接使用你現有的 EventHandler 執行（不需 ExternalEvent Raise）
                var handler = new RoutePipesByWaypointsEventHandler();
                handler.SetTask(task);

                // 注意：handler.Execute 內部會自行開 Transaction 並回填 Result
                handler.Execute(uiapp);

                var result = handler.Result;
                if (result != null && result.Success)
                {
                    TaskDialog.Show("RoutePipes Test",
                        $"{result.Message}\n" +
                        $"Start: {task.StartElementId}, End: {task.EndElementId}\n" +
                        $"Created: {string.Join(", ", result.Response ?? new List<int>())}");
                    return Result.Succeeded;
                }
                else
                {
                    message = result?.Message ?? "Route failed (no result).";
                    TaskDialog.Show("RoutePipes Test - Failed", message);
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
                TaskDialog.Show("RoutePipes Test - Exception", message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// 取得兩個元素：先用目前選取；不足則讓使用者點兩個支持的元素（Pipe / FamilyInstance）
        /// </summary>
        private static Tuple<Element, Element> EnsureTwoElements(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var selIds = uidoc.Selection.GetElementIds().ToList();

            // 過濾：Pipe or FamilyInstance
            List<Element> selected = selIds
                .Select(id => doc.GetElement(id))
                .Where(e => e is Pipe || e is FamilyInstance)
                .ToList();

            var picked = new List<Element>(selected);

            while (picked.Count < 2)
            {
                var refObj = uidoc.Selection.PickObject(ObjectType.Element, new MEPHostOrPipeFilter(),
                    $"請選擇第 {picked.Count + 1} 個元素（Pipe 或具 MEP Connector 的 Family）");
                var e = doc.GetElement(refObj.ElementId);
                if (e is Pipe || e is FamilyInstance) picked.Add(e);
            }

            return Tuple.Create(picked[0], picked[1]);
        }

        /// <summary>
        /// 回傳元素的代表點：LocationPoint/Curve 中點，或 BBox 中心。
        /// </summary>
        private static XYZ GetRepresentativePoint(Element e)
        {
            if (e?.Location is LocationPoint lp) return lp.Point;
            if (e?.Location is LocationCurve lc)
            {
                var c = lc.Curve;
                return c.Evaluate(0.5, true);
            }
            var bbox = e?.get_BoundingBox(null);
            if (bbox != null)
            {
                return (bbox.Min + bbox.Max) * 0.5;
            }
            return XYZ.Zero;
        }

        /// <summary>
        /// 僅允許選 Pipe 或 FamilyInstance 的選取過濾器。
        /// </summary>
        private class MEPHostOrPipeFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => (elem is Pipe) || (elem is FamilyInstance);
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}

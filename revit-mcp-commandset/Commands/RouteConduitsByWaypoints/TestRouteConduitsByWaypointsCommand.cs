// Commands/RouteConduitsByWaypoints/TestRouteConduitsByWaypointsCommand.cs
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services.Routing.Conduits;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Commands.RouteConduitsByWaypoints
{
    [Transaction(TransactionMode.Manual)]
    public class TestRouteConduitsByWaypointsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;
            ILogger logger = new Logger();

            try
            {
                // 1) 讓使用者選 tray + endpoint
                var (tray, endpoint) = EnsureTrayAndEndpoint(uidoc);

                logger.Info($"[TestConduit] Tray={tray.Id.IntegerValue}, Endpoint={endpoint.Id.IntegerValue}");

                // 2) 建立簡單 ConduitRouteTask
                var task = new ConduitRouteTaskInfo
                {
                    StartElementId = tray.Id.IntegerValue,
                    EndElementId = endpoint.Id.IntegerValue,
                    ConduitDiameterMm = 53,
                };
                if (true)  // 範例 waypoint：直接在 endpoint 上方建立一個 waypoint
                {
                    XYZ p1 = GetCenterOfElement(tray);
                    XYZ p2 = (endpoint as FamilyInstance)?.MEPModel.ConnectorManager.Connectors.Cast<Connector>().OrderByDescending(c => c.Origin.Z).FirstOrDefault().Origin;
                    var wp = new JZPoint(x: p2.X * 304.8, y: p2.Y * 304.8, z: p1.Z * 304.8);
                    task.Waypoints.Add(wp);
                }

                if (false)  //
                {
                    XYZ p1 = GetCenterOfElement(tray);
                    XYZ p2 = (endpoint as FamilyInstance)?.MEPModel.ConnectorManager.Connectors.Cast<Connector>().OrderByDescending(c => c.Origin.Z).FirstOrDefault().Origin;
                    var wp1 = new JZPoint(x: p2.X * 304.8, y: (p1.Y - (p1.Y - p2.Y)/2) * 304.8, z: p1.Z * 304.8);
                    task.Waypoints.Add(wp1);
                    var wp2 = new JZPoint(x: p2.X * 304.8, y: p2.Y * 304.8, z: p1.Z * 304.8 / 2);
                    task.Waypoints.Add(wp2);
                }


                // 3) 直接呼叫 handler（內含 Transaction）
                var handler = new RouteConduitsByWaypointsEventHandler();
                handler.SetTask(task);
                handler.Execute(uiapp);

                // 4) 顯示結果
                var result = handler.Result;
                if (result != null && result.Success)
                {
                    TaskDialog.Show(
                        "Route Conduits",
                        $"{result.Message}\n建立元素: {string.Join(", ", result.Response ?? new List<int>())}"
                    );
                    return Result.Succeeded;
                }
                else
                {
                    TaskDialog.Show(
                        "Route Conduits",
                        $"路由失敗：{result?.Message ?? "未知錯誤"}"
                    );
                    return Result.Failed;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Route Conduits - Error", ex.ToString());
                return Result.Failed;
            }
        }

        // ---------- Helpers ----------

        private static (Element tray, Element endpoint) EnsureTrayAndEndpoint(UIDocument uidoc)
        {
            var doc = uidoc.Document;

            var selected = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .ToList();

            Element tray = selected.FirstOrDefault(e => e is CableTray);
            Element endpoint = selected.FirstOrDefault(e =>
                !(e is CableTray) && (e is FamilyInstance || e is Conduit)
            );

            while (tray == null)
            {
                var r = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new TrayPickFilter(),
                    "請選取 CableTray 作為起點"
                );
                var el = doc.GetElement(r.ElementId);
                if (el is CableTray)
                    tray = el;
            }

            while (endpoint == null)
            {
                var r = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new EndpointPickFilter(),
                    "請選取 Endpoint（具 MEP Connector 的 Family 或既有 Conduit）"
                );
                var el = doc.GetElement(r.ElementId);
                if (!(el is CableTray) && (el is FamilyInstance || el is Conduit))
                    endpoint = el;
            }

            return (tray, endpoint);
        }

        private class TrayPickFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is CableTray;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private class EndpointPickFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
                => !(elem is CableTray) && (elem is FamilyInstance || elem is Conduit);

            public bool AllowReference(Reference reference, XYZ position) => true;
        }
        private static XYZ GetCenterOfElement(Element e)
        {
            if (e?.Location is LocationPoint lp)
                return lp.Point;

            if (e?.Location is LocationCurve lc)
                return lc.Curve?.Evaluate(0.5, true) ?? XYZ.Zero;

            var bb = e?.get_BoundingBox(null);
            if (bb != null)
                return (bb.Min + bb.Max) * 0.5;

            return XYZ.Zero;
        }
    }
}

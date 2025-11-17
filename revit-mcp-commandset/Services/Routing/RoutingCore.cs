using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Routing;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static RevitMCPCommandSet.Commands.RoutePipesByWaypoints.RoutePipesByWaypointsEventHandler;
using static RevitMCPCommandSet.Services.Routing.RoutingServices;

namespace RevitMCPCommandSet.Services.Routing
{
    public static class RoutingCore
    {
        /// <summary>
        /// Route Pipes 單筆任務，沒有交易包裝，由handler負責交易     
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="task"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static List<ElementId> RoutePipesTask(Document doc, RouteTaskInfo task, ILogger logger)
        {
            var created = new List<ElementId>();
            List<Element> unions = new(); // 若你後續還要清掉暫時接頭

            logger.Info(" ===== EXECUTION START (Single) =====");
            logger.Info($"Document: {doc.Title}, Task: {RouteLoggerHelper.SerializeTask(task)}");

            // === 1) 解析起訖 ===
            var startEle = doc.GetElement(new ElementId(task.StartElementId));
            var endEle = doc.GetElement(new ElementId(task.EndElementId));
            if (startEle == null || endEle == null)
                throw new InvalidOperationException("起點或終點元素不存在");
            logger.Info($"Start Element: {RouteLoggerHelper.DescribeElement(startEle)}");
            logger.Info($"End   Element: {RouteLoggerHelper.DescribeElement(endEle)}");

            var startKind = Classify(startEle);
            var endKind = Classify(endEle);
            if (startKind == ElementKind.FamilyInstance) ConnectorUtils.EnsureHasConnectors((FamilyInstance)startEle);   // 檢查有沒有Connector
            if (endKind == ElementKind.FamilyInstance) ConnectorUtils.EnsureHasConnectors((FamilyInstance)endEle);    // 檢查有沒有Connector

            var ctx = InferRoutingContext(doc, startEle, endEle, task);
            logger.Info($"[CONTEXT] SystemTypeId={ctx.SystemTypeId}, PipeTypeId={ctx.PipeTypeId}, LevelId={ctx.LevelId}, Diameter={ctx.Diameter_ft * 304.8:F1} mm");

            // 處理 Waypoints，將跟起訖點位置一樣的點移除，以利後續推論判斷
            if (task.Waypoints != null && task.Waypoints.Count > 0)
            {
                var wpXYZ = task.Waypoints
                    .Select(p => JZPoint.ToXYZ(p))
                    .ToList();
                var startPt = ConnectorUtils.GetNearConnector(startEle, wpXYZ.FirstOrDefault()).Origin;
                var endPt = ConnectorUtils.GetNearConnector(startEle, wpXYZ.LastOrDefault()).Origin;
                RoutingServices.RemoveNearEndpointsInPlace(wpXYZ, startPt, endPt, ctx.Tolerance_ft);
                task.Waypoints = wpXYZ
                    .Select(p => new JZPoint(p.X * 304.8, p.Y * 304.8, p.Z * 304.8))
                    .ToList();
            }

            // 3) 若沒 Waypoints，自動推論（你原本的行為）
            if (task.Waypoints == null || task.Waypoints.Count == 0)
            {
                var wp = InferWaypointsIfEmpty(doc, startEle, endEle, ctx);
                logger.Info($"[Waypoints] Inferred {wp.Count} waypoint(s).");
                if (wp.Count == 0)
                    throw new InvalidOperationException("未提供路由途經點，且無法推論，請重新執行路由指令。");
                task.Waypoints.AddRange(wp.Select(p => new JZPoint(p.X * 304.8, p.Y * 304.8, p.Z * 304.8)));
                logger.Info($"[Waypoints] Inferred:newTask: {RouteLoggerHelper.SerializeTask(task)}");
            }

            var startAnchor = new RoutingAnchor(doc, startEle, task, true, ctx);
            var endAnchor = new RoutingAnchor(doc, endEle, task, false, ctx);
            if (startAnchor.CreatedElementIds.Count > 0) created.AddRange(startAnchor.CreatedElementIds);
            if (endAnchor.CreatedElementIds.Count > 0) created.AddRange(endAnchor.CreatedElementIds);

            var path = BuildPathWorldPoints(startAnchor.AnchorPoint, task.Waypoints, endAnchor.AnchorPoint);
            path = NormalizePathPoints(path, ctx.Tolerance_ft, task.ToleranceDeg);
            logger.Info($"[BuildPath][Final] {string.Join(" -> ", path.Select(Pt))}");

            if (path.Count == 1)
            {
                try
                {
                    var eid = PipeUtils.TryCreateElbow(doc, startAnchor.AnchorElement as Pipe, endAnchor.AnchorElement as Pipe, startAnchor.AnchorPoint);
                    created.Add(eid);
                }
                catch (Exception ex)
                {
                    logger.Info($"[CreateSegments][ERROR] {ex}");
                }
            }
            else if (path.Count == 2)
            {
                var segId = SegmentBuilder.CreatePipeSegmentAlignedOrBent(
                    doc, ctx, startAnchor.AnchorConnector, path[0], path[1],
                    task.MinSegmentLengthMm / 304.8, task.ToleranceMm / 304.8, created
                );
                Pipe pipe = doc.GetElement(segId) as Pipe;
                PipeUtils.TryCreateElbow(doc, pipe, endAnchor.AnchorElement as Pipe, endAnchor.AnchorPoint);
                created.Add(segId);
            }
            else
            {
                var segCreated = CreateSegmentsAndFittings(
                    doc, ctx, startAnchor, endAnchor, path,
                    task.MinSegmentLengthMm / 304.8, task.RoutingPreference, task.ToleranceMm / 304.8
                );
                created.AddRange(segCreated);
            }

     
            logger.Info($"[SUCCESS] Created {created.Count} elements: {string.Join(", ", created)}");
            logger.Info("===== EXECUTION END (Single) =====\n");

            return created;
        }
    }

}

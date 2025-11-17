// Services/Routing/Conduits/ConduitRoutingCore.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Routing;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Services.Routing.Conduits
{
    /// <summary>
    /// 單筆 conduit routing 核心：
    /// 目前只做：根據 Start/End element 建出兩段「ConduitAnchor」，
    /// 之後再把這兩個 anchor 交給真正的 Routing/SegmentBuilder。
    /// </summary>
    public static class ConduitRoutingCore
    {
        public static List<ElementId> RouteConduitsFromTrayTask(
            Document doc,
            ConduitRouteTaskInfo taskInfo,
            ILogger logger)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (taskInfo == null) throw new ArgumentNullException(nameof(taskInfo));

            /// =========================
            /// ========前置處理=========
            /// =========================

            // 0) 初始化 logger
            logger ??= new Logger();
            logger.Info("===== ConduitRoutingCore - EXECUTION START =====");
            logger.Info($"[Task] StartElementId={taskInfo.StartElementId}, EndElementId={taskInfo.EndElementId}");

            // 1) 解析起訖
            var startEle = doc.GetElement(new ElementId(taskInfo.StartElementId));
            var endEle = doc.GetElement(new ElementId(taskInfo.EndElementId));   

            if (startEle == null || endEle == null)
                throw new InvalidOperationException("起點或終點元素不存在");
            logger.Info($"Start Element: {RouteLoggerHelper.DescribeElement(startEle)}");
            logger.Info($"End   Element: {RouteLoggerHelper.DescribeElement(endEle)}");

            // 如果是FamilyInstance 則檢查有沒有Connector
            if (startEle is FamilyInstance sfi)
            {
                ConnectorUtils.EnsureHasConnectors(sfi);
            }
            if (endEle is FamilyInstance efi) 
            {
                ConnectorUtils.EnsureHasConnectors(efi);
            }

            // 2) 推論 Context
            var ctx = ConduitRoutingServices.InferRoutingContext(doc, startEle, endEle, taskInfo);


            // 3) 處理 Waypoints
            if (taskInfo.Waypoints != null && taskInfo.Waypoints.Count > 0)
            {
                // todo : 移除與起訖點一樣的waypoint
                //  ConduitRoutingServices.RemoveNearEndpointsInPlace 尚未實作

                /* 
                var wpXYZ = task.Waypoints
                    .Select(p => JZPoint.ToXYZ(p))
                    .ToList();
                var startPt = ConnectorUtils.GetNearConnector(startEle, wpXYZ.FirstOrDefault()).Origin;
                var endPt = ConnectorUtils.GetNearConnector(startEle, wpXYZ.LastOrDefault()).Origin;
                ConduitRoutingServices.RemoveNearEndpointsInPlace(wpXYZ, startPt, endPt, ctx.Tolerance_ft);
                task.Waypoints = wpXYZ
                    .Select(p => new JZPoint(p.X * 304.8, p.Y * 304.8, p.Z * 304.8))
                    .ToList();
                */
            }

            // 4) 若沒 Waypoints，自動推論（你原本的行為）
            if (taskInfo.Waypoints == null || taskInfo.Waypoints.Count == 0)
            {
                // todo : 推論 waypoint
                // ConduitRoutingServices.InferWaypointsIfEmpty 尚未實作
                
                /* 
                var wp = ConduitRoutingServices.InferWaypointsIfEmpty(doc, startEle, endEle, ctx);
                logger.Info($"[Waypoints] Inferred {wp.Count} waypoint(s).");
                if (wp.Count == 0)
                    throw new InvalidOperationException("未提供路由途經點，且無法推論，請重新執行路由指令。");
                task.Waypoints.AddRange(wp.Select(p => new JZPoint(p.X * 304.8, p.Y * 304.8, p.Z * 304.8)));
                logger.Info($"[Waypoints] Inferred:newTask: {RouteLoggerHelper.SerializeTask(task)}");
                */
            }

            /// =========================
            /// ========規劃路由=========
            /// =========================
            var created = new List<ElementId>();

            // 1) 建構 anchor 物件
            // 這部分已經完成可以運行
            var startAnchor = new ConduitRoutingAnchor(doc, startEle, taskInfo, true, ctx);
            var endAnchor = new ConduitRoutingAnchor(doc, endEle, taskInfo, false, ctx);
            created.Add(startAnchor.AnchorElement.Id);
            created.Add(endAnchor.AnchorElement.Id);

            logger.Info($"[RoutingAnchor] StartAnchor {startAnchor.AnchorElement.Id}, Diameter {(startAnchor.AnchorElement as Conduit).Diameter*304.8}mm");
            logger.Info($"[RoutingAnchor] EndAnchor {endAnchor.AnchorElement.Id}, Diameter {(endAnchor.AnchorElement as Conduit).Diameter * 304.8}mm");

            // 2) 建構路徑
            List<XYZ> path;
            // 2.1) 建構路徑
            path = ConduitRoutingServices.BuildPathWorldPoints(startAnchor.AnchorPoint, taskInfo.Waypoints, endAnchor.AnchorPoint);   // 可與Pipe共用
            // 2.2) todo :整理路徑
            path = ConduitRoutingServices.NormalizePathPoints(path, ctx.ToleranceFt, ctx.ToleranceDeg);  // 可與Pipe共用
            logger.Info($"[BuildPath][Final] {string.Join(" -> ", path.Select(ConduitRoutingServices.Pt))}");

            // 3) 依waypoints數量規劃決定生成模式
            if (path.Count < 1)
            {
                throw new InvalidOperationException("路徑無法產生點位，無法進行佈管。");
            }
            else if (path.Count == 1)
            {
                logger.Info($"[CreateSegments][Start]單點");
                // 1 pt => 嘗試直接建立彎頭(失敗不中斷)
                try
                {
                    var eid = ConduitUtils.TryCreateElbow(doc, startAnchor.AnchorElement as Conduit, endAnchor.AnchorElement as Conduit, startAnchor.AnchorPoint);
                    created.Add(eid);
                }
                catch (Exception ex)
                {
                    logger.Info($"[CreateSegments][ERROR] {ex}");
                }

            }
            else if (path.Count == 2)
            {
                logger.Info($"[CreateSegments][Start]雙點");
                //  2 pts => 建管後建立彎頭
                var segId = ConduitSegmentBuilder.CreateConduitSegmentAlignedOrBent(
                    doc, ctx, startAnchor.AnchorConnector, path[0], path[1], created);
                Conduit conduit = doc.GetElement(segId) as Conduit;
                ConduitUtils.TryCreateElbow(doc, conduit, endAnchor.AnchorElement as Conduit, endAnchor.AnchorPoint);
                created.Add(segId);
            }
            else
            {
                logger.Info($"[CreateSegments][Start]多點");
                // 多個 => 建管後建立彎頭
                var segCreated = ConduitRoutingServices.CreateSegmentsAndFittings(
                    doc, ctx, startAnchor, endAnchor, path);
                created.AddRange(segCreated);
            }

            logger.Info($"[SUCCESS] Created {created.Count} elements: {string.Join(", ", created)}");
            logger.Info("===== EXECUTION END (Single) =====\n");

            return created;
        }
    }
}

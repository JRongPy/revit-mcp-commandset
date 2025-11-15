// Services/Routing/Conduits/ConduitRoutingCore.cs
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
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
            ConduitRouteTask task,
            ILogger logger)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (task == null) throw new ArgumentNullException(nameof(task));

            logger ??= new Logger();

            logger.Info("===== ConduitRoutingCore - EXECUTION START =====");
            logger.Info($"[Task] StartElementId={task.StartElementId}, EndElementId={task.EndElementId}");

            var startEle = doc.GetElement(new ElementId(task.StartElementId));
            var endEle = doc.GetElement(new ElementId(task.EndElementId));

            if (startEle == null)
                throw new InvalidOperationException($"StartElementId={task.StartElementId} 找不到對應元素。");
            if (endEle == null)
                throw new InvalidOperationException($"EndElementId={task.EndElementId} 找不到對應元素。");

            logger.Info($"Start Element: {RouteLoggerHelper.DescribeElement(startEle)}");
            logger.Info($"End   Element: {RouteLoggerHelper.DescribeElement(endEle)}");

            var created = new List<ElementId>();

            // 1) 從 tray 端長出第一段 Conduit anchor
            var trayAnchor = ConduitAnchorResolver.CreateTrayAnchor(doc, startEle, endEle, logger);
            created.Add(trayAnchor.AnchorConduit.Id);

            // 2) 從 endpoint 端也長出一段 Conduit anchor（暫時不做對接，只是 anchor stub）
            var endpointAnchor = ConduitAnchorResolver.CreateEndpointAnchor(doc, startEle, endEle, logger);
            created.Add(endpointAnchor.AnchorConduit.Id);

            logger.Info($"[SUCCESS] Created {created.Count} conduit anchor(s): {string.Join(", ", created)}");
            logger.Info("===== ConduitRoutingCore - EXECUTION END =====");

            return created;
        }
    }
}

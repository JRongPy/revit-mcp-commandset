using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMCPCommandSet.Services.Routing
{
    internal static class SegmentBuilder
    {
        public static ElementId CreatePipeSegmentAlignedOrBent(
            Document doc, RoutingContext ctx, Connector currentConnector,
            XYZ fromPoint, XYZ toPoint, double minSegmentLen_ft, double tol_ft, List<ElementId> acc)
        {
            var start = (currentConnector?.Origin ?? fromPoint);
            var dirWanted = (toPoint - start).Normalize();

            var connDir = currentConnector?.CoordinateSystem?.BasisZ?.Normalize();
            bool aligned = connDir != null && connDir.IsAlmostEqualTo(dirWanted, 1e-6);

            if (!aligned && connDir != null)
            {
                // 先沿接頭方向推出一小段，再轉向目標
                var kickEnd = start + connDir.Multiply(minSegmentLen_ft);
                var p1 = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, start, kickEnd);
                SetPipeDiameter(p1, ctx.Diameter_ft);
                acc?.Add(p1.Id);

                // 第二段朝目標
                var p2 = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, kickEnd, toPoint);
                SetPipeDiameter(p2, ctx.Diameter_ft);
                acc?.Add(p2.Id);

                // 在兩段相接點放「彎頭」
                var c1 = ConnectorUtils.GetPipeConnectors(p1).OrderBy(c => c.Origin.DistanceTo(kickEnd)).Last();
                var c2 = ConnectorUtils.GetPipeConnectors(p2).OrderBy(c => c.Origin.DistanceTo(kickEnd)).First();
                doc.Create.NewElbowFitting(c1, c2);

                return p2.Id;
            }
            else
            {
                // 方向一致：直接打一段
                var p = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, start, toPoint);
                SetPipeDiameter(p, ctx.Diameter_ft);
                acc?.Add(p.Id);
                return p.Id;
            }
        }

        public static Connector GetFarEndConnector(ElementId pipeId, Connector nearEnd)
        {
            var doc = nearEnd?.Owner?.Document;
            var pipe = (doc?.GetElement(pipeId) as Pipe) ?? (nearEnd?.Owner as Pipe);
            if (pipe == null) return null;
            return ConnectorUtils.GetFarEndConnector(pipe, nearEnd);
        }

        public static void ConnectToTargetEnd(
            Document doc, RoutingContext ctx, Connector lastConnector,
            RoutingServices.AttachPoint end, List<ElementId> acc, double tol_ft, string pref)
        {
            if (end.Connector != null)
            {
                // 與終點connector距離遠 → 補一小段再用彎頭連接；近 → 直接彎頭
                var near = lastConnector;
                var far = end.Connector;

                if (near.Origin.DistanceTo(far.Origin) > tol_ft)
                {
                    var p = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, near.Origin, far.Origin);
                    SetPipeDiameter(p, ctx.Diameter_ft);
                    acc?.Add(p.Id);

                    var pNear = ConnectorUtils.GetPipeConnectors(p)
                        .OrderBy(c => c.Origin.DistanceTo(near.Origin)).First();
                    doc.Create.NewElbowFitting(near, pNear);

                    var pFar = ConnectorUtils.GetFarEndConnector(p, pNear);
                    if (pFar != null) doc.Create.NewElbowFitting(pFar, far);
                }
                else
                {
                    doc.Create.NewElbowFitting(near, far);
                }
                return;
            }

            // 終點無connector → 打最後一段到 anchor point（必要時後續再行處理轉接）
            var finalPipe = Pipe.Create(doc, ctx.SystemTypeId, ctx.PipeTypeId, ctx.LevelId, lastConnector.Origin, end.AnchorPoint);
            SetPipeDiameter(finalPipe, ctx.Diameter_ft);
            acc?.Add(finalPipe.Id);
        }

        private static void SetPipeDiameter(Pipe p, double diameterFt)
        {
            if (diameterFt > 0)
            {
                var diaParam = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diaParam != null && !diaParam.IsReadOnly)
                    diaParam.Set(diameterFt);
            }
        }
    }
}

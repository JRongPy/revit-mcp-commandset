using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Utils.Routing;
using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Services.Routing.Conduits
{
    internal class ConduitSegmentBuilder
    {
        /// <summary>
        /// 從 currentConnector 朝 toPoint 佈線：
        /// - 若 currentConnector 的方向與 (toPoint - fromPoint) 大致一致 → 直接打一段 conduit
        /// - 若方向不一致：
        ///   - 若 currentConnector.Owner 是 Conduit → 新打一段 conduit 接上去，並嘗試建立 elbow
        ///   - 若 Owner 是 FamilyInstance 等 → 先按接頭方向踢出一小段，再轉向目標，再試著建立 elbow
        ///
        /// 回傳：最後一段 Conduit 的 ElementId。
        /// </summary>
        public static ElementId CreateConduitSegmentAlignedOrBent(
            Document doc,
            ConduitRoutingContext ctx,
            Connector currentConnector,
            XYZ fromPoint,
            XYZ toPoint,
            List<ElementId> createdElementIds)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (currentConnector == null && fromPoint == null)
                throw new ArgumentNullException(nameof(currentConnector),
                    "currentConnector 與 fromPoint 不可同時為 null");

            // 起點：優先從 currentConnector.Origin 取，沒有就用 fromPoint
            var start = (currentConnector?.Origin ?? fromPoint);
            if (start == null) throw new InvalidOperationException("無法決定 conduit 段起點座標");

            var dirWanted = (toPoint - start);
            if (dirWanted.IsZeroLength())
            {
                // 起訖同點，直接返回目前 owner Id（或 Invalid）
                return currentConnector?.Owner?.Id ?? ElementId.InvalidElementId;
            }
            dirWanted = dirWanted.Normalize();

            var connDir = currentConnector?.CoordinateSystem?.BasisZ?.Normalize();
            bool aligned = connDir != null && Math.Abs(connDir.DotProduct(dirWanted)) > 0.99;
            bool isConduitOwner = currentConnector?.Owner is Conduit;

            // ========================
            // case A: 目前接頭在 conduit 上
            // ========================
            if (isConduitOwner)
            {
                var currentConduit = (Conduit)currentConnector.Owner;

                if (!aligned)
                {
                    // 方向不一致：另打一段 conduit 接到目標，再用 elbow/union 與原 conduit 接起來
                    var newConduit = Conduit.Create(doc, ctx.ConduitTypeId, start, toPoint, ctx.LevelId);
                    ConduitUtils.SetConduitDiameter(newConduit, ctx.DiameterFt);
                    createdElementIds?.Add(newConduit.Id);

                    // 建立 elbow 或 union（視幾何與尺寸而定）
                    var elbowId = ConduitUtils.TryCreateElbow(doc, newConduit, currentConduit, start);
                    if (elbowId != ElementId.InvalidElementId)
                        createdElementIds?.Add(elbowId);

                    return newConduit.Id;
                }
                else
                {
                    // 方向一致：直接把接頭拉到 toPoint
                    currentConnector.Origin = toPoint;
                    return currentConnector.Owner.Id;
                }
            }

            // ========================
            // case B: 目前接頭不在 conduit 上（多半是 FamilyInstance 的 connector）
            // ========================
            if (!aligned && connDir != null)
            {
                // 方向不一致：先沿接頭方向踢出一小段，再轉向目標

                // 第一段：start → kickEnd（沿接頭方向）
                var kickEnd = start + connDir.Multiply(ctx.MinSegmentLengthFt);
                var c1 = Conduit.Create(doc, ctx.ConduitTypeId, start, kickEnd, ctx.LevelId);
                ConduitUtils.SetConduitDiameter(c1, ctx.DiameterFt);
                createdElementIds?.Add(c1.Id);

                // 嘗試把 host connector 接到新的 conduit 上（可失敗不影響主流程）
                try
                {
                    var c1Conn = ConnectorUtils.GetNearConnector(c1, start);
                    if (c1Conn != null && currentConnector != null &&
                        !c1Conn.IsConnected && !currentConnector.IsConnected)
                    {
                        currentConnector.ConnectTo(c1Conn);
                    }
                }
                catch
                {
                    // 連接失敗不影響主流程
                }

                // 第二段：kickEnd → toPoint（朝向目標）
                var c2 = Conduit.Create(doc, ctx.ConduitTypeId, kickEnd, toPoint, ctx.LevelId);
                ConduitUtils.SetConduitDiameter(c2, ctx.DiameterFt);
                createdElementIds?.Add(c2.Id);

                // 在兩段 conduit 交界附近嘗試建立 elbow
                var elbowId2 = ConduitUtils.TryCreateElbow(doc, c1, c2, kickEnd);
                if (elbowId2 != ElementId.InvalidElementId)
                    createdElementIds?.Add(elbowId2);

                return c2.Id;
            }
            else
            {
                // 方向一致：直接打一段 conduit
                var c = Conduit.Create(doc, ctx.ConduitTypeId, start, toPoint, ctx.LevelId);
                ConduitUtils.SetConduitDiameter(c, ctx.DiameterFt);
                createdElementIds?.Add(c.Id);

                // 嘗試把 host connector 接到新的 conduit 上
                try
                {
                    var cConn = ConnectorUtils.GetNearConnector(c, start);
                    if (cConn != null && currentConnector != null &&
                        !cConn.IsConnected && !currentConnector.IsConnected)
                    {
                        currentConnector.ConnectTo(cConn);
                    }
                }
                catch
                {
                    // 忽略連接錯誤
                }

                return c.Id;
            }
        }

        /// <summary>
        /// 在幹管 host 的 projPt 投影位置建立分支 conduit。
        ///
        /// 簡化版（conduit 版）：
        /// - 目前只在 host 上的投影點建立一支從 onCrv → targetPt 的分支 conduit，
        /// - 尚未進行真正的「切斷主幹 + Tee/Takeoff」幾何（pipe API 專用的 PlumbingUtils / TakeoffFitting 不適用）。
        ///
        /// 後續若需要更精細的 conduit Tee 建構，再逐步補完。
        /// </summary>
        public static Conduit CreateBranchAt(
            Document doc,
            ConduitRoutingContext ctx,
            Conduit host,
            XYZ projPt,
            XYZ targetPt,
            string pref,
            List<ElementId> createdElementIds)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (!(host.Location is LocationCurve lc) || lc.Curve == null)
                throw new InvalidOperationException("Host conduit has no valid LocationCurve.");

            // 1) 將 projPt 投影到幹管中心線，確保在曲線上
            var curve = lc.Curve;
            var res = curve.Project(projPt);
            var onCrv = (res != null) ? res.XYZPoint : curve.Evaluate(0.5, true);

            // 2) 建立分支 conduit（onCrv → targetPt）
            var branchStart = onCrv;
            var branchEnd = targetPt;

            var branch = Conduit.Create(doc, ctx.ConduitTypeId, branchStart, branchEnd, ctx.LevelId);
            ConduitUtils.SetConduitDiameter(branch, ctx.DiameterFt);
            createdElementIds?.Add(branch.Id);

            // 3) 以 Tee 連接：先把幹管在 onCrv 切斷
            ElementId newId = PlumbingUtils.BreakCurve(doc, host.Id, onCrv);
            createdElementIds.Add(newId);
            var host2 = doc.GetElement(newId) as Conduit;

            // 取兩段幹管在切點附近的端頭接頭（各取一端）
            var cHostA = ConnectorUtils.GetNearConnector(host, onCrv);
            var cHostB = ConnectorUtils.GetNearConnector(host2, onCrv);

            // 分支管取「外端」接頭
            var cBranch = ConnectorUtils.GetNearConnector(branch, onCrv);

            // 放三通
            try
            {
                var newTee = doc.Create.NewTeeFitting(cHostA, cHostB, cBranch);
                createdElementIds.Add(newTee.Id);
            }
            catch
            {
                // 忽略無法建立 Tee 的錯誤
            }
            return branch;
        }
    }
}

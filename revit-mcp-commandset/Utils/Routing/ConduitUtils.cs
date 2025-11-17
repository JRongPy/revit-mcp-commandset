using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Utils.Routing
{
    public class ConduitUtils
    {
        /// <summary>
        /// 設定 conduit 直徑（英尺）
        /// </summary>
        public static void SetConduitDiameter(Conduit conduit, double diameterFt)
        {
            try
            {
                if (conduit == null || diameterFt <= 0) return;

                // Revit 內部單位為 ft
                var prm = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                if (prm != null && !prm.IsReadOnly)
                    prm.Set(diameterFt);
            }
            catch
            {
                // 忽略設定失敗，不影響主流程
            }
        }

        /// <summary>
        /// 嘗試在兩段 conduit 之間建立 UnionFitting（若兩端對向且同軸），
        /// 若失敗則退而求其次建立 ElbowFitting。
        /// </summary>
        /// <param name="doc">Revit Document</param>
        /// <param name="conduit1">第一段 conduit</param>
        /// <param name="conduit2">第二段 conduit</param>
        /// <param name="point">用來決定使用哪一端 connector 的近似位置</param>
        /// <param name="angleTolDeg">角度容忍值（度數）</param>
        /// <returns>建立出來的 fitting ElementId；失敗則回傳 InvalidElementId</returns>
        public static ElementId TryCreateElbow(
            Document doc,
            Conduit conduit1,
            Conduit conduit2,
            XYZ point,
            double angleTolDeg = 5.0)
        {
            if (doc == null || conduit1 == null || conduit2 == null || point == null)
                return ElementId.InvalidElementId;

            // 1) 取得兩段 conduit 上「靠近 point」的 connector
            Connector c1 = ConnectorUtils.GetNearConnector(conduit1, point);
            Connector c2 = ConnectorUtils.GetNearConnector(conduit2, point);
            if (c1 == null || c2 == null)
                return ElementId.InvalidElementId;

            // 2) 若兩 connector 幾乎是「對向且同軸」，優先嘗試 Union（直通接頭）
            try
            {
                if (AreFacingEachOther(c1, c2, angleTolDeg))
                {
                    var union = doc.Create.NewUnionFitting(c1, c2); // 直通接頭
                    if (union != null) return union.Id;
                }
            }
            catch
            {
                // union 可能因尺寸 / domain / type 不合而失敗，失敗則進入 elbow 路徑
            }

            // 3) 退而求其次：建立 elbow fitting
            try
            {
                var elbow = doc.Create.NewElbowFitting(c2, c1);
                return elbow?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// 判斷兩個 connector 是否大致上「同軸、互相面對」。
        /// 條件：
        /// - dirA 約等於 -dirB（對向，角度小於 angleTolDeg）
        /// - 連心向量大致沿著 dirA / -dirB
        /// </summary>
        private static bool AreFacingEachOther(Connector a, Connector b, double angleTolDeg)
        {
            var dirA = ConnectorUtils.GetConnectorDirection(a); // normalized
            var dirB = ConnectorUtils.GetConnectorDirection(b); // normalized
            if (dirA == null || dirB == null) return false;

            // 對向軸：dirA 與 -dirB 的夾角需小於容許值
            double oppAxisRad = dirA.AngleTo(dirB.Negate());
            if (RadToDeg(oppAxisRad) > angleTolDeg) return false;

            // 連心向量要與 dirA 大致對齊（對 b 則反向）
            var vAB = (b.Origin - a.Origin);
            if (vAB.IsZeroLength()) return true; // 同一點 -> 視為對向

            vAB = vAB.Normalize();

            double aTowardB = RadToDeg(dirA.AngleTo(vAB));          // a 指向 b
            double bTowardA = RadToDeg(dirB.AngleTo(vAB.Negate())); // b 指向 a

            return aTowardB <= angleTolDeg && bTowardA <= angleTolDeg;
        }

        private static double RadToDeg(double r) => r * (180.0 / Math.PI);

        /// <summary>
        /// 取得 MEPCurve 類元素（Pipe/Duct/Conduit 等）的 LocationCurve。
        /// 若無則回傳 null。
        /// </summary>
        public static Curve GetLocationCurve(Element e)
        {
            var lc = e?.Location as LocationCurve;
            return lc?.Curve;
        }

        /// <summary>
        /// 嘗試將一個點投影到 conduit 的曲線上。
        /// 若 clampToSegment=true，超出線段區域時會改用最近端點。
        /// </summary>
        public static bool TryProjectPointOnConduit(
            Conduit conduit,
            XYZ point,
            out XYZ projected,
            out double distance,
            bool clampToSegment = true)
        {
            projected = null;
            distance = double.MaxValue;

            var curve = GetLocationCurve(conduit);
            if (curve == null) return false;

            // 投影到 curve 上
            var ir = curve.Project(point);
            if (ir != null)
            {
                var p = ir.XYZPoint;
                if (clampToSegment && !IsInsideCurveDomain(curve, ir.Parameter))
                {
                    // 若在參數域外則 clamp 到最近端點
                    var p0 = curve.GetEndPoint(0);
                    var p1 = curve.GetEndPoint(1);
                    var d0 = p0.DistanceTo(point);
                    var d1 = p1.DistanceTo(point);
                    projected = (d0 <= d1) ? p0 : p1;
                    distance = projected.DistanceTo(point);
                    return true;
                }

                projected = p;
                distance = projected.DistanceTo(point);
                return true;
            }

            // 投影失敗，退回最近端點
            var a = curve.GetEndPoint(0);
            var b = curve.GetEndPoint(1);
            projected = (a.DistanceTo(point) <= b.DistanceTo(point)) ? a : b;
            distance = projected.DistanceTo(point);
            return true;
        }

        /// <summary>
        /// 取得 conduit 上距離指定點最近的點（投影或端點）。
        /// 若 conduit 無 LocationCurve 則回傳 null。
        /// </summary>
        public static XYZ GetNearestPointOnConduit(
            Conduit conduit,
            XYZ point,
            bool clampToSegment = true)
        {
            return TryProjectPointOnConduit(conduit, point, out var proj, out _, clampToSegment)
                ? proj
                : null;
        }

        /// <summary>
        /// 檢查某個參數值是否落在 curve 的定義區間內（[t0, t1]）。
        /// </summary>
        private static bool IsInsideCurveDomain(
            Curve c,
            double param,
            double tol = 1e-9)
        {
            var t0 = c.GetEndParameter(0);
            var t1 = c.GetEndParameter(1);

            if (Math.Abs(t1 - t0) < tol) return false; // 退化 curve

            if (t0 > t1) (t0, t1) = (t1, t0);

            return (param >= t0 - tol) && (param <= t1 + tol);
        }
    }
}

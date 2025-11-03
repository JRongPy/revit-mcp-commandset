using Autodesk.Revit.DB.Plumbing;
using RevitMCPCommandSet.Services.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace RevitMCPCommandSet.Utils.Routing
{
    public static class PipeUtils
    {
        /// <summary>
        /// 設定管徑（英尺）
        /// </summary>
        public static void SetPipeDiameter(Pipe pipe, double diameterFt)
        {
            try
            {
                if (pipe == null || diameterFt <= 0) return;
                var prm = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (prm != null && !prm.IsReadOnly)
                    prm.Set(diameterFt);
            }
            catch { /* 忽略設定失敗，不影響主流程 */ }
        }

        public static ElementId TryCreateElbow(Document doc, Pipe pipe1, Pipe pipe2, XYZ point, double angleTolDeg = 5.0)
        {
            if (doc == null || pipe1 == null || pipe2 == null || point == null)
                return ElementId.InvalidElementId;

            // Pick the connectors nearest to the picked point on each pipe
            Connector c1 = ConnectorUtils.GetNearConnector(pipe1, point);
            Connector c2 = ConnectorUtils.GetNearConnector(pipe2, point);
            if (c1 == null || c2 == null)
                return ElementId.InvalidElementId;

            // 1) If connectors face each other (inline & opposing), try a union fitting
            try
            {
                if (AreFacingEachOther(c1, c2, angleTolDeg))
                {
                    var union = doc.Create.NewUnionFitting(c1, c2); // straight coupling
                    if (union != null) return union.Id;
                }
            }
            catch
            {
                // union may fail due to size/domain/type mismatch; fall back to elbow
            }

            // 2) Fall back to elbow
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
        /// Returns true when two connectors are approximately coaxial and pointing toward each other.
        /// Conditions:
        /// - dir1 ~ -dir2 (opposing within angle tolerance)
        /// - vector between origins aligns with dir1 / -dir2 (both aiming toward each other)
        /// </summary>
        private static bool AreFacingEachOther(Connector a, Connector b, double angleTolDeg)
        {
            var dirA = ConnectorUtils.GetConnectorDirection(a); // normalized direction
            var dirB = ConnectorUtils.GetConnectorDirection(b); // normalized direction
            if (dirA == null || dirB == null) return false;

            // Opposing axis: angle between dirA and -dirB should be small
            double oppAxisRad = dirA.AngleTo(dirB.Negate());
            if (RadToDeg(oppAxisRad) > angleTolDeg) return false;

            // Line-of-centers vector should align with dirA (and opposite for dirB)
            var vAB = (b.Origin - a.Origin);
            if (vAB.IsZeroLength()) return true; // same point -> treat as facing
            vAB = vAB.Normalize();

            double aTowardB = RadToDeg(dirA.AngleTo(vAB));          // a points toward b
            double bTowardA = RadToDeg(dirB.AngleTo(vAB.Negate())); // b points toward a

            return aTowardB <= angleTolDeg && bTowardA <= angleTolDeg;
        }

        private static double RadToDeg(double r) => r * (180.0 / Math.PI);


        /// <summary>
        /// Get the LocationCurve of an MEPCurve-like element (Pipe/Duct/Conduit/etc.).
        /// Returns null if not available.
        /// </summary>
        public static Curve GetLocationCurve(Element e)
        {
            var lc = (e?.Location as LocationCurve);
            return lc?.Curve;
        }

        /// <summary>
        /// Try project a point onto a pipe/MEPCurve curve.
        /// Returns true if a projection was found; outputs the projected point and distance.
        /// When clampToSegment=true, projection is clamped to curve domain (falls back to nearest end).
        /// </summary>
        public static bool TryProjectPointOnPipe(Pipe pipe, XYZ point, out XYZ projected, out double distance, bool clampToSegment = true)
        {
            projected = null;
            distance = double.MaxValue;

            var curve = GetLocationCurve(pipe);
            if (curve == null) return false;

            // Project onto curve
            var ir = curve.Project(point);
            if (ir != null)
            {
                var p = ir.XYZPoint;
                if (clampToSegment && !IsInsideCurveDomain(curve, ir.Parameter))
                {
                    // clamp to ends if outside domain
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

            // Fallback: nearest end
            var a = curve.GetEndPoint(0);
            var b = curve.GetEndPoint(1);
            projected = (a.DistanceTo(point) <= b.DistanceTo(point)) ? a : b;
            distance = projected.DistanceTo(point);
            return true;
        }

        /// <summary>
        /// Get the closest point on the pipe (projection or nearest end if outside).
        /// Returns null if pipe has no LocationCurve.
        /// </summary>
        public static XYZ GetNearestPointOnPipe(Pipe pipe, XYZ point, bool clampToSegment = true)
        {
            return TryProjectPointOnPipe(pipe, point, out var proj, out _, clampToSegment) ? proj : null;
        }

        /// <summary>
        /// Check whether a curve parameter lies within [0,1] domain (for bounded curves).
        /// </summary>
        private static bool IsInsideCurveDomain(Curve c, double param, double tol = 1e-9)
        {
            var d = c.GetEndParameter(1) - c.GetEndParameter(0);
            if (Math.Abs(d) < tol) return false; // degenerate
            var t0 = c.GetEndParameter(0);
            var t1 = c.GetEndParameter(1);
            if (t0 > t1) (t0, t1) = (t1, t0);
            return (param >= t0 - tol) && (param <= t1 + tol);
        }
    }
}

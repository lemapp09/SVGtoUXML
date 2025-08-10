using UnityEngine;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

// Namespace groups related SVG parsing utilities for use in Unity UI Toolkit rendering.
namespace SVGToUXML
{
    /// <summary>
    /// Parses a subset of SVG path syntax into a structured format and provides
    /// small utilities (like bezier sampling and bounds approximation) used by the
    /// rest of the toolchain. The goal is clarity and reliability over completeness.
    /// </summary>
    public static class SVGPathParser
    {
        /// <summary>
        /// A single SVG path command (e.g., 'M', 'L', 'C', 'Z') and its numeric operands.
        /// - <see cref="type"/> is the command letter as it appears in the path data.
        /// - <see cref="values"/> holds the sequence of numbers following that command.
        ///   For example, for "L 10 20", type='L' and values=[10,20].
        /// </summary>
        public struct PathCommand
        {
            /// <summary>
            /// Command character as in the SVG path data, for example:
            /// 'M' (moveto), 'L' (lineto), 'H' (horizontal lineto), 'V' (vertical lineto),
            /// 'C' (cubic bezier), and 'Z' (close path). Lowercase variants are relative.
            /// </summary>
            public char type;

            /// <summary>
            /// Numeric operands for the command in the order they appear in the path data.
            /// </summary>
            public List<float> values;
        }

        /// <summary>
        /// Tokenizes and parses an SVG path data string into a list of <see cref="PathCommand"/>.
        /// Supported commands: M/m, L/l, H/h, V/v, C/c, Z/z. Other commands are ignored here.
        /// This parser is robust to varied number formats (e.g., "-1.2e-3", ".5", "5.").
        /// </summary>
        /// <param name="pathData">The raw 'd' attribute content from an SVG path element.</param>
        /// <returns>A list of parsed commands preserving the order in the input string.</returns>
        public static List<PathCommand> ParseCommands(string pathData)
        {
            var commands = new List<PathCommand>();
            if (string.IsNullOrEmpty(pathData)) return commands; // nothing to parse

            // 1) Split the input into command tokens and the number substrings that follow them.
            //    Example match groups for "M10 20L30 40":
            //    - ('M', '10 20') and ('L', '30 40')
            var matches = Regex.Matches(pathData, "([MmLlHhVvCcSsQqTtAaZz])([^MmLlHhVvCcSsQqTtAaZz]*)");
            foreach (Match match in matches)
            {
                char type = match.Groups[1].Value[0];
                string valueStr = match.Groups[2].Value.Trim();
                var values = new List<float>();

                // 2) Extract numbers from the value string. This pattern supports:
                //    - optional leading sign (+/-)
                //    - integers (e.g., 10)
                //    - decimals with or without leading digits (e.g., 0.5, .5, 5.)
                //    - optional scientific notation (e.g., 1e3, -2.5E-2)
                var valMatches = Regex.Matches(valueStr, "[+-]?(?:\\d+\\.\\d+|\\d+\\.\\d*|\\.\\d+|\\d+)(?:[eE][+-]?\\d+)?");
                foreach (Match valMatch in valMatches)
                {
                    // Use invariant culture to ensure '.' is treated as the decimal separator regardless of OS locale.
                    if (float.TryParse(valMatch.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        values.Add(f);
                }

                // 3) Store the command and its parsed numeric operands.
                commands.Add(new PathCommand { type = type, values = values });
            }
            return commands;
        }

        /// <summary>
        /// Evaluates a cubic Bezier curve at parameter t in [0,1].
        /// This is used for sampling curves when approximating bounds and for drawing.
        /// </summary>
        /// <param name="p0">Curve start point.</param>
        /// <param name="p1">First control point.</param>
        /// <param name="p2">Second control point.</param>
        /// <param name="p3">Curve end point.</param>
        /// <param name="t">Interpolation parameter between 0 and 1.</param>
        /// <returns>The point on the curve corresponding to t.</returns>
        private static Vector2 CubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            // Standard cubic Bezier formulation using Bernstein polynomials.
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            // Weighted sum of endpoints and control points.
            Vector2 p = uuu * p0; // u^3 * p0
            p += 3f * uu * t * p1; // 3u^2 t * p1
            p += 3f * u * tt * p2; // 3u t^2 * p2
            p += ttt * p3; // t^3 * p3
            return p;
        }

        /// <summary>
        /// Calculates the analytical bounding box of a cubic Bézier curve by finding extrema.
        /// A cubic Bézier B(t) = (1-t)³P₀ + 3(1-t)²tP₁ + 3(1-t)t²P₂ + t³P₃
        /// has derivative B'(t) = 3(1-t)²(P₁-P₀) + 6(1-t)t(P₂-P₁) + 3t²(P₃-P₂)
        /// Setting B'(t) = 0 and solving gives us the extrema points.
        /// </summary>
        /// <param name="p0">Curve start point.</param>
        /// <param name="p1">First control point.</param>
        /// <param name="p2">Second control point.</param>
        /// <param name="p3">Curve end point.</param>
        /// <returns>Tight axis-aligned bounding rectangle.</returns>
        private static Rect GetCubicBezierBounds(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            // Start with the endpoints
            float minX = Mathf.Min(p0.x, p3.x);
            float maxX = Mathf.Max(p0.x, p3.x);
            float minY = Mathf.Min(p0.y, p3.y);
            float maxY = Mathf.Max(p0.y, p3.y);

            // Find extrema by solving B'(t) = 0 for each axis
            // B'(t) = at² + bt + c where:
            // a = 3(P₃ - 3P₂ + 3P₁ - P₀)
            // b = 6(P₂ - 2P₁ + P₀) 
            // c = 3(P₁ - P₀)

            // X-axis extrema
            float ax = 3f * (p3.x - 3f * p2.x + 3f * p1.x - p0.x);
            float bx = 6f * (p2.x - 2f * p1.x + p0.x);
            float cx = 3f * (p1.x - p0.x);
            var xRoots = SolveQuadratic(ax, bx, cx);
            foreach (float t in xRoots)
            {
                if (t >= 0f && t <= 1f)
                {
                    float x = CubicBezier(p0, p1, p2, p3, t).x;
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                }
            }

            // Y-axis extrema
            float ay = 3f * (p3.y - 3f * p2.y + 3f * p1.y - p0.y);
            float by = 6f * (p2.y - 2f * p1.y + p0.y);
            float cy = 3f * (p1.y - p0.y);
            var yRoots = SolveQuadratic(ay, by, cy);
            foreach (float t in yRoots)
            {
                if (t >= 0f && t <= 1f)
                {
                    float y = CubicBezier(p0, p1, p2, p3, t).y;
                    minY = Mathf.Min(minY, y);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// Solves the quadratic equation ax² + bx + c = 0.
        /// Returns the real roots in the range that could be extrema.
        /// </summary>
        /// <param name="a">Coefficient of x²</param>
        /// <param name="b">Coefficient of x</param>
        /// <param name="c">Constant term</param>
        /// <returns>List of real roots (0, 1, or 2 roots)</returns>
        private static List<float> SolveQuadratic(float a, float b, float c)
        {
            var roots = new List<float>();
            const float epsilon = 1e-9f;

            if (Mathf.Abs(a) < epsilon)
            {
                // Linear case: bx + c = 0
                if (Mathf.Abs(b) >= epsilon)
                {
                    roots.Add(-c / b);
                }
            }
            else
            {
                // Quadratic case: ax² + bx + c = 0
                float discriminant = b * b - 4f * a * c;
                if (discriminant >= 0f)
                {
                    float sqrtDisc = Mathf.Sqrt(discriminant);
                    roots.Add((-b + sqrtDisc) / (2f * a));
                    roots.Add((-b - sqrtDisc) / (2f * a));
                }
            }

            return roots;
        }

        /// <summary>
        /// Approximates the axis-aligned bounding rectangle of a path by sampling:
        /// - All vertex positions for line-based commands.
        /// - Multiple points along each cubic Bezier segment (uniformly in t).
        /// Note: This is an approximation; exact Bezier bounds require solving for extrema.
        /// </summary>
        /// <param name="commands">The parsed path commands.</param>
        /// <returns>A Rect that encloses all sampled points, or Rect.zero if no points.</returns>
        public static Rect GetBounds(List<PathCommand> commands)
        {
            var points = new List<Vector2>(); // Collected points used for min/max extent calculation.
            Vector2 currentPoint = Vector2.zero; // Tracks the end-point of the previous command.
            bool prevWasCubic = false; Vector2 lastCubicCtrl = Vector2.zero;
            bool prevWasQuad = false; Vector2 lastQuadCtrl = Vector2.zero;

            // Local helper: add rect corners
            void AddRect(Rect r)
            {
                points.Add(new Vector2(r.xMin, r.yMin));
                points.Add(new Vector2(r.xMin, r.yMax));
                points.Add(new Vector2(r.xMax, r.yMin));
                points.Add(new Vector2(r.xMax, r.yMax));
            }

            // Iterate through commands and accumulate representative sample points.
            foreach (var cmd in commands)
            {
                switch (cmd.type)
                {
                    // Absolute move/line commands: values are (x,y) pairs.
                    case 'M': // moveto (absolute)
                    case 'L': // lineto (absolute)
                        for (int i = 0; i < cmd.values.Count; i += 2)
                        {
                            currentPoint = new Vector2(cmd.values[i], cmd.values[i + 1]);
                            points.Add(currentPoint);
                        }
                        prevWasCubic = prevWasQuad = false;
                        break;

                    // Relative move/line commands: add offsets to currentPoint.
                    case 'm': // moveto (relative)
                    case 'l': // lineto (relative)
                        for (int i = 0; i < cmd.values.Count; i += 2)
                        {
                            currentPoint += new Vector2(cmd.values[i], cmd.values[i + 1]);
                            points.Add(currentPoint);
                        }
                        prevWasCubic = prevWasQuad = false;
                        break;

                    // Horizontal/vertical lines (absolute).
                    case 'H': // horizontal lineto (absolute)
                        for (int i = 0; i < cmd.values.Count; i++)
                        {
                            currentPoint.x = cmd.values[i];
                            points.Add(currentPoint);
                        }
                        prevWasCubic = prevWasQuad = false;
                        break;
                    case 'V': // vertical lineto (absolute)
                        for (int i = 0; i < cmd.values.Count; i++)
                        {
                            currentPoint.y = cmd.values[i];
                            points.Add(currentPoint);
                        }
                        prevWasCubic = prevWasQuad = false;
                        break;

                    // Horizontal/vertical lines (relative).
                    case 'h': // horizontal lineto (relative)
                        for (int i = 0; i < cmd.values.Count; i++)
                        {
                            currentPoint.x += cmd.values[i];
                            points.Add(currentPoint);
                        }
                        prevWasCubic = prevWasQuad = false;
                        break;
                    case 'v': // vertical lineto (relative)
                        for (int i = 0; i < cmd.values.Count; i++)
                        {
                            currentPoint.y += cmd.values[i];
                            points.Add(currentPoint);
                        }
                        prevWasCubic = prevWasQuad = false;
                        break;

                    // Cubic Bezier (absolute): values are groups of six numbers per segment:
                    // (x1,y1, x2,y2, x,y) for the two control points and the end point.
                    case 'C': // curveto (absolute)
                        for (int i = 0; i < cmd.values.Count; i += 6)
                        {
                            Vector2 p0 = currentPoint; // segment start
                            Vector2 p1 = new Vector2(cmd.values[i], cmd.values[i + 1]);
                            Vector2 p2 = new Vector2(cmd.values[i + 2], cmd.values[i + 3]);
                            Vector2 p3 = new Vector2(cmd.values[i + 4], cmd.values[i + 5]);

                            // Use analytical bounds for tight coverage.
                            Rect rb = GetCubicBezierBounds(p0, p1, p2, p3);
                            AddRect(rb);

                            // Update current point and last control.
                            currentPoint = p3;
                            lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false;
                        }
                        break;

                        // Cubic Bezier (relative): same as above but operands are offsets from currentPoint.
                    case 'c': // curveto (relative)
                        for (int i = 0; i < cmd.values.Count; i += 6)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 p1 = currentPoint + new Vector2(cmd.values[i], cmd.values[i + 1]);
                            Vector2 p2 = currentPoint + new Vector2(cmd.values[i + 2], cmd.values[i + 3]);
                            Vector2 p3 = currentPoint + new Vector2(cmd.values[i + 4], cmd.values[i + 5]);

                            // Use analytical bounds for tight coverage.
                            Rect rb = GetCubicBezierBounds(p0, p1, p2, p3);
                            AddRect(rb);

                            currentPoint = p3;
                            lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false;
                        }
                        break;

                    // Smooth cubic: reflect last cubic control or use currentPoint if none
                    case 'S':
                        for (int i = 0; i < cmd.values.Count; i += 6)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 p1 = prevWasCubic ? (currentPoint * 2 - lastCubicCtrl) : currentPoint;
                            Vector2 p2 = new Vector2(cmd.values[i], cmd.values[i + 1]);
                            Vector2 p3 = new Vector2(cmd.values[i + 2], cmd.values[i + 3]);
                            Rect rb = GetCubicBezierBounds(p0, p1, p2, p3);
                            AddRect(rb);
                            currentPoint = p3; lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false;
                        }
                        break;
                    case 's':
                        for (int i = 0; i < cmd.values.Count; i += 6)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 p1 = prevWasCubic ? (currentPoint * 2 - lastCubicCtrl) : currentPoint;
                            Vector2 p2 = currentPoint + new Vector2(cmd.values[i], cmd.values[i + 1]);
                            Vector2 p3 = currentPoint + new Vector2(cmd.values[i + 2], cmd.values[i + 3]);
                            Rect rb = GetCubicBezierBounds(p0, p1, p2, p3);
                            AddRect(rb);
                            currentPoint = p3; lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false;
                        }
                        break;

                    // Quadratic and smooth quadratic: convert to cubic for bounds
                    case 'Q':
                        for (int i = 0; i < cmd.values.Count; i += 4)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 q1 = new Vector2(cmd.values[i], cmd.values[i + 1]);
                            Vector2 p3 = new Vector2(cmd.values[i + 2], cmd.values[i + 3]);
                            (Vector2 c1, Vector2 c2) = QuadraticToCubic(p0, q1, p3);
                            Rect rb = GetCubicBezierBounds(p0, c1, c2, p3);
                            AddRect(rb);
                            currentPoint = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false;
                        }
                        break;
                    case 'q':
                        for (int i = 0; i < cmd.values.Count; i += 4)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 q1 = currentPoint + new Vector2(cmd.values[i], cmd.values[i + 1]);
                            Vector2 p3 = currentPoint + new Vector2(cmd.values[i + 2], cmd.values[i + 3]);
                            (Vector2 c1, Vector2 c2) = QuadraticToCubic(p0, q1, p3);
                            Rect rb = GetCubicBezierBounds(p0, c1, c2, p3);
                            AddRect(rb);
                            currentPoint = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false;
                        }
                        break;
                    case 'T':
                        for (int i = 0; i < cmd.values.Count; i += 2)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 q1 = prevWasQuad ? (currentPoint * 2 - lastQuadCtrl) : currentPoint;
                            Vector2 p3 = new Vector2(cmd.values[i], cmd.values[i + 1]);
                            (Vector2 c1, Vector2 c2) = QuadraticToCubic(p0, q1, p3);
                            Rect rb = GetCubicBezierBounds(p0, c1, c2, p3);
                            AddRect(rb);
                            currentPoint = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false;
                        }
                        break;
                    case 't':
                        for (int i = 0; i < cmd.values.Count; i += 2)
                        {
                            Vector2 p0 = currentPoint;
                            Vector2 q1 = prevWasQuad ? (currentPoint * 2 - lastQuadCtrl) : currentPoint;
                            Vector2 p3 = currentPoint + new Vector2(cmd.values[i], cmd.values[i + 1]);
                            (Vector2 c1, Vector2 c2) = QuadraticToCubic(p0, q1, p3);
                            Rect rb = GetCubicBezierBounds(p0, c1, c2, p3);
                            AddRect(rb);
                            currentPoint = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false;
                        }
                        break;

                    // Elliptical Arc: convert to cubic(s) then use cubic bounds
                    case 'A':
                        for (int i = 0; i < cmd.values.Count; i += 7)
                        {
                            float rx = cmd.values[i]; float ry = cmd.values[i + 1];
                            float phi = cmd.values[i + 2] * Mathf.Deg2Rad;
                            bool largeArc = cmd.values[i + 3] != 0f;
                            bool sweep = cmd.values[i + 4] != 0f;
                            Vector2 p3 = new Vector2(cmd.values[i + 5], cmd.values[i + 6]);
                            foreach (var seg in ArcToCubicBeziers(currentPoint, p3, rx, ry, phi, largeArc, sweep))
                            {
                                Rect rb = GetCubicBezierBounds(currentPoint, seg.c1, seg.c2, seg.p3);
                                AddRect(rb);
                                currentPoint = seg.p3;
                            }
                            prevWasCubic = prevWasQuad = false;
                        }
                        break;
                    case 'a':
                        for (int i = 0; i < cmd.values.Count; i += 7)
                        {
                            float rx = cmd.values[i]; float ry = cmd.values[i + 1];
                            float phi = cmd.values[i + 2] * Mathf.Deg2Rad;
                            bool largeArc = cmd.values[i + 3] != 0f;
                            bool sweep = cmd.values[i + 4] != 0f;
                            Vector2 p3 = currentPoint + new Vector2(cmd.values[i + 5], cmd.values[i + 6]);
                            foreach (var seg in ArcToCubicBeziers(currentPoint, p3, rx, ry, phi, largeArc, sweep))
                            {
                                Rect rb = GetCubicBezierBounds(currentPoint, seg.c1, seg.c2, seg.p3);
                                AddRect(rb);
                                currentPoint = seg.p3;
                            }
                            prevWasCubic = prevWasQuad = false;
                        }
                        break;

                    case 'Z': case 'z':
                        prevWasCubic = prevWasQuad = false;
                        break;
                }
            }

            // If no points were produced (e.g., empty path), return an empty Rect.
            if (points.Count == 0) return Rect.zero;

            // Initialize bounds with the first point, then expand to include all others.
            Rect bounds = new Rect(points[0], Vector2.zero);
            for (int i = 1; i < points.Count; i++)
            {
                bounds.xMin = Mathf.Min(bounds.xMin, points[i].x);
                bounds.yMin = Mathf.Min(bounds.yMin, points[i].y);
                bounds.xMax = Mathf.Max(bounds.xMax, points[i].x);
                bounds.yMax = Mathf.Max(bounds.yMax, points[i].y);
            }
            return bounds;
        }
        // Convert quadratic to cubic control points
        private static (Vector2 c1, Vector2 c2) QuadraticToCubic(Vector2 p0, Vector2 q1, Vector2 p3)
        {
            Vector2 c1 = p0 + (2f / 3f) * (q1 - p0);
            Vector2 c2 = p3 + (2f / 3f) * (q1 - p3);
            return (c1, c2);
        }

        private struct CubicSeg { public Vector2 c1, c2, p3; }

        // SVG arc to cubic bezier conversion per W3C spec
        private static List<CubicSeg> ArcToCubicBeziers(Vector2 p0, Vector2 p1, float rx, float ry, float phi, bool largeArc, bool sweep)
        {
            var result = new List<CubicSeg>();
            if (p0 == p1) return result;
            rx = Mathf.Abs(rx); ry = Mathf.Abs(ry);
            if (rx == 0f || ry == 0f)
            {
                // Treat as straight line
                result.Add(new CubicSeg { c1 = Vector2.Lerp(p0, p1, 1f/3f), c2 = Vector2.Lerp(p0, p1, 2f/3f), p3 = p1 });
                return result;
            }

            float cosPhi = Mathf.Cos(phi); float sinPhi = Mathf.Sin(phi);

            // Step 1: Compute (x1', y1')
            float dx2 = (p0.x - p1.x) / 2f;
            float dy2 = (p0.y - p1.y) / 2f;
            float x1p = cosPhi * dx2 + sinPhi * dy2;
            float y1p = -sinPhi * dx2 + cosPhi * dy2;

            // Correct radii
            float rx2 = rx * rx; float ry2 = ry * ry;
            float x1p2 = x1p * x1p; float y1p2 = y1p * y1p;
            float lambda = x1p2 / rx2 + y1p2 / ry2;
            if (lambda > 1f)
            {
                float s = Mathf.Sqrt(lambda);
                rx *= s; ry *= s; rx2 = rx * rx; ry2 = ry * ry;
            }

            // Step 2: Compute center (cx', cy')
            float sign = (largeArc == sweep) ? -1f : 1f;
            float num = rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2;
            float denom = rx2 * y1p2 + ry2 * x1p2;
            float coef = denom == 0f ? 0f : sign * Mathf.Sqrt(Mathf.Max(0f, num / denom));
            float cxp = coef * (rx * y1p) / ry;
            float cyp = coef * -(ry * x1p) / rx;

            // Step 3: Compute (cx, cy)
            float cx = cosPhi * cxp - sinPhi * cyp + (p0.x + p1.x) / 2f;
            float cy = sinPhi * cxp + cosPhi * cyp + (p0.y + p1.y) / 2f;

            // Step 4: Compute start and sweep angles
            Vector2 v1 = new Vector2((x1p - cxp) / rx, (y1p - cyp) / ry);
            Vector2 v2 = new Vector2((-x1p - cxp) / rx, (-y1p - cyp) / ry);
            float ang(Vector2 u, Vector2 v)
            {
                float dot = Mathf.Clamp(u.x * v.x + u.y * v.y, -1f, 1f);
                float a = Mathf.Acos(dot);
                if (u.x * v.y - u.y * v.x < 0f) a = -a;
                return a;
            }
            float theta1 = ang(new Vector2(1, 0), v1);
            float delta = ang(v1, v2);
            if (!sweep && delta > 0) delta -= 2f * Mathf.PI;
            else if (sweep && delta < 0) delta += 2f * Mathf.PI;

            // Step 5: Approximate arc with segments of <= 90 degrees
            int segs = Mathf.CeilToInt(Mathf.Abs(delta) / (Mathf.PI / 2f));
            float deltaPerSeg = delta / segs;
            float t = theta1;
            for (int i = 0; i < segs; i++)
            {
                float t1 = t; float t2 = t + deltaPerSeg;
                // Compute endpoints and derivatives for the segment in the ellipse frame
                Vector2 e1 = new Vector2(Mathf.Cos(t1), Mathf.Sin(t1));
                Vector2 e2 = new Vector2(Mathf.Cos(t2), Mathf.Sin(t2));
                // Derivative scaled by kappa for cubic approximation
                float alpha = (4f / 3f) * Mathf.Tan((t2 - t1) / 4f);
                Vector2 q1 = new Vector2(e1.x - alpha * e1.y, e1.y + alpha * e1.x);
                Vector2 q2 = new Vector2(e2.x + alpha * e2.y, e2.y - alpha * e2.x);

                // Map from ellipse frame back to original coordinates
                Vector2 pStart = new Vector2(rx * e1.x, ry * e1.y);
                Vector2 c1 = new Vector2(rx * q1.x, ry * q1.y);
                Vector2 c2 = new Vector2(rx * q2.x, ry * q2.y);
                Vector2 pEnd = new Vector2(rx * e2.x, ry * e2.y);

                // Rotate by phi and translate by center
                Vector2 R(Vector2 v) => new Vector2(cosPhi * v.x - sinPhi * v.y, sinPhi * v.x + cosPhi * v.y);
                Vector2 segP0 = new Vector2(cx, cy) + R(pStart);
                Vector2 segC1 = new Vector2(cx, cy) + R(c1);
                Vector2 segC2 = new Vector2(cx, cy) + R(c2);
                Vector2 segP3 = new Vector2(cx, cy) + R(pEnd);

                // First segment should start exactly at p0; adjust small numerical drift
                if (i == 0) segP0 = p0;
                if (i == segs - 1) segP3 = p1;

                result.Add(new CubicSeg { c1 = segC1 + (p0 - segP0), c2 = segC2 + (p1 - segP3), p3 = (i == segs - 1) ? p1 : segP3 });
                p0 = (i == segs - 1) ? p1 : segP3; // advance p0 for next segment bounds computation
                t = t2;
            }
            return result;
        }
    }
}

using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace SVGToUXML
{
    /// <summary>
    /// A custom UI Toolkit control that can draw a list of SvgShape objects using Painter2D.
    ///
    /// How it fits together:
    /// - SvgParser reads an SVG file and produces a List<SvgShape>.
    /// - This control takes those shapes (UpdateContent) and renders them inside its rect.
    /// - It also exposes a UXML attribute (shapes-data) to allow serializing the shapes
    ///   directly into a .uxml file for later reuse.
    ///
    /// Teaching notes:
    /// - VisualElement has a generateVisualContent event. Subscribing to it lets us draw custom
    ///   content by issuing 2D drawing commands (Painter2D) during UI Toolkit's rendering phase.
    /// - We compute a global bounding box for all shapes to scale/center them nicely.
    /// </summary>
    public class SvgVectorControl : VisualElement
    {
        // The shapes currently displayed by this control.
        private List<SvgShape> _mShapes = new List<SvgShape>();

        // Union of all individual shape bounds; used to scale/center during drawing.
        private Rect _mTotalBounds = Rect.zero;

        // Serialized representation of m_Shapes that can live in a UXML attribute.
        private string _mShapesData;

        /// <summary>
        /// UXML attribute that stores/loads the serialized shapes string.
        /// When set (either from UXML or code), we deserialize and update the control.
        /// </summary>
        public string shapesData
        {
            get => _mShapesData;
            set
            {
                _mShapesData = value;
                _mShapes = SvgParser.DeserializeShapes(_mShapesData);
                UpdateContent(_mShapes);
            }
        }
        
        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            // Describe the shapes-data attribute for UXML.
            private readonly UxmlStringAttributeDescription m_ShapesDataAttr = new UxmlStringAttributeDescription { name = "shapes-data", defaultValue = "" };
            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                if (ve is SvgVectorControl svg)
                {
                    svg.shapesData = m_ShapesDataAttr.GetValueFromBag(bag, cc);
                }
            }
        }
        
        /// <summary>
        /// Required factory/traits boilerplate so the control is usable from UXML.
        ///
        /// Note: The [UxmlElement] attribute is now the standard way to declare a custom UXML element.
        /// UxmlFactory is now a generic factory class and the Traits class defines the attributes.
        /// </summary>
        public new class UxmlFactory : UxmlFactory<SvgVectorControl, UxmlTraits> {}

        /// <summary>
        /// Constructor: hook our draw callback into UI Toolkit's rendering.
        /// </summary>
        public SvgVectorControl() => generateVisualContent += OnGenerateVisualContent;

        /// <summary>
        /// Recomputes an axis-aligned bounding box that encloses all shapes.
        /// This is used to compute a uniform scale and an offset so the artwork
        /// fits nicely into the control's contentRect while preserving aspect ratio.
        /// </summary>
        private void CalculateBounds()
        {
            if (_mShapes == null || _mShapes.Count == 0) { _mTotalBounds = Rect.zero; return; }
            _mTotalBounds = _mShapes[0].bounds;
            for (int i = 1; i < _mShapes.Count; i++)
            {
                _mTotalBounds.xMin = Mathf.Min(_mTotalBounds.xMin, _mShapes[i].bounds.xMin);
                _mTotalBounds.yMin = Mathf.Min(_mTotalBounds.yMin, _mShapes[i].bounds.yMin);
                _mTotalBounds.xMax = Mathf.Max(_mTotalBounds.xMax, _mShapes[i].bounds.xMax);
                _mTotalBounds.yMax = Mathf.Max(_mTotalBounds.yMax, _mShapes[i].bounds.yMax);
            }
        }

        /// <summary>
        /// Loads new shapes into the control and triggers a repaint.
        /// Also (re)serializes the shapes into m_ShapesData so the control can export UXML.
        /// </summary>
        public void UpdateContent(List<SvgShape> shapes)
        {
            _mShapes = shapes ?? new List<SvgShape>();
            _mShapesData = SvgParser.SerializeShapes(_mShapes);
            CalculateBounds();
            MarkDirtyRepaint(); // Tell UI Toolkit we have new content to draw.
        }

        /// <summary>
        /// Convenience guard used by callers (e.g., the editor window) before saving/exporting.
        /// </summary>
        public bool HasContent() => _mShapes is { Count: > 0 };

        /// <summary>
        /// Generates a minimal UXML document that embeds this control and the serialized shapes.
        /// The consumer can paste this into a .uxml asset and use it in UI Toolkit.
        /// </summary>
        public string GenerateUXML(string elementName = null)
        {
            string serializedShapes = SvgParser.SerializeShapes(_mShapes);
            string nameAttr = string.IsNullOrEmpty(elementName) ? string.Empty : $" name=\"{elementName}\"";

            return $"\u003cui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:custom=\"SVGToUXML\"\u003e\n  \u003ccustom:SvgVectorControl{nameAttr} shapes-data=\"{serializedShapes}\" style=\"width: 100%; height: 100%;\" /\u003e\n\u003c/ui:UXML\u003e";
        }

        /// <summary>
        /// Core drawing routine. This is called by UI Toolkit when the element needs repainting.
        /// We use Painter2D (mgc.painter2D) to issue vector drawing commands corresponding
        /// to our path command list.
        /// </summary>
        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            // Nothing to draw if there are no shapes or if we have degenerate bounds.
            if (_mShapes == null || _mShapes.Count == 0 || _mTotalBounds.width == 0 || _mTotalBounds.height == 0) return;

            var painter = mgc.painter2D; // Painter2D exposes MoveTo/LineTo/BezierCurveTo/Fill/Stroke.

            // The area available to draw (excludes borders/padding handled by layout).
            float targetWidth = contentRect.width;
            float targetHeight = contentRect.height;
            if (targetWidth <= 0 || targetHeight <= 0) return;

            // Compute a uniform scale that preserves aspect ratio and fits the total bounds.
            float scale = Mathf.Min(targetWidth / _mTotalBounds.width, targetHeight / _mTotalBounds.height);

            // Compute an offset that centers the scaled artwork within the content rect,
            // and also accounts for the bounds' origin (x/y may not be zero in the SVG space).
            Vector2 offset = new Vector2(
                (targetWidth - _mTotalBounds.width * scale) / 2f - _mTotalBounds.x * scale,
                (targetHeight - _mTotalBounds.height * scale) / 2f - _mTotalBounds.y * scale
            );

            // Local helper to transform a point from SVG space -> control space.
            Vector2 TP(Vector2 p) => p * scale + offset;

            // Helpers to support Q/T and A commands
            (Vector2 c1, Vector2 c2) QuadToCubic(Vector2 p0, Vector2 q1, Vector2 p3)
            {
                Vector2 c1 = p0 + (2f / 3f) * (q1 - p0);
                Vector2 c2 = p3 + (2f / 3f) * (q1 - p3);
                return (c1, c2);
            }
            System.Collections.Generic.List<(Vector2 c1, Vector2 c2, Vector2 p3)> ArcToCubics(Vector2 p0, Vector2 p1, float rx, float ry, float phiRad, bool largeArc, bool sweep)
            {
                var list = new System.Collections.Generic.List<(Vector2, Vector2, Vector2)>();
                if (p0 == p1) return list;
                rx = Mathf.Abs(rx); ry = Mathf.Abs(ry);
                if (rx == 0f || ry == 0f)
                {
                    list.Add((Vector2.Lerp(p0, p1, 1f/3f), Vector2.Lerp(p0, p1, 2f/3f), p1));
                    return list;
                }
                float cosPhi = Mathf.Cos(phiRad); float sinPhi = Mathf.Sin(phiRad);
                float dx2 = (p0.x - p1.x) / 2f; float dy2 = (p0.y - p1.y) / 2f;
                float x1p = cosPhi * dx2 + sinPhi * dy2; float y1p = -sinPhi * dx2 + cosPhi * dy2;
                float rx2 = rx * rx, ry2 = ry * ry; float x1p2 = x1p * x1p, y1p2 = y1p * y1p;
                float lambda = x1p2 / rx2 + y1p2 / ry2;
                if (lambda > 1f) { float s = Mathf.Sqrt(lambda); rx *= s; ry *= s; rx2 = rx * rx; ry2 = ry * ry; }
                float sign = (largeArc == sweep) ? -1f : 1f;
                float num = rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2; float denom = rx2 * y1p2 + ry2 * x1p2;
                float coef = denom == 0f ? 0f : sign * Mathf.Sqrt(Mathf.Max(0f, num / denom));
                float cxp = coef * (rx * y1p) / ry; float cyp = coef * -(ry * x1p) / rx;
                float cx = cosPhi * cxp - sinPhi * cyp + (p0.x + p1.x) / 2f;
                float cy = sinPhi * cxp + cosPhi * cyp + (p0.y + p1.y) / 2f;
                Vector2 v1 = new Vector2((x1p - cxp) / rx, (y1p - cyp) / ry);
                Vector2 v2 = new Vector2((-x1p - cxp) / rx, (-y1p - cyp) / ry);
                float Ang(Vector2 u, Vector2 v) { float dot = Mathf.Clamp(u.x * v.x + u.y * v.y, -1f, 1f); float a = Mathf.Acos(dot); if (u.x * v.y - u.y * v.x < 0f) a = -a; return a; }
                float theta1 = Ang(new Vector2(1, 0), v1); float delta = Ang(v1, v2);
                if (!sweep && delta > 0) delta -= 2f * Mathf.PI; else if (sweep && delta < 0) delta += 2f * Mathf.PI;
                int segs = Mathf.CeilToInt(Mathf.Abs(delta) / (Mathf.PI / 2f)); float deltaPerSeg = delta / segs; float t = theta1;
                for (int i = 0; i < segs; i++)
                {
                    float t1 = t, t2 = t + deltaPerSeg; Vector2 e1 = new Vector2(Mathf.Cos(t1), Mathf.Sin(t1)); Vector2 e2 = new Vector2(Mathf.Cos(t2), Mathf.Sin(t2));
                    float alpha = (4f / 3f) * Mathf.Tan((t2 - t1) / 4f);
                    Vector2 q1 = new Vector2(e1.x - alpha * e1.y, e1.y + alpha * e1.x);
                    Vector2 q2 = new Vector2(e2.x + alpha * e2.y, e2.y - alpha * e2.x);
                    Vector2 pStart = new Vector2(rx * e1.x, ry * e1.y); Vector2 c1 = new Vector2(rx * q1.x, ry * q1.y); Vector2 c2 = new Vector2(rx * q2.x, ry * q2.y); Vector2 pEnd = new Vector2(rx * e2.x, ry * e2.y);
                    Vector2 R(Vector2 v) => new Vector2(cosPhi * v.x - sinPhi * v.y, sinPhi * v.x + cosPhi * v.y);
                    Vector2 segP0 = new Vector2(cx, cy) + R(pStart); Vector2 segC1 = new Vector2(cx, cy) + R(c1); Vector2 segC2 = new Vector2(cx, cy) + R(c2); Vector2 segP3 = new Vector2(cx, cy) + R(pEnd);
                    if (i == 0) segP0 = p0; if (i == segs - 1) segP3 = p1;
                    list.Add((segC1 + (p0 - segP0), segC2 + (p1 - segP3), (i == segs - 1) ? p1 : segP3));
                    p0 = (i == segs - 1) ? p1 : segP3; t = t2;
                }
                return list;
            }

            // Transform a point by shape.transform before fitting into content rect
            Vector2 TPW(Vector2 p, Matrix4x4 mat) { Vector3 v = mat.MultiplyPoint(new Vector3(p.x, p.y, 0)); return TP(new Vector2(v.x, v.y)); }

            // Draw each shape in order, applying its fill and stroke.
            foreach (var shape in _mShapes)
            {
                painter.strokeColor = shape.strokeColor;
                painter.fillColor = shape.fillColor;
                painter.lineWidth = shape.strokeWidth * scale; // Scale stroke width with geometry.
                painter.BeginPath();

                // Current "pen" position in SVG space. Commands update this as we go.
                Vector2 cp = Vector2.zero;
                bool prevWasCubic = false; Vector2 lastCubicCtrl = Vector2.zero;
                bool prevWasQuad = false; Vector2 lastQuadCtrl = Vector2.zero;

                // Reconstruct the geometry by replaying the saved path commands.
                foreach (var cmd in shape.commands)
                {
                    switch (cmd.type)
                    {
                        // Absolute move/line commands
                        case 'M': for (int i = 0; i < cmd.values.Count; i += 2) { cp = new Vector2(cmd.values[i], cmd.values[i + 1]); if (i == 0) painter.MoveTo(TPW(cp, shape.transform)); else painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;
                        case 'L': for (int i = 0; i < cmd.values.Count; i += 2) { cp = new Vector2(cmd.values[i], cmd.values[i + 1]); painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;

                        // Relative move/line commands
                        case 'm': for (int i = 0; i < cmd.values.Count; i += 2) { cp += new Vector2(cmd.values[i], cmd.values[i + 1]); if (i == 0) painter.MoveTo(TPW(cp, shape.transform)); else painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;
                        case 'l': for (int i = 0; i < cmd.values.Count; i += 2) { cp += new Vector2(cmd.values[i], cmd.values[i + 1]); painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;

                        // Horizontal/vertical lines
                        case 'H': foreach (var t in cmd.values) { cp.x = t; painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;
                        case 'h': foreach (var t in cmd.values) { cp.x += t; painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;
                        case 'V': foreach (var t in cmd.values) { cp.y = t; painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;
                        case 'v': foreach (var t in cmd.values) { cp.y += t; painter.LineTo(TPW(cp, shape.transform)); } prevWasCubic = prevWasQuad = false; break;

                        // Cubic bézier curves (absolute and relative)
case 'C': for (int i = 0; i < cmd.values.Count; i += 6) { Vector2 p1 = new Vector2(cmd.values[i], cmd.values[i + 1]); Vector2 p2 = new Vector2(cmd.values[i + 2], cmd.values[i + 3]); cp = new Vector2(cmd.values[i + 4], cmd.values[i + 5]); painter.BezierCurveTo(TPW(p1, shape.transform), TPW(p2, shape.transform), TPW(cp, shape.transform)); lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false; } break;
case 'c': for (int i = 0; i < cmd.values.Count; i += 6) { Vector2 p1 = cp + new Vector2(cmd.values[i], cmd.values[i + 1]); Vector2 p2 = cp + new Vector2(cmd.values[i + 2], cmd.values[i + 3]); cp += new Vector2(cmd.values[i + 4], cmd.values[i + 5]); painter.BezierCurveTo(TPW(p1, shape.transform), TPW(p2, shape.transform), TPW(cp, shape.transform)); lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false; } break;

                        // Smooth cubic
case 'S': for (int i = 0; i < cmd.values.Count; i += 4) { Vector2 p1 = prevWasCubic ? (cp * 2 - lastCubicCtrl) : cp; Vector2 p2 = new Vector2(cmd.values[i], cmd.values[i + 1]); Vector2 p3 = new Vector2(cmd.values[i + 2], cmd.values[i + 3]); painter.BezierCurveTo(TPW(p1, shape.transform), TPW(p2, shape.transform), TPW(p3, shape.transform)); cp = p3; lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false; } break;
case 's': for (int i = 0; i < cmd.values.Count; i += 4) { Vector2 p1 = prevWasCubic ? (cp * 2 - lastCubicCtrl) : cp; Vector2 p2 = cp + new Vector2(cmd.values[i], cmd.values[i + 1]); Vector2 p3 = cp + new Vector2(cmd.values[i + 2], cmd.values[i + 3]); painter.BezierCurveTo(TPW(p1, shape.transform), TPW(p2, shape.transform), TPW(p3, shape.transform)); cp = p3; lastCubicCtrl = p2; prevWasCubic = true; prevWasQuad = false; } break;

                        // Quadratic (convert to cubic)
case 'Q': for (int i = 0; i < cmd.values.Count; i += 4) { Vector2 q1 = new Vector2(cmd.values[i], cmd.values[i + 1]); Vector2 p3 = new Vector2(cmd.values[i + 2], cmd.values[i + 3]); var cc = QuadToCubic(cp, q1, p3); painter.BezierCurveTo(TPW(cc.c1, shape.transform), TPW(cc.c2, shape.transform), TPW(p3, shape.transform)); cp = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false; } break;
case 'q': for (int i = 0; i < cmd.values.Count; i += 4) { Vector2 q1 = cp + new Vector2(cmd.values[i], cmd.values[i + 1]); Vector2 p3 = cp + new Vector2(cmd.values[i + 2], cmd.values[i + 3]); var cc = QuadToCubic(cp, q1, p3); painter.BezierCurveTo(TPW(cc.c1, shape.transform), TPW(cc.c2, shape.transform), TPW(p3, shape.transform)); cp = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false; } break;

                        // Smooth quadratic
case 'T': for (int i = 0; i < cmd.values.Count; i += 2) { Vector2 q1 = prevWasQuad ? (cp * 2 - lastQuadCtrl) : cp; Vector2 p3 = new Vector2(cmd.values[i], cmd.values[i + 1]); var cc = QuadToCubic(cp, q1, p3); painter.BezierCurveTo(TPW(cc.c1, shape.transform), TPW(cc.c2, shape.transform), TPW(p3, shape.transform)); cp = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false; } break;
case 't': for (int i = 0; i < cmd.values.Count; i += 2) { Vector2 q1 = prevWasQuad ? (cp * 2 - lastQuadCtrl) : cp; Vector2 p3 = cp + new Vector2(cmd.values[i], cmd.values[i + 1]); var cc = QuadToCubic(cp, q1, p3); painter.BezierCurveTo(TPW(cc.c1, shape.transform), TPW(cc.c2, shape.transform), TPW(p3, shape.transform)); cp = p3; lastQuadCtrl = q1; prevWasQuad = true; prevWasCubic = false; } break;

                        // Elliptical arc (convert to cubic segments)
case 'A': for (int i = 0; i < cmd.values.Count; i += 7) { float rx = cmd.values[i]; float ry = cmd.values[i + 1]; float phi = cmd.values[i + 2] * Mathf.Deg2Rad; bool largeArc = cmd.values[i + 3] != 0f; bool sweep = cmd.values[i + 4] != 0f; Vector2 p3 = new Vector2(cmd.values[i + 5], cmd.values[i + 6]); var segs = ArcToCubics(cp, p3, rx, ry, phi, largeArc, sweep); foreach (var seg in segs) { painter.BezierCurveTo(TPW(seg.c1, shape.transform), TPW(seg.c2, shape.transform), TPW(seg.p3, shape.transform)); cp = seg.p3; } prevWasCubic = prevWasQuad = false; } break;
case 'a': for (int i = 0; i < cmd.values.Count; i += 7) { float rx = cmd.values[i]; float ry = cmd.values[i + 1]; float phi = cmd.values[i + 2] * Mathf.Deg2Rad; bool largeArc = cmd.values[i + 3] != 0f; bool sweep = cmd.values[i + 4] != 0f; Vector2 p3 = cp + new Vector2(cmd.values[i + 5], cmd.values[i + 6]); var segs = ArcToCubics(cp, p3, rx, ry, phi, largeArc, sweep); foreach (var seg in segs) { painter.BezierCurveTo(TPW(seg.c1, shape.transform), TPW(seg.c2, shape.transform), TPW(seg.p3, shape.transform)); cp = seg.p3; } prevWasCubic = prevWasQuad = false; } break;

                        // Close current subpath
                        case 'Z': case 'z': painter.ClosePath(); prevWasCubic = prevWasQuad = false; break;
                    }
                }

                // Fill first, then stroke, matching typical vector graphics behavior.
                if (shape.fillColor.a > 0) painter.Fill();
                if (shape.strokeColor.a > 0 && shape.strokeWidth > 0) painter.Stroke();
            }
        }
    }
}

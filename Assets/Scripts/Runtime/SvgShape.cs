using UnityEngine;
using System.Collections.Generic;

namespace SVGToUXML
{
    /// <summary>
    /// Represents a single drawable shape extracted from an SVG element.
    ///
    /// This is a very lightweight data container used by the editor tools and the
    /// UI Toolkit control (SvgVectorControl) to preview and serialize vector data.
    ///
    /// Key ideas to understand for students:
    /// - An SVG drawing is often made of multiple elements (rect, path, circle, ...).
    ///   We convert each drawable element into a list of path commands (<see cref="commands"/>),
    ///   plus the styling needed to render it (fill, stroke, line width, etc.).
    /// - We keep a cached axis-aligned bounding box (<see cref="bounds"/>) to help with
    ///   layout, centering, and scaling in the preview control.
    /// - There is no behavior in this class—it's purely data (a "model").
    /// </summary>
    public class SvgShape
    {
        /// \u003csummary\u003e
        /// The path commands that describe the geometry of this shape.
        /// These are produced by parsing the original SVG element (e.g., rect, circle, path)
        /// into a unified "path-like" representation. See \u003csee cref=\"SVGPathParser\"/\u003e.
        ///
        /// Example: for a rectangle, this might contain an 'M' move command followed by
        /// several 'L' (line) commands and a 'Z' (close path).
        /// \u003c/summary\u003e
        public List<SVGPathParser.PathCommand> commands;

        /// \u003csummary\u003e
        /// Axis-aligned bounding rectangle that encloses all points of this shape.
        /// Used by the preview renderer to compute scale and centering within the control.
        /// \u003c/summary\u003e
        public Rect bounds;

        /// \u003csummary\u003e
        /// Cumulative transform inherited from ancestor \u003cg\u003e elements and the element itself.
        /// Applied during drawing; bounds are pre-transformed at parse time.
        /// \u003c/summary\u003e
        public Matrix4x4 transform = Matrix4x4.identity;

        /// <summary>
        /// The fill color for the interior of the shape. Color.clear means "no fill".
        /// In SVG terms, this corresponds to the 'fill' property (e.g., fill="#FF00FF" or fill="none").
        /// </summary>
        public Color fillColor = Color.clear;

        /// <summary>
        /// The stroke (outline) color of the shape. Color.clear means "no stroke".
        /// In SVG terms, this corresponds to the 'stroke' property.
        /// </summary>
        public Color strokeColor = Color.clear;

        /// <summary>
        /// The thickness of the stroke in SVG units. If 0, no stroke will be drawn.
        /// This maps to the 'stroke-width' property in SVG.
        /// </summary>
        public float strokeWidth = 0f;

        /// <summary>
        /// The style of the stroke's end caps when drawing open subpaths.
        /// Valid values are typically "butt", "round", or "square" (SVG semantics).
        /// </summary>
        public string strokeCap = "butt";

        /// <summary>
        /// The style of the stroke's corners where path segments join.
        /// Typical SVG values are "miter", "round", or "bevel".
        /// </summary>
        public string strokeJoin = "miter";
    }
}


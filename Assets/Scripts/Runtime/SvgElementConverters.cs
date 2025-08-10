using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace SVGToUXML
{
    /// <summary>
    /// Converts individual SVG element nodes (rect, circle, path, etc.) into a unified
    /// list-of-path-commands representation that the rest of the tool understands.
    ///
    /// Why do this? SVG has many element types, but our renderer works in terms of
    /// generic path commands (M/L/C/Z...). By converting everything to path-like data,
    /// we simplify the drawing and serialization code paths.
    /// </summary>
    public static class SvgElementConverters
    {
        /// <summary>
        /// Translates absolute coordinates of path commands by (dx, dy). Relative commands are left unchanged.
        /// </summary>
        public static List<SVGPathParser.PathCommand> TranslateCommands(List<SVGPathParser.PathCommand> src, float dx, float dy)
        {
            if (src == null || (dx == 0f && dy == 0f)) return src;
            var outList = new List<SVGPathParser.PathCommand>(src.Count);
            foreach (var cmd in src)
            {
                var c = cmd; // struct copy
                var v = c.values != null ? new List<float>(c.values) : new List<float>();
                switch (c.type)
                {
                    case 'M':
                    case 'L':
                        for (int i = 0; i < v.Count; i += 2) { v[i] += dx; v[i + 1] += dy; }
                        break;
                    case 'H':
                        for (int i = 0; i < v.Count; i++) { v[i] += dx; }
                        break;
                    case 'V':
                        for (int i = 0; i < v.Count; i++) { v[i] += dy; }
                        break;
                    case 'C':
                        for (int i = 0; i < v.Count; i += 6) { v[i] += dx; v[i + 1] += dy; v[i + 2] += dx; v[i + 3] += dy; v[i + 4] += dx; v[i + 5] += dy; }
                        break;
                    case 'S':
                        for (int i = 0; i < v.Count; i += 4) { v[i] += dx; v[i + 1] += dy; v[i + 2] += dx; v[i + 3] += dy; }
                        break;
                    case 'Q':
                        for (int i = 0; i < v.Count; i += 4) { v[i] += dx; v[i + 1] += dy; v[i + 2] += dx; v[i + 3] += dy; }
                        break;
                    case 'T':
                        for (int i = 0; i < v.Count; i += 2) { v[i] += dx; v[i + 1] += dy; }
                        break;
                    case 'A':
                        for (int i = 0; i < v.Count; i += 7) { /* rx,ry,phi,laf,sweep, x, y */ v[i + 5] += dx; v[i + 6] += dy; }
                        break;
                    // relative commands left unchanged
                }
                c.values = v;
                outList.Add(c);
            }
            return outList;
        }

        /// <summary>
        /// Entry point that selects the appropriate conversion routine based on the
        /// element name (e.g., "rect", "circle", "path").
        /// </summary>
        /// <param name="el">An XElement from the parsed SVG document tree.</param>
        /// <param name="options">Parse options for logging/strictness.</param>
        /// <returns>
        /// A list of PathCommand instances describing the shape, or null for unsupported elements.
        /// </returns>
        public static List<SVGPathParser.PathCommand> ToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            switch (el.Name.LocalName)
            {
                case "path": return SVGPathParser.ParseCommands(GetStringAttribute(el, "d"));
                case "rect": return RectToPathCommands(el, options);
                case "circle": return CircleToPathCommands(el, options);
                case "ellipse": return EllipseToPathCommands(el, options);
                case "line": return LineToPathCommands(el, options);
                case "polygon": return PolygonToPathCommands(el, options);
                case "polyline": return PolylineToPathCommands(el, options);
                case "use": return UseToPathCommands(el, options);
                default: return null; // unsupported element types are ignored by the higher layers
            }
        }

        /// <summary>
        /// Converts an <rect> element to path commands. Handles both sharp-cornered and
        /// rounded rectangles. Rounded corners are approximated with cubic bezier arcs.
        /// </summary>
        public static List<SVGPathParser.PathCommand> RectToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            // SVG attributes. If absent, these default to 0 which will result in an empty shape.
            float x = GetFloatAttribute(el, "x", 0f, options), y = GetFloatAttribute(el, "y", 0f, options), w = GetFloatAttribute(el, "width", 0f, options), h = GetFloatAttribute(el, "height", 0f, options);
            float rx = GetFloatAttribute(el, "rx", 0f, options), ry = GetFloatAttribute(el, "ry", 0f, options);

            var cmds = new List<SVGPathParser.PathCommand>();
            if (w <= 0f || h <= 0f)
                return cmds; // nothing to draw

            // Case 1: No corner radii -> simple rectangle path (M + L's + Z)
            if (rx <= 0f && ry <= 0f)
            {
                cmds.Add(new SVGPathParser.PathCommand { type = 'M', values = new List<float> { x, y, x + w, y, x + w, y + h, x, y + h } });
                cmds.Add(new SVGPathParser.PathCommand { type = 'Z', values = new List<float>() });
                return cmds;
            }

            // Case 2: Rounded rectangle.
            // Per SVG spec, if only rx or only ry is specified, the other takes the same value.
            // Clamp radii to half the width/height to avoid self-intersection.
            rx = Mathf.Min(rx, w * 0.5f);
            ry = Mathf.Min(ry, h * 0.5f);
            if (rx <= 0f) rx = ry; if (ry <= 0f) ry = rx;

            // Magic constant k ≈ 0.5522847 converts circular arcs to cubic beziers.
            // See: https://spencermortensen.com/articles/bezier-circle/
            float kx = 0.552284749831f * rx;
            float ky = 0.552284749831f * ry;

            // Helpful aliases for the rectangle’s key x/y positions.
            float x0 = x, x1 = x + rx, x2 = x + w - rx, x3 = x + w;
            float y0 = y, y1 = y + ry, y2 = y + h - ry, y3 = y + h;

            // Path construction in clockwise order starting at the top-left arc start.
            cmds.Add(new SVGPathParser.PathCommand { type = 'M', values = new List<float> { x1, y0 } }); // move to top-left arc start
            cmds.Add(new SVGPathParser.PathCommand { type = 'L', values = new List<float> { x2, y0 } }); // top edge
            cmds.Add(new SVGPathParser.PathCommand { type = 'C', values = new List<float> { x2 + kx, y0, x3, y1 - ky, x3, y1 } }); // top-right corner (arc)
            cmds.Add(new SVGPathParser.PathCommand { type = 'L', values = new List<float> { x3, y2 } }); // right edge
            cmds.Add(new SVGPathParser.PathCommand { type = 'C', values = new List<float> { x3, y2 + ky, x2 + kx, y3, x2, y3 } }); // bottom-right corner
            cmds.Add(new SVGPathParser.PathCommand { type = 'L', values = new List<float> { x1, y3 } }); // bottom edge
            cmds.Add(new SVGPathParser.PathCommand { type = 'C', values = new List<float> { x1 - kx, y3, x0, y2 + ky, x0, y2 } }); // bottom-left corner
            cmds.Add(new SVGPathParser.PathCommand { type = 'L', values = new List<float> { x0, y1 } }); // left edge
            cmds.Add(new SVGPathParser.PathCommand { type = 'C', values = new List<float> { x0, y1 - ky, x1 - kx, y0, x1, y0 } }); // top-left corner
            cmds.Add(new SVGPathParser.PathCommand { type = 'Z', values = new List<float>() });
            return cmds;
        }

        /// <summary>
        /// Converts a <circle> to path commands by delegating to the ellipse logic with rx=ry=r.
        /// </summary>
        public static List<SVGPathParser.PathCommand> CircleToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            float cx = GetFloatAttribute(el, "cx", 0f, options), cy = GetFloatAttribute(el, "cy", 0f, options), r = GetFloatAttribute(el, "r", 0f, options);

            // Create a temporary <ellipse> node so we can reuse the ellipse converter below.
            var e = new XElement("ellipse");
            e.SetAttributeValue("cx", cx.ToString(CultureInfo.InvariantCulture));
            e.SetAttributeValue("cy", cy.ToString(CultureInfo.InvariantCulture));
            e.SetAttributeValue("rx", r.ToString(CultureInfo.InvariantCulture));
            e.SetAttributeValue("ry", r.ToString(CultureInfo.InvariantCulture));
            return EllipseToPathCommands(e, options);
        }

        /// <summary>
        /// Converts an <ellipse> element to a closed path using four cubic bezier segments.
        /// The constant k ≈ 0.5522847 gives a good visual approximation of a quarter-circle.
        /// </summary>
        public static List<SVGPathParser.PathCommand> EllipseToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            float cx = GetFloatAttribute(el, "cx", 0f, options), cy = GetFloatAttribute(el, "cy", 0f, options), rx = GetFloatAttribute(el, "rx", 0f, options), ry = GetFloatAttribute(el, "ry", 0f, options), k = 0.552284749831f;
            return new List<SVGPathParser.PathCommand>
            {
                // Start at top (12 o'clock), then go clockwise using four cubic segments.
                new SVGPathParser.PathCommand{ type='M', values = new List<float>{ cx, cy - ry } },
                new SVGPathParser.PathCommand{ type='C', values = new List<float>{ cx + k*rx, cy - ry, cx + rx, cy - k*ry, cx + rx, cy } },
                new SVGPathParser.PathCommand{ type='C', values = new List<float>{ cx + rx, cy + k*ry, cx + k*rx, cy + ry, cx, cy + ry } },
                new SVGPathParser.PathCommand{ type='C', values = new List<float>{ cx - k*rx, cy + ry, cx - rx, cy + k*ry, cx - rx, cy } },
                new SVGPathParser.PathCommand{ type='C', values = new List<float>{ cx - rx, cy - k*ry, cx - k*rx, cy - ry, cx, cy - ry } },
                new SVGPathParser.PathCommand{ type='Z', values = new List<float>() }
            };
        }

        /// <summary>
        /// Converts a single straight <line> element into a MoveTo (M) and a LineTo (L).
        /// </summary>
        public static List<SVGPathParser.PathCommand> LineToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            float x1 = GetFloatAttribute(el, "x1", 0f, options), y1 = GetFloatAttribute(el, "y1", 0f, options), x2 = GetFloatAttribute(el, "x2", 0f, options), y2 = GetFloatAttribute(el, "y2", 0f, options);
            return new List<SVGPathParser.PathCommand>
            {
                new SVGPathParser.PathCommand{ type='M', values = new List<float>{ x1, y1 } },
                new SVGPathParser.PathCommand{ type='L', values = new List<float>{ x2, y2 } }
            };
        }

        /// <summary>
        /// Converts a closed polygon into a polyline and then appends a close-path (Z).
        /// </summary>
        public static List<SVGPathParser.PathCommand> PolygonToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            var cmds = PolylineToPathCommands(el, options);
            if (cmds.Count > 0) cmds.Add(new SVGPathParser.PathCommand{ type='Z', values = new List<float>()});
            return cmds;
        }

        /// <summary>
        /// Converts a <polyline> element's "points" attribute into a sequence of M/L commands.
        /// The "points" attribute is a whitespace or comma separated list of numbers:
        /// Example: "10,20 30,40 50,60" -> (10,20) -> (30,40) -> (50,60)
        /// </summary>
        public static List<SVGPathParser.PathCommand> PolylineToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            var raw = GetStringAttribute(el, "points");
            var tokens = raw.Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            var values = new List<float>(tokens.Length);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (float.TryParse(tokens[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var fv)) values.Add(fv);
                else SvgParser.ReportWarning(el, $"Failed to parse point token '{tokens[i]}' in points='{raw}'", options);
            }

            var cmds = new List<SVGPathParser.PathCommand>();
            if (values.Count >= 2)
            {
                // Move to the first coordinate pair, then line to each subsequent pair.
                cmds.Add(new SVGPathParser.PathCommand{ type='M', values = new List<float>{ values[0], values[1] } });
                for (int i = 2; i < values.Count; i += 2)
                    cmds.Add(new SVGPathParser.PathCommand{ type='L', values = new List<float>{ values[i], values[i+1] } });
            }
            return cmds;
        }

        /// <summary>
        /// Reads a numeric attribute from an element, returning a default if missing or malformed.
        /// This method is forgiving: it accepts leading/trailing whitespace and supports
        /// integers, decimals, and scientific notation (e.g., "1e-3").
        /// </summary>
        public static float GetFloatAttribute(XElement el, string name, float def = 0f, SvgParser.ParseOptions options = null)
        {
            string v = GetStringAttribute(el, name);
            if (string.IsNullOrEmpty(v)) return def;

            // Extract the first number-like token from the attribute value.
            // Use a verbatim string (@) to avoid C# escape-sequence issues and improve readability.
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^[\t ]*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)");
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float r))
                return r;

            // Failed to parse
            if (options != null)
            {
                if (options.strictMode) SvgParser.ReportError(el, $"Attribute '{name}' has invalid numeric value '{v}'", options);
                else SvgParser.ReportWarning(el, $"Attribute '{name}' has invalid numeric value '{v}' (using default {def})", options);
            }
            return def;
        }

        /// <summary>
        /// Retrieves the raw string value of an attribute, or a default if not present.
        /// </summary>
        public static string GetStringAttribute(XElement el, string name, string def = "")
        { var a = el.Attribute(name); return a != null ? a.Value : def; }

        /// <summary>
        /// Resolves a <use> element by dereferencing its href and applying x/y translation.
        /// </summary>
        public static List<SVGPathParser.PathCommand> UseToPathCommands(XElement el, SvgParser.ParseOptions options = null)
        {
            string href = GetStringAttribute(el, "href");
            if (string.IsNullOrEmpty(href))
            {
                // legacy xlink namespace
                XNamespace xlink = "http://www.w3.org/1999/xlink";
                var a = el.Attribute(xlink + "href");
                href = a != null ? a.Value : "";
            }
            if (string.IsNullOrEmpty(href) || href[0] != '#')
            {
                if (options != null && options.verboseLogging) SVGToUXML.SvgParser.ReportWarning(el, $"<use> missing href or unsupported href='{href}'", options);
                return null;
            }
            string id = href.Substring(1);
            var root = el.Document?.Root;
            if (root == null) return null;
            XElement target = null;
            foreach (var d in root.Descendants())
            {
                var idAttr = d.Attribute("id");
                if (idAttr != null && idAttr.Value == id) { target = d; break; }
            }
            if (target == null)
            {
                if (options != null && options.verboseLogging) SVGToUXML.SvgParser.ReportWarning(el, $"<use> target not found: #{id}", options);
                return null;
            }
            var cmds = ToPathCommands(target, options);
            if (cmds == null || cmds.Count == 0) return cmds;
            float x = GetFloatAttribute(el, "x", 0f, options);
            float y = GetFloatAttribute(el, "y", 0f, options);
            return TranslateCommands(cmds, x, y);
        }
    }
}


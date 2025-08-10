using UnityEngine;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Xml.Linq;

namespace SVGToUXML
{
    /// <summary>
    /// High-level SVG parser that reads an SVG file and produces a flat list of
    /// SvgShape records ready for preview and serialization.
    ///
    /// Key responsibilities:
    /// - Load and parse the XML document.
    /// - Walk the SVG tree and convert supported elements into path commands (via SvgElementConverters).
    /// - Apply style attributes (fill/stroke) to each shape (via SvgStyle).
    /// - Compute simple heuristics like removing background rectangles when appropriate.
    /// - Provide serialization helpers so shapes can be embedded in UXML easily.
    /// </summary>
    public static class SvgParser
    {
        /// <summary>
        /// Options to control parsing and error reporting behavior.
        /// </summary>
        public class ParseOptions
        {
            public bool strictMode = false;        // Throw on parse errors (malformed numeric values, etc.)
            public bool verboseLogging = true;     // Emit warnings with element path and attribute context
        }

        /// <summary>
        /// Builds an XPath-like path for an element, including 1-based sibling index.
        /// Example: /svg[1]/g[2]/rect[3]
        /// </summary>
        public static string GetElementPath(XElement el)
        {
            var stack = new System.Collections.Generic.Stack<string>();
            XElement cur = el;
            while (cur != null)
            {
                var parent = cur.Parent;
                int idx = 1;
                if (parent != null)
                {
                    foreach (var sib in parent.Elements(cur.Name))
                    {
                        if (sib == cur) break;
                        idx++;
                    }
                }
                stack.Push($"{cur.Name.LocalName}[{idx}]");
                cur = parent;
            }
            return "/" + string.Join("/", stack);
        }

        /// <summary>
        /// Centralized logging helpers so converters can report issues consistently.
        /// </summary>
        public static void ReportWarning(XElement el, string message, ParseOptions opt)
        {
            if (opt != null && opt.verboseLogging)
                Debug.LogWarning($"[SVG] {message} at {GetElementPath(el)}");
        }
        public static void ReportError(XElement el, string message, ParseOptions opt)
        {
            if (opt != null && opt.strictMode)
                throw new System.FormatException($"{message} at {GetElementPath(el)}");
            Debug.LogError($"[SVG] {message} at {GetElementPath(el)}");
        }

        /// <summary>
        /// Parses the given SVG file into a list of SvgShape instances.
        /// This method is resilient: unsupported elements are skipped, and errors are logged.
        /// </summary>
        /// <param name="filePath">Absolute path to an .svg file.</param>
        /// <param name="options">Optional parse options (strict/logging behavior).</param>
        /// <returns>List of shapes (possibly empty) in document order.</returns>
        public static List<SvgShape> ParseShapes(string filePath, ParseOptions options = null)
        {
            var shapes = new List<SvgShape>();
            try
            {
                // 1) Read and parse the XML. XDocument gives us LINQ-to-XML access.
                var text = System.IO.File.ReadAllText(filePath);
                var doc = XDocument.Parse(text);
                if (doc?.Root == null) return shapes; // empty or invalid document

                // 1b) Build class styles from any <style> blocks under <defs> (and elsewhere)
                var classStyles = BuildClassStyles(doc.Root, options);

                // 2) Capture root dimensions. This helps us detect a full-canvas background rect
                //    that often exists only to set the background color. We may strip its fill
                //    unless an explicit fill was provided on that element.
                float rootW = 0f, rootH = 0f;
                rootW = SvgElementConverters.GetFloatAttribute(doc.Root, "width", 0f, options);
                rootH = SvgElementConverters.GetFloatAttribute(doc.Root, "height", 0f, options);
                if ((rootW <= 0f || rootH <= 0f))
                {
                    // If width/height are not present, try viewBox="minX minY width height".
                    var vb = SvgElementConverters.GetStringAttribute(doc.Root, "viewBox", "");
                    if (!string.IsNullOrEmpty(vb))
                    {
                        // Split on any whitespace
                        var parts = vb.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 4)
                        {
                            if (!float.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out rootW))
                                ReportWarning(doc.Root, $"Could not parse viewBox width '{parts[2]}'", options);
                            if (!float.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out rootH))
                                ReportWarning(doc.Root, $"Could not parse viewBox height '{parts[3]}'", options);
                        }
                        else if (options != null && options.verboseLogging)
                        {
                            ReportWarning(doc.Root, $"Unexpected viewBox format '{vb}'", options);
                        }
                    }
                }

                // 3) Recursively walk the tree, carrying style and transform from groups.
                var shapesOut = new List<SvgShape>();
                void ProcessElement(System.Xml.Linq.XElement el, SvgStyle.StyleValues parentStyle, Matrix4x4 parentMatrix)
                {
                    // Compose transforms: parent * local
                    Matrix4x4 localTransform = ParseTransform(SvgElementConverters.GetStringAttribute(el, "transform", ""));
                    Matrix4x4 cum = parentMatrix * localTransform;

                    // Merge styles: parentStyle overridden by element's own styles (including class-based)
                    var style = SvgStyle.Extract(el, parentStyle, classStyles);

                    // Try to convert current element into drawable commands
                    var commands = SvgElementConverters.ToPathCommands(el, options);

                    // Suppress warnings for known non-drawable/container elements; warn only when
                    // typically-drawable elements yield no geometry.
                    if (commands == null || commands.Count == 0)
                    {
                        if (options != null && options.verboseLogging)
                        {
                            string localName = el.Name.LocalName;
                            var nonDrawable = new System.Collections.Generic.HashSet<string>
                            {
                                "svg","g","defs","style","title","desc","metadata"
                            };
                            var usuallyDrawable = new System.Collections.Generic.HashSet<string>
                            {
                                "path","rect","circle","ellipse","line","polyline","polygon","use","text","image"
                            };

                            if (usuallyDrawable.Contains(localName))
                            {
                                ReportWarning(el, $"Unsupported or empty element \u003c{localName}\u003e", options);
                            }
                            // else: container/metadata or structural element; no warning
                        }
                    }

                    if (commands != null && commands.Count > 0)
                    {
                        // Build the shape: geometry + style + bounds, then apply heuristics.
                        var shape = new SvgShape { commands = commands };
                        SvgStyle.ApplyStyle(style, shape);

                        // Compute raw bounds then transform to account for cumulative transform.
                        Rect raw = SVGPathParser.GetBounds(commands);
                        Rect tb = TransformRect(raw, cum);
                        shape.bounds = tb;
                        shape.transform = cum;

                        // Heuristic: background rect handling on raw attributes (pre-transform alignment)
                        if (el.Name.LocalName == "rect" && rootW > 0f && rootH > 0f)
                        {
                            float x = SvgElementConverters.GetFloatAttribute(el, "x", 0f, options);
                            float y = SvgElementConverters.GetFloatAttribute(el, "y", 0f, options);
                            float w = SvgElementConverters.GetFloatAttribute(el, "width", 0f, options);
                            float h = SvgElementConverters.GetFloatAttribute(el, "height", 0f, options);
                            if (Approximately(x, 0f) && Approximately(y, 0f) && Approximately(w, rootW) && Approximately(h, rootH))
                            {
                                // If no explicit fill/stroke on this element, treat as transparent background with no border.
                                if (!HasExplicitFill(el)) shape.fillColor = Color.clear;
                                if (!HasExplicitStroke(el)) { shape.strokeColor = Color.clear; shape.strokeWidth = 0f; }
                            }
                        }

                        shapesOut.Add(shape);
                    }

                    // Always recurse into children to process nested content (e.g., <svg>, <g>, <defs>, etc.)
                    foreach (var child in el.Elements())
                        ProcessElement(child, style, cum);
                }

                ProcessElement(doc.Root, null, Matrix4x4.identity);
                shapes.AddRange(shapesOut);
            }
            catch (System.Exception ex)
            {
                // We fail gracefully and return whatever we managed to parse.
                Debug.LogError($"SVG parse error: {ex.Message}\n{ex.StackTrace}");
            }
            return shapes;
        }

        /// <summary>
        /// Returns true if the element explicitly sets a 'fill' (either as an attribute
        /// or within the 'style' attribute). This is used by the background-rect heuristic.
        /// </summary>
        private static bool HasExplicitFill(XElement el)
        {
            if (el.Attribute("fill") != null) return true;
            var style = SvgElementConverters.GetStringAttribute(el, "style", "");
            if (string.IsNullOrEmpty(style)) return false;
            return style.IndexOf("fill:", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasExplicitStroke(XElement el)
        {
            if (el.Attribute("stroke") != null) return true;
            var style = SvgElementConverters.GetStringAttribute(el, "style", "");
            if (string.IsNullOrEmpty(style)) return false;
            return style.IndexOf("stroke:", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Floating-point comparison helper that tolerates small relative errors.
        /// Useful when matching root-sized rectangles from string-parsed attributes.
        /// </summary>
        private static bool Approximately(float a, float b)
        {
            return Mathf.Abs(a - b) <= 0.001f * Mathf.Max(1f, Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)));
        }

        private static Rect TransformRect(Rect r, Matrix4x4 m)
        {
            Vector3 p0 = m.MultiplyPoint(new Vector3(r.xMin, r.yMin, 0));
            Vector3 p1 = m.MultiplyPoint(new Vector3(r.xMin, r.yMax, 0));
            Vector3 p2 = m.MultiplyPoint(new Vector3(r.xMax, r.yMin, 0));
            Vector3 p3 = m.MultiplyPoint(new Vector3(r.xMax, r.yMax, 0));
            float xmin = Mathf.Min(Mathf.Min(p0.x, p1.x), Mathf.Min(p2.x, p3.x));
            float xmax = Mathf.Max(Mathf.Max(p0.x, p1.x), Mathf.Max(p2.x, p3.x));
            float ymin = Mathf.Min(Mathf.Min(p0.y, p1.y), Mathf.Min(p2.y, p3.y));
            float ymax = Mathf.Max(Mathf.Max(p0.y, p1.y), Mathf.Max(p2.y, p3.y));
            return new Rect(xmin, ymin, xmax - xmin, ymax - ymin);
        }

        private static Matrix4x4 ParseTransform(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return Matrix4x4.identity;
            var m = Matrix4x4.identity;
            // Basic parser: supports translate(x[,y]), scale(x[,y]), rotate(angle[,cx,cy])
            var parts = t.Split(new[] { ')' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var s = raw.Trim();
                int paren = s.IndexOf('(');
                if (paren < 0) continue;
                string name = s.Substring(0, paren).Trim();
                string argsStr = s.Substring(paren + 1).Trim();
                var args = argsStr.Split(new[] { ',', ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    switch (name)
                    {
                        case "translate":
                            {
                                float tx = args.Length > 0 ? float.Parse(args[0], CultureInfo.InvariantCulture) : 0f;
                                float ty = args.Length > 1 ? float.Parse(args[1], CultureInfo.InvariantCulture) : 0f;
                                m = m * Matrix4x4.Translate(new Vector3(tx, ty, 0));
                                break;
                            }
                        case "scale":
                            {
                                float sx = args.Length > 0 ? float.Parse(args[0], CultureInfo.InvariantCulture) : 1f;
                                float sy = args.Length > 1 ? float.Parse(args[1], CultureInfo.InvariantCulture) : sx;
                                m = m * Matrix4x4.Scale(new Vector3(sx, sy, 1));
                                break;
                            }
                        case "rotate":
                            {
                                float ang = args.Length > 0 ? float.Parse(args[0], CultureInfo.InvariantCulture) : 0f;
                                float rad = ang * Mathf.Deg2Rad;
                                if (args.Length >= 3)
                                {
                                    float cx = float.Parse(args[1], CultureInfo.InvariantCulture);
                                    float cy = float.Parse(args[2], CultureInfo.InvariantCulture);
                                    m = m * Matrix4x4.Translate(new Vector3(cx, cy, 0)) * Matrix4x4.Rotate(Quaternion.Euler(0, 0, ang)) * Matrix4x4.Translate(new Vector3(-cx, -cy, 0));
                                }
                                else
                                {
                                    m = m * Matrix4x4.Rotate(Quaternion.Euler(0, 0, ang));
                                }
                                break;
                            }
                        // Skew and matrix() not implemented in this pass
                    }
                }
                catch { /* ignore malformed transform entries */ }
            }
            return m;
        }

        /// <summary>
        /// Serializes a list of shapes into a compact, UXML-friendly string.
        /// We HTML-encode the final string so it can be embedded safely in an attribute.
        ///
        /// Format per shape (joined with '|'):
        ///   {fill};{stroke};{strokeWidth};{lineCap};{lineJoin};{pathCommands}
        /// where pathCommands is a space-separated list of commands like
        ///   M10,20 L30,40 C... Z
        /// </summary>
        public static string SerializeShapes(List<SvgShape> shapes)
        {
            var ss = shapes.Select(s =>
            {
                string f = "#" + ColorUtility.ToHtmlStringRGBA(s.fillColor);
                string st = "#" + ColorUtility.ToHtmlStringRGBA(s.strokeColor);
                string w = s.strokeWidth.ToString(CultureInfo.InvariantCulture);
                string cap = SvgStyle.NormalizeCap(s.strokeCap);
                string join = SvgStyle.NormalizeJoin(s.strokeJoin);
                string d = string.Join(" ", s.commands.Select(c =>
                {
                    var vals = c.values ?? Enumerable.Empty<float>();
                    return c.type + string.Join(",", vals.Select(v => v.ToString(CultureInfo.InvariantCulture)));
                }));
                return $"{f};{st};{w};{cap};{join};{d}";
            });
            return WebUtility.HtmlEncode(string.Join("|", ss));
        }

        /// <summary>
        /// Reconstructs shapes from the string produced by SerializeShapes.
        /// </summary>
        public static List<SvgShape> DeserializeShapes(string data)
        {
            var shapes = new List<SvgShape>();
            if (string.IsNullOrEmpty(data)) return shapes;

            string dec = WebUtility.HtmlDecode(data);
            foreach (var s in dec.Split('|'))
            {
                var p = s.Split(';');
                if (p.Length < 4) continue; // malformed record

                // Colors are stored as #RRGGBBAA
                ColorUtility.TryParseHtmlString(p[0], out var fill);
                ColorUtility.TryParseHtmlString(p[1], out var stroke);

                // Stroke width is stored using invariant culture.
                float.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var sw);

                // Optional stroke cap/join come next (defaults if absent).
                string cap = p.Length >= 5 ? SvgStyle.NormalizeCap(p[3]) : "butt";
                string join = p.Length >= 6 ? SvgStyle.NormalizeJoin(p[4]) : "miter";

                // The remaining field is the flat path command string.
                int idx = p.Length >= 6 ? 5 : 3;
                var cmds = SVGPathParser.ParseCommands(p[idx]);

                shapes.Add(new SvgShape
                {
                    fillColor = fill,
                    strokeColor = stroke,
                    strokeWidth = sw,
                    strokeCap = cap,
                    strokeJoin = join,
                    commands = cmds,
                    bounds = SVGPathParser.GetBounds(cmds)
                });
            }
            return shapes;
        }
        private static System.Collections.Generic.Dictionary<string, SvgStyle.StyleValues> BuildClassStyles(System.Xml.Linq.XElement root, ParseOptions options)
        {
            var dict = new System.Collections.Generic.Dictionary<string, SvgStyle.StyleValues>();
            if (root == null) return dict;
            foreach (var styleEl in root.Descendants().Where(e => e.Name.LocalName == "style"))
            {
                string css = styleEl.Value ?? string.Empty;
                ParseCssInto(css, dict, options);
            }
            return dict;
        }

        private static void ParseCssInto(string css, System.Collections.Generic.Dictionary<string, SvgStyle.StyleValues> dict, ParseOptions options)
        {
            if (string.IsNullOrWhiteSpace(css)) return;
            // Very simple CSS parser: matches .class { key:value; key:value; }
            var rx = new System.Text.RegularExpressions.Regex(@"\.([a-zA-Z0-9_-]+)\s*\{([^}]*)\}", System.Text.RegularExpressions.RegexOptions.Multiline);
            var declRx = new System.Text.RegularExpressions.Regex(@"\s*([a-zA-Z-]+)\s*:\s*([^;]+)\s*;?", System.Text.RegularExpressions.RegexOptions.Multiline);
            var matches = rx.Matches(css);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (!m.Success || m.Groups.Count < 3) continue;
                string cls = m.Groups[1].Value.Trim();
                string body = m.Groups[2].Value;
                var sv = dict.ContainsKey(cls) ? dict[cls] : new SvgStyle.StyleValues();
                var decls = declRx.Matches(body);
                foreach (System.Text.RegularExpressions.Match dm in decls)
                {
                    if (!dm.Success || dm.Groups.Count < 3) continue;
                    string key = dm.Groups[1].Value.Trim().ToLowerInvariant();
                    string val = dm.Groups[2].Value.Trim();
                    switch (key)
                    {
                        case "fill":
                            if (val == "none") sv.fill = Color.clear;
                            else if (ColorUtility.TryParseHtmlString(val, out var f)) sv.fill = f;
                            break;
                        case "stroke":
                            if (val == "none") sv.stroke = Color.clear;
                            else if (ColorUtility.TryParseHtmlString(val, out var s)) sv.stroke = s;
                            break;
                        case "stroke-width":
                            if (float.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sw)) sv.strokeWidth = sw;
                            break;
                        case "stroke-linecap":
                            sv.strokeCap = SvgStyle.NormalizeCap(val);
                            break;
                        case "stroke-linejoin":
                            sv.strokeJoin = SvgStyle.NormalizeJoin(val);
                            break;
                        // ignore other properties
                    }
                }
                dict[cls] = sv;
            }
        }
    }
}


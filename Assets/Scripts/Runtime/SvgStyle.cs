using UnityEngine;
using System.Globalization;
using System.Xml.Linq;

namespace SVGToUXML
{
    /// <summary>
    /// Applies SVG presentation attributes (fill, stroke, stroke-width, linecap, linejoin)
    /// from an XElement onto an SvgShape data object.
    ///
    /// Notes for students:
    /// - SVG allows styles to be set either as individual attributes (fill="#FF00FF")
    ///   or as a single CSS-like "style" attribute (style="fill:#FF00FF; stroke:none").
    /// - We first read the individual attributes, then we parse the style string so
    ///   that inline style declarations can override previous values (typical CSS behavior).
    /// - Unsupported style properties are simply ignored (keeps the code robust).
    /// </summary>
    public static class SvgStyle
    {
        public class StyleValues
        {
            public Color? fill;
            public Color? stroke;
            public float? strokeWidth;
            public string strokeCap;
            public string strokeJoin;
        }

        /// <summary>
        /// Extracts style information from the element and merges it onto an optional parent style.
        /// </summary>
        public static StyleValues Extract(XElement el, StyleValues parent = null, System.Collections.Generic.Dictionary<string, StyleValues> classStyles = null)
        {
            var sv = new StyleValues();
            if (parent != null)
            {
                sv.fill = parent.fill;
                sv.stroke = parent.stroke;
                sv.strokeWidth = parent.strokeWidth;
                sv.strokeCap = parent.strokeCap;
                sv.strokeJoin = parent.strokeJoin;
            }

            // 0) Merge class-based styles first (lowest precedence of explicit styles)
            var classAttr = el.Attribute("class");
            if (classAttr != null && classStyles != null)
            {
                var classes = classAttr.Value.Split(new[] { ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var cls in classes)
                {
                    if (classStyles.TryGetValue(cls, out var cs))
                    {
                        if (cs.fill.HasValue) sv.fill = cs.fill.Value;
                        if (cs.stroke.HasValue) sv.stroke = cs.stroke.Value;
                        if (cs.strokeWidth.HasValue) sv.strokeWidth = cs.strokeWidth.Value;
                        if (!string.IsNullOrEmpty(cs.strokeCap)) sv.strokeCap = NormalizeCap(cs.strokeCap);
                        if (!string.IsNullOrEmpty(cs.strokeJoin)) sv.strokeJoin = NormalizeJoin(cs.strokeJoin);
                    }
                }
            }

            // 1) Read simple per-attribute values. These provide defaults that can be
            //    overridden by later 'style' declarations below.
            var fillStr = GetStringAttribute(el, "fill");
            if (!string.IsNullOrEmpty(fillStr))
            {
                if (fillStr == "none") sv.fill = Color.clear; // SVG keyword for no fill
                else if (ColorUtility.TryParseHtmlString(fillStr, out var f)) sv.fill = f; // e.g. #RRGGBB or #RRGGBBAA
            }

            var strokeStr = GetStringAttribute(el, "stroke");
            if (!string.IsNullOrEmpty(strokeStr))
            {
                if (strokeStr == "none") sv.stroke = Color.clear; // no outline
                else if (ColorUtility.TryParseHtmlString(strokeStr, out var st)) sv.stroke = st;
            }

            var swStr = GetStringAttribute(el, "stroke-width");
            if (!string.IsNullOrEmpty(swStr) && float.TryParse(swStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var sw))
                sv.strokeWidth = sw;

            var lc = GetStringAttribute(el, "stroke-linecap");
            if (!string.IsNullOrEmpty(lc)) sv.strokeCap = NormalizeCap(lc);

            var lj = GetStringAttribute(el, "stroke-linejoin");
            if (!string.IsNullOrEmpty(lj)) sv.strokeJoin = NormalizeJoin(lj);

            // 2) Parse CSS-like inline style. Declarations here override previously set values.
            var style = GetStringAttribute(el, "style");
            if (!string.IsNullOrEmpty(style))
            {
                foreach (var decl in style.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(decl)) continue;
                    var kv = decl.Split(':');
                    if (kv.Length != 2) continue; // ignore malformed entries

                    string key = kv[0].Trim();
                    string val = kv[1].Trim();

                    switch (key)
                    {
                        case "fill":
                            sv.fill = val == "none" ? Color.clear : (ColorUtility.TryParseHtmlString(val, out var f2) ? f2 : sv.fill);
                            break;
                        case "stroke":
                            sv.stroke = val == "none" ? Color.clear : (ColorUtility.TryParseHtmlString(val, out var s2) ? s2 : sv.stroke);
                            break;
                        case "stroke-width":
                            if (float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var w)) sv.strokeWidth = w;
                            break;
                        case "stroke-linecap":
                            sv.strokeCap = NormalizeCap(val);
                            break;
                        case "stroke-linejoin":
                            sv.strokeJoin = NormalizeJoin(val);
                            break;
                    }
                }
            }
            return sv;
        }

        /// <summary>
        /// Applies the merged style values to a SvgShape instance.
        /// </summary>
        public static void ApplyStyle(StyleValues sv, SvgShape s)
        {
            if (sv == null) return;
            if (sv.fill.HasValue) s.fillColor = sv.fill.Value;
            if (sv.stroke.HasValue) s.strokeColor = sv.stroke.Value;
            if (sv.strokeWidth.HasValue) s.strokeWidth = sv.strokeWidth.Value;
            if (!string.IsNullOrEmpty(sv.strokeCap)) s.strokeCap = NormalizeCap(sv.strokeCap);
            if (!string.IsNullOrEmpty(sv.strokeJoin)) s.strokeJoin = NormalizeJoin(sv.strokeJoin);
        }

        /// <summary>
        /// Reads style information from the SVG element and writes it into the SvgShape.
        /// This does not mutate geometry—only visual properties like colors and stroke.
        /// </summary>
        public static void Apply(XElement el, SvgShape s)
        {
            ApplyStyle(Extract(el), s);
        }

        /// <summary>
        /// Normalizes a stroke-linecap string to one of the supported values.
        /// Unrecognized inputs default to "butt" as per SVG defaults.
        /// </summary>
        public static string NormalizeCap(string v)
        {
            v = (v ?? string.Empty).Trim().ToLowerInvariant();
            return v == "round" ? "round" : v == "square" ? "square" : "butt";
        }

        /// <summary>
        /// Normalizes a stroke-linejoin string to one of the supported values.
        /// Unrecognized inputs default to "miter" as per SVG defaults.
        /// </summary>
        public static string NormalizeJoin(string v)
        {
            v = (v ?? string.Empty).Trim().ToLowerInvariant();
            return v == "round" ? "round" : v == "bevel" ? "bevel" : "miter";
        }

        /// <summary>
        /// Safely retrieves an attribute's string value or returns a default if missing.
        /// </summary>
        private static string GetStringAttribute(XElement el, string name, string def = "")
        {
            var attr = el.Attribute(name);
            return attr != null ? attr.Value : def;
        }
    }
}


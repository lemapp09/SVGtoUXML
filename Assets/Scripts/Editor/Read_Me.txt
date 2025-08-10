## SVG to UXML Converter for Unity UI Toolkit

This tool provides a convenient way to convert SVG image files into Unity UI Toolkit 
XML assets, allowing you to integrate *resolution-independent images* into your Unity 
user interface.

### Installation

- Place all seven C# scripts inside a folder named `Editor` in your Unity project.  
  *Note: Unity requires all Editor scripts to reside in an `Editor` directory for 
  proper functionality.*

### How to Use

- After installation, open the Unity Editor. You will find a new entry under 
  the **Tools** menu:  
  **SVG to UXML Converter**
- Select **SVG to UXML Converter** to launch the tool.

The converter window offers three main features:

- **Load SVG:** Use this button to select and load your SVG file.
- **SVG Display Area:** The loaded SVG will be shown here if it is 
    processed successfully.
- **Save as UXML Asset:** Save your converted SVG as a UXML asset anywhere in 
    your Unity project.

#### Workflow

1. Click **Load SVG** and select an SVG file.  
2. If the SVG is supported, its image will appear in the display area.  
3. Click **Save as UXML Asset** to store the converted image in your desired 
   project location.

### Limitations

- This tool is currently a **work in progress**.  
- **Not all SVG features are supported**, so some SVG files may not convert correctly.

***

*Use this tool to streamline the integration of scalable vector graphics into your 
 Unity UI. Contributions, feedback, and bug reports are welcome as development continues!*


** Below is guidance on the known limitations of this tool. 

Supported elements
•  path
•  Supported commands: M, m, L, l, H, h, V, v, C, c, Z, z
•  Not supported: S/s (smooth cubic), Q/q (quadratic), 
   T/t (smooth quadratic), A/a (elliptical arcs)
•  rect
•  Sharp corners
•  Rounded corners via rx/ry (approximated using cubic Béziers)
•  circle
•  Converted to ellipse under the hood
•  ellipse
•  Approximated with 4 cubic Bézier segments
•  line
•  polygon
•  Converted from polyline + Z (closed)
•  polyline

Supported styling
•  Per-element attributes:
•  fill (hex colors like #RRGGBB or #RRGGBBAA; the keyword none clears fill)
•  stroke (hex colors; the keyword none clears stroke)
•  stroke-width (numeric only)
•  stroke-linecap (butt, round, square)
•  stroke-linejoin (miter, round, bevel)
•  Inline CSS-style attribute
•  style="fill:#xxxxxx; stroke:#xxxxxx; stroke-width:…; 
   stroke-linecap:…; stroke-linejoin:…"
•  Inline style values override the simple attributes above for those keys
•  Color formats
•  Hex (#RRGGBB, #RRGGBBAA) are supported
•  Named colors might not be fully supported; prefer hex
•  Units
•  Numeric attributes are parsed leniently; leading numbers are extracted even if units 
   are present (e.g., "10px" → 10)

Geometry and rendering behavior
•  Bounds
•  Lines use exact points; cubic curves are sampled (21 points per segment) to 
   approximate bounds
•  Scaling/fit
•  The control calculates a union bounds across all shapes and scales uniformly to fit 
   its contentRect (aspect-preserving)
•  Stroke width
•  Scales with geometry to keep a consistent look at different sizes
•  Closing paths
•  Z and z commands close subpaths
•  Background rectangle heuristic
•  If a rect exactly matches the root viewBox/width/height (origin at 0,0) and does not 
   explicitly set fill, its fill is cleared to avoid hiding content

Unsupported (or not implemented)
•  Gradients (linearGradient, radialGradient, gradientUnits, gradientTransform, etc.)
•  Patterns (pattern)
•  Filters (filter, feGaussianBlur, feColorMatrix, etc.)
•  Masks and clipping (mask, clipPath, clip-rule)
•  Transform attributes (transform on elements, including 
   translate/scale/rotate/skew/matrix)
•  CSS stylesheets or advanced selectors (<style> blocks, classes, external CSS); only 
   simple inline style="" is parsed
•  Additional path commands (S, s, Q, q, T, t, A, a)
•  Text (text, tspan), images (image), symbols/uses (symbol, use), defs
•  Stroke dash settings (stroke-dasharray, stroke-dashoffset), miter limit, overall 
   opacity (opacity), fill-rule
•  Inheritance/parent styling (e.g., <g> group styles inherited by children); only direct 
   element attributes and inline style="" are applied
•  preserveAspectRatio (scaling is handled by the control’s fit logic, not the 
   SVG attribute)

Practical guidance for authors
•  Prefer simple shapes: rect, circle/ellipse, line, polygon, polyline, and path using 
   only M/L/H/V/C/Z.
•  Use hex colors for fill and stroke; use none to disable fill or stroke.
•  Keep styles inline on each element or use the style attribute with simple key:value 
   pairs; avoid external CSS and complex selectors.
•  Avoid transforms, gradients, filters, masks, and arcs (A)—they won’t render.
•  Rounded rectangles (rx/ry) are supported.
•  If a solid background rect covers the entire canvas, explicitly set fill if you want 
   it to remain visible (otherwise it may be cleared by the heuristic).

Known caveats
•  Curve bounds are approximated by sampling; extreme control points may produce slightly 
   conservative bounding boxes.
•  The control scales stroke width along with geometry (not SVG-spec accurate in all 
   cases, but visually consistent for UI use).
•  viewBox is only used indirectly for the background-rect heuristic; overall layout is 
   handled by the control’s own fitting logic.
   
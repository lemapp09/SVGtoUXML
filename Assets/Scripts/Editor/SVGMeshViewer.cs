using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using System.IO;

namespace SVGToUXML
{
    /// <summary>
    /// Editor window that lets you:
    /// 1) Pick an SVG file from disk.
    /// 2) Parse it into vector shapes that our UI Toolkit control can draw.
    /// 3) Preview the result directly in the window.
    /// 4) Export the vector data as a UXML asset for use in UI layouts.
    ///
    /// This class demonstrates how to build a simple EditorWindow with UI Toolkit,
    /// wire up buttons, and coordinate between parsing logic (SvgParser) and a
    /// custom VisualElement (SvgVectorControl) for rendering.
    /// </summary>
    public class SVGMeshViewer : EditorWindow
    {
        // A reusable preview control that knows how to render our parsed SvgShape list.
        // We keep a single instance and update its content whenever a new SVG is loaded.
        private SvgVectorControl m_SvgControl;

        // Parser options toggles
        private bool m_StrictMode = false;
        private bool m_VerboseLogging = true;

        /// <summary>
        /// Adds a menu item under Tools to open this window. Clicking the menu item
        /// will instantiate (or focus) the window.
        /// </summary>
        [MenuItem("Tools/SVG to UXML Converter/Open")]
        public static void ShowWindow() => GetWindow<SVGMeshViewer>("SVG to UXML Converter");

        /// <summary>
        /// Runs a quick bounds self-test on a few sample path strings and logs results.
        /// Use this to verify the exact cubic bounds behavior in your environment.
        /// </summary>
        [MenuItem("Tools/SVG to UXML Converter/Run Bounds Self-Test")]
        public static void RunBoundsSelfTest()
        {
            const float eps = 1e-3f;
            bool Approx(float a, float b) => Mathf.Abs(a - b) <= eps * Mathf.Max(1f, Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)));
            bool RectApprox(Rect r, Rect e) => Approx(r.xMin, e.xMin) && Approx(r.yMin, e.yMin) && Approx(r.width, e.width) && Approx(r.height, e.height);

            var tests = new System.Collections.Generic.List<(string name, string path, Rect expected)>
            {
                ("Line basic", "M0,0 L100,50",               new Rect(0, 0, 100, 50)),
                ("Cubic horizontal forward", "M0,0 C30,0 70,0 100,0", new Rect(0, 0, 100, 0)),
                ("Cubic vertical forward",   "M0,0 C0,30 0,70 0,100", new Rect(0, 0, 0, 100)),
                ("Cubic S-curve",            "M0,0 C0,100 100,0 100,100", new Rect(0, 0, 100, 100)),
                ("Cubic horizontal reverse", "M100,0 C70,0 30,0 0,0",   new Rect(0, 0, 100, 0)),
                ("Degenerate to line",       "M0,0 C0,0 100,100 100,100", new Rect(0, 0, 100, 100)),
                ("Single point cubic",       "M0,0 C0,0 0,0 0,0",         new Rect(0, 0, 0, 0)),
                ("Relative horizontal cubic", "M10,10 c50,0 50,0 100,0",  new Rect(10, 10, 100, 0)),

                // Smooth cubic (S/s)
                ("Smooth cubic horizontal S", "M0,0 C30,0 70,0 100,0 S130,0 200,0", new Rect(0, 0, 200, 0)),
                ("Smooth cubic horizontal s", "M0,0 c30,0 70,0 100,0 s30,0 100,0", new Rect(0, 0, 200, 0)),

                // Quadratic (Q/q)
                ("Quadratic vertical Q", "M0,0 Q0,50 0,100", new Rect(0, 0, 0, 100)),
                ("Quadratic vertical q (relative)", "M10,10 q0,50 0,100", new Rect(10, 10, 0, 100)),

                // Smooth quadratic (T/t)
                ("Smooth quad vertical T", "M0,0 Q0,50 0,100 T0,200", new Rect(0, 0, 0, 200)),
                ("Smooth quad vertical t (relative)", "M10,10 q0,50 0,100 t0,100", new Rect(10, 10, 0, 200)),

                // Elliptical arc (A/a) - quarter circle from (0,0) to (0,100) with r=50
                ("Arc quarter A", "M0,0 A50,50 0 0,1 0,100", new Rect(0, 0, 50, 100)),
                ("Arc quarter a (relative)", "M0,0 a50,50 0 0,1 0,100", new Rect(0, 0, 50, 100)),
            };

            int pass = 0, fail = 0;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Bounds Self-Test Results]");

            foreach (var t in tests)
            {
                var cmds = SVGPathParser.ParseCommands(t.path);
                var r = SVGPathParser.GetBounds(cmds);
                bool ok = RectApprox(r, t.expected);
                if (ok) pass++; else fail++;
                sb.AppendLine($"- {t.name}: {(ok ? "PASS" : "FAIL")} | expected x:{t.expected.xMin:F3},y:{t.expected.yMin:F3},w:{t.expected.width:F3},h:{t.expected.height:F3} | got x:{r.xMin:F3},y:{r.yMin:F3},w:{r.width:F3},h:{r.height:F3}");
            }

            sb.AppendLine($"Summary: {pass} passed, {fail} failed.");
            Debug.Log(sb.ToString());
            EditorUtility.DisplayDialog("Bounds Self-Test", $"Completed. {pass} passed, {fail} failed. See Console for details.", "OK");
        }

        /// <summary>
        /// Called by Unity when the window's UI is created. Here we construct our UI:
        /// - A "Load SVG" button that opens a file picker.
        /// - The SvgVectorControl that previews the parsed shapes.
        /// - A "Save as UXML Asset" button that serializes the current shapes into UXML.
        /// </summary>
        public void CreateGUI()
        {
            // Options row: Strict Mode and Verbose Logging
            var optionsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6 } };
            var strictToggle = new Toggle("Strict Mode") { tooltip = "Throw on parse errors (malformed numbers, etc.)" };
            strictToggle.value = m_StrictMode;
            strictToggle.RegisterValueChangedCallback(evt => m_StrictMode = evt.newValue);
            var verboseToggle = new Toggle("Verbose Logging") { tooltip = "Emit detailed warnings with element paths" };
            verboseToggle.value = m_VerboseLogging;
            verboseToggle.RegisterValueChangedCallback(evt => m_VerboseLogging = evt.newValue);
            optionsRow.Add(strictToggle);
            optionsRow.Add(verboseToggle);
            rootVisualElement.Add(optionsRow);

            // Top button: choose an SVG to load.
            rootVisualElement.Add(new Button(LoadSVG) { text = "Load SVG" });

            // Create and style the preview control. The border makes the preview bounds visible.
            m_SvgControl = new SvgVectorControl { name = "svg-preview-control" };
            m_SvgControl.style.flexGrow = 1;   // Take the remaining vertical space.
            m_SvgControl.style.marginTop = 10; // Add some spacing under the top button.
            // Ensure the preview control has no visible borders/background so art remains transparent
            m_SvgControl.style.borderLeftWidth = 0;
            m_SvgControl.style.borderRightWidth = 0;
            m_SvgControl.style.borderTopWidth = 0;
            m_SvgControl.style.borderBottomWidth = 0;
            m_SvgControl.style.backgroundColor = Color.clear;
            rootVisualElement.Add(m_SvgControl);

            // Bottom button: export the currently previewed vector data as a UXML asset.
            rootVisualElement.Add(new Button(SaveUXML) { text = "Save as UXML Asset" });
        }

        /// <summary>
        /// Opens a native file picker for .svg files, parses the selection into shapes,
        /// and updates the preview control. Shows helpful dialogs on empty/invalid input.
        /// </summary>
        private void LoadSVG()
        {
            // Open a read-only picker; returns an absolute path outside the project if you choose from the OS.
            string svgPath = EditorUtility.OpenFilePanel("Open SVG File", "", "svg");
            if (string.IsNullOrEmpty(svgPath)) return; // user canceled

            try
            {
                // Parse the SVG into a list of SvgShape objects. The parser is resilient
                // and will skip unsupported elements while logging errors to the Console.
                var shapes = SvgParser.ParseShapes(svgPath, new SvgParser.ParseOptions
                {
                    strictMode = m_StrictMode,
                    verboseLogging = m_VerboseLogging
                });

                // If nothing drawable is found, let the user know.
                if (shapes == null || shapes.Count == 0)
                {
                    EditorUtility.DisplayDialog("SVG Parsing", "No drawable shapes were found in the selected SVG.", "OK");
                    return;
                }

                // Update the preview control with the new shapes. This triggers a repaint.
                m_SvgControl.UpdateContent(shapes);
            }
            catch (System.Exception ex)
            {
                // Log full details to the Console for debugging and show a friendly dialog to the user.
                Debug.LogError($"Failed to parse SVG: {ex.Message}\n{ex.StackTrace}");
                EditorUtility.DisplayDialog("Error", "Failed to parse the SVG file. See Console for details.", "OK");
            }
        }

        /// <summary>
        /// Serializes the currently previewed shapes to a .uxml asset inside the project.
        /// - Prompts for a project-relative location (so the file appears in the Project window).
        /// - Asks the SvgVectorControl for a UXML string, then writes it to disk.
        /// - Refreshes the AssetDatabase so Unity imports the new asset immediately.
        /// </summary>
        private void SaveUXML()
        {
            // Guard: nothing to save if no shapes have been loaded yet.
            if (m_SvgControl == null || !m_SvgControl.HasContent())
            {
                EditorUtility.DisplayDialog("Nothing to Save", "Please load an SVG file first.", "OK");
                return;
            }

            // Ask for a path inside the Unity project (Assets/...). This ensures the asset
            // shows up in the Project window immediately after writing.
            string path = EditorUtility.SaveFilePanelInProject(
                "Save UXML Asset",
                "NewSvgAsset",
                "uxml",
                "Save the vector data as a UXML file.");

            if (string.IsNullOrEmpty(path)) return; // user canceled

            // Get the UXML markup from the control and write it to disk.
            string elementName = System.IO.Path.GetFileNameWithoutExtension(path);
            string uxmlContent = m_SvgControl.GenerateUXML(elementName);
            File.WriteAllText(path, uxmlContent);

            // Tell Unity to re-scan the Assets folder so the new file is recognized.
            AssetDatabase.Refresh();
            Debug.Log($"Successfully saved UXML asset to {path}");
        }
    }
}

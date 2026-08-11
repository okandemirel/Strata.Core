using System.IO;
using UnityEditor;
using UnityEngine;
// UnityEditor also exposes a legacy PackageInfo type; alias the Package Manager one so the
// unqualified name below cannot bind to it.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Enhanced About window for Strada Framework.
    /// Displays version, features, requirements, and quick links.
    /// </summary>
    public sealed class StradaAboutWindow : EditorWindow
    {
        private const string PackageRootFallback = "Packages/com.strada.core";

        private static readonly Vector2 WindowSize = new(420, 520);
        private static readonly Color SeparatorColor = new(0.5f, 0.5f, 0.5f, 0.3f);

        private string _version = "unknown";
        private string _unityVersion = "6000.0+";
        private string _packageRoot = PackageRootFallback;
        private Vector2 _scrollPosition;
        private GUIStyle _headerStyle;
        private GUIStyle _subtitleStyle;
        private GUIStyle _versionBoxStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _linkStyle;
        private GUIStyle _featureStyle;
        private GUIStyle _copyrightStyle;
        private bool _stylesInitialized;

        public static void ShowWindow()
        {
            var window = GetWindow<StradaAboutWindow>(true, "About Strada Framework", true);
            window.minSize = WindowSize;
            window.maxSize = WindowSize;
            window.Show();
        }

        private void OnEnable()
        {
            LoadPackageInfo();
        }

        private void LoadPackageInfo()
        {
            // "Packages/com.strada.core" is a virtual path: it only exists on disk when the
            // package is embedded under the project. Installed from a registry or a local
            // path the files live under Library/PackageCache, so probing the virtual path
            // finds nothing and the window silently shows stale defaults. PackageInfo knows
            // the resolved location either way.
            var packageInfo = PackageInfo.FindForAssembly(typeof(StradaAboutWindow).Assembly);
            if (packageInfo != null)
            {
                if (!string.IsNullOrEmpty(packageInfo.resolvedPath))
                    _packageRoot = packageInfo.resolvedPath;
                if (!string.IsNullOrEmpty(packageInfo.version))
                    _version = packageInfo.version;
            }

            var packageJsonPath = Path.Combine(_packageRoot, "package.json");
            if (!File.Exists(packageJsonPath)) return;

            var json = File.ReadAllText(packageJsonPath);
            var versionMatch = System.Text.RegularExpressions.Regex.Match(json, "\"version\":\\s*\"([^\"]+)\"");
            if (versionMatch.Success)
                _version = versionMatch.Groups[1].Value;

            var unityMatch = System.Text.RegularExpressions.Regex.Match(json, "\"unity\":\\s*\"([^\"]+)\"");
            if (unityMatch.Success)
                _unityVersion = unityMatch.Groups[1].Value + "+";
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 10, 5)
            };

            _sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin = new RectOffset(0, 0, 10, 5)
            };

            _linkStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.3f, 0.5f, 1f) },
                hover = { textColor = new Color(0.5f, 0.7f, 1f) },
                margin = new RectOffset(15, 0, 2, 2)
            };

            _featureStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(15, 0, 1, 1)
            };

            _subtitleStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 11 };

            _versionBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(20, 20, 8, 8)
            };

            _copyrightStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 10
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            DrawVersion();
            DrawFeatures();
            DrawRequirements();
            DrawDocumentation();
            DrawStatistics();
            DrawFooter();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Strada Framework", _headerStyle);
            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Unified MVCS + ECS Architecture for Unity", _subtitleStyle);

            EditorGUILayout.Space(10);
            DrawSeparator();
        }

        private void DrawVersion()
        {
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUILayout.Box($"Version {_version}", _versionBoxStyle);

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
        }

        private void DrawFeatures()
        {
            EditorGUILayout.LabelField("Core Features", _sectionStyle);

            DrawFeatureItem("Dependency Injection", "Expression-tree compiled, 1.56x overhead");
            DrawFeatureItem("Entity Component System", "SparseSet storage, 6-28ns per entity");
            DrawFeatureItem("MessageBus", "Zero-allocation pub/sub, 4ns dispatch");
            DrawFeatureItem("Reactive Bindings", "ReactiveProperty, ComputedProperty");
            DrawFeatureItem("Module System", "ScriptableObject-driven configuration");
            DrawFeatureItem("Object Pooling", "Generic pools with lifecycle hooks");
            DrawFeatureItem("State Machine", "Type-safe FSM with transitions");
            DrawFeatureItem("Timer Service", "Managed timers with pause/resume");
            DrawFeatureItem("Parallel Jobs", "Burst-compiled, 17x speedup");

            EditorGUILayout.Space(5);
            DrawSeparator();
        }

        private void DrawFeatureItem(string name, string description)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"  \u2022 {name}", _featureStyle, GUILayout.Width(150));
            GUILayout.Label($"- {description}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRequirements()
        {
            EditorGUILayout.LabelField("Requirements", _sectionStyle);

            DrawRequirementItem("Unity Version", _unityVersion);
            DrawRequirementItem(".NET Standard", "2.1");
            DrawRequirementItem("Entities Package", "1.0.16+");
            DrawRequirementItem("Burst Package", "1.8.12+");

            EditorGUILayout.Space(5);
            DrawSeparator();
        }

        private void DrawRequirementItem(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"  {label}:", _featureStyle, GUILayout.Width(120));
            GUILayout.Label(value, EditorStyles.label);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDocumentation()
        {
            EditorGUILayout.LabelField("Documentation", _sectionStyle);

            if (DrawLink("Open README"))
                OpenDocumentation("README.md");
            if (DrawLink("DI Container Guide"))
                OpenDocumentation("Documentation~/DI.md");
            if (DrawLink("ECS System Guide"))
                OpenDocumentation("Documentation~/ECS.md");
            if (DrawLink("Modules Guide"))
                OpenDocumentation("Documentation~/Modules.md");
            if (DrawLink("Messaging Guide"))
                OpenDocumentation("Documentation~/Messaging.md");
            if (DrawLink("Benchmarks"))
                OpenDocumentation("Documentation~/Benchmarks.md");

            EditorGUILayout.Space(5);
            DrawSeparator();
        }

        private bool DrawLink(string text)
        {
            var label = $"  \u2192 {text}";
            var content = new GUIContent(label);
            var rect = GUILayoutUtility.GetRect(content, _linkStyle);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            if (Event.current.type == EventType.Repaint)
                _linkStyle.Draw(rect, label, rect.Contains(Event.current.mousePosition), false, false, false);

            return Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition);
        }

        private void OpenDocumentation(string relativePath)
        {
            var fullPath = Path.Combine(_packageRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"[Strada] Documentation file not found: {fullPath}");
                return;
            }

            // Unity deliberately excludes folders whose name ends in '~' from the
            // AssetDatabase, so LoadAssetAtPath can never resolve a Documentation~ path.
            // Hand the resolved absolute path to the OS instead.
            Application.OpenURL(new System.Uri(Path.GetFullPath(fullPath)).AbsoluteUri);
        }

        private void DrawStatistics()
        {
            EditorGUILayout.LabelField("Framework Statistics", _sectionStyle);

            DrawRequirementItem("Public Types", "552+");
            DrawRequirementItem("Test Cases", "324+");
            DrawRequirementItem("Documentation", "5,000+ lines");

            EditorGUILayout.Space(5);
            DrawSeparator();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("\u00a9 2025 Strada Framework Team", _copyrightStyle);
            EditorGUILayout.LabelField("All rights reserved", _copyrightStyle);

            EditorGUILayout.Space(10);
        }

        private void DrawSeparator()
        {
            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            rect.x += 10;
            rect.width -= 20;
            EditorGUI.DrawRect(rect, SeparatorColor);
            EditorGUILayout.Space(2);
        }
    }
}

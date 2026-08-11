using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Templates
{
    /// <summary>
    /// Provides menu items for creating Strada templates from the Project window.
    /// Context-aware template creation based on folder location.
    /// </summary>
    public static class StradaTemplateMenus
    {
        private const int MenuPriority = 80;

        [MenuItem("Assets/Create/Strada/System", false, MenuPriority)]
        public static void CreateSystem()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.System, "NewSystem");
        }

        [MenuItem("Assets/Create/Strada/Controller", false, MenuPriority + 1)]
        public static void CreateController()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.Controller, "NewController");
        }

        [MenuItem("Assets/Create/Strada/Service", false, MenuPriority + 2)]
        public static void CreateService()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.Service, "NewService");
        }

        [MenuItem("Assets/Create/Strada/Component", false, MenuPriority + 3)]
        public static void CreateComponent()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.Component, "NewComponent");
        }

        [MenuItem("Assets/Create/Strada/View", false, MenuPriority + 4)]
        public static void CreateView()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.View, "NewView");
        }

        [MenuItem("Assets/Create/Strada/Config (CD_)", false, MenuPriority + 5)]
        public static void CreateConfig()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.Config, "NewConfig");
        }

        [MenuItem("Assets/Create/Strada/Command", false, MenuPriority + 6)]
        public static void CreateCommand()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.Command, "NewCommand");
        }

        [MenuItem("Assets/Create/Strada/Event", false, MenuPriority + 7)]
        public static void CreateEvent()
        {
            CreateTemplateWithDialog(TemplateContextDetector.TemplateType.Event, "NewEvent");
        }

        [MenuItem("Assets/Create/Strada/Context-Aware Template...", false, MenuPriority + 20)]
        public static void CreateContextAwareTemplate()
        {
            var folderPath = TemplateContextDetector.GetSelectedFolderPath();
            var templates = TemplateContextDetector.DetectContext(folderPath);

            if (templates.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "No Templates Available",
                    "No context-specific templates available for this location.",
                    "OK");
                return;
            }

            var window = ScriptableObject.CreateInstance<TemplateSelectionWindow>();
            window.Initialize(folderPath, templates);
            window.ShowUtility();
        }

        private static void CreateTemplateWithDialog(
            TemplateContextDetector.TemplateType templateType,
            string defaultName)
        {
            var folderPath = TemplateContextDetector.GetSelectedFolderPath();
            if (string.IsNullOrEmpty(folderPath))
            {
                folderPath = "Assets";
            }

            var window = ScriptableObject.CreateInstance<TemplateNameInputWindow>();
            window.Initialize(templateType, folderPath, defaultName);
            window.ShowUtility();
        }
    }

    /// <summary>
    /// Window for entering template name.
    /// </summary>
    public class TemplateNameInputWindow : EditorWindow
    {
        private TemplateContextDetector.TemplateType _templateType;
        private string _folderPath;
        private string _className = "";
        private string _preview = "";

        public void Initialize(
            TemplateContextDetector.TemplateType templateType,
            string folderPath,
            string defaultName)
        {
            _templateType = templateType;
            _folderPath = folderPath;
            _className = defaultName;

            titleContent = new GUIContent($"Create {templateType}");
            minSize = new Vector2(400, 300);
            maxSize = new Vector2(600, 400);

            UpdatePreview();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField($"Create New {_templateType}", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Folder:", _folderPath, EditorStyles.miniLabel);
            EditorGUILayout.Space(5);

            EditorGUI.BeginChangeCheck();
            _className = EditorGUILayout.TextField("Class Name:", _className);
            if (EditorGUI.EndChangeCheck())
            {
                UpdatePreview();
            }

            EditorGUILayout.Space(10);

            var namespaceName = TemplateContextDetector.ExtractNamespace(_folderPath);
            EditorGUILayout.LabelField("Namespace:", namespaceName, EditorStyles.miniLabel);

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Preview:", EditorStyles.boldLabel);
            var previewStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                fontSize = 10
            };

            var scrollHeight = position.height - 180;
            EditorGUILayout.TextArea(_preview, previewStyle, GUILayout.Height(scrollHeight));

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                Close();
            }

            EditorGUI.BeginDisabledGroup(!StradaTemplates.IsValidClassName(_className));
            if (GUILayout.Button("Create", GUILayout.Width(80)))
            {
                CreateTemplate();
                Close();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private void UpdatePreview()
        {
            if (string.IsNullOrWhiteSpace(_className))
            {
                _preview = "Enter a class name to see preview...";
                return;
            }

            if (!StradaTemplates.IsValidClassName(_className))
            {
                _preview = $"'{_className}' is not a valid C# identifier. Use letters, digits and " +
                           "underscores only, and do not start with a digit.";
                return;
            }

            var namespaceName = TemplateContextDetector.ExtractNamespace(_folderPath);
            _preview = StradaTemplates.GenerateTemplate(_templateType, _className, namespaceName);
        }

        private void CreateTemplate()
        {
            if (string.IsNullOrWhiteSpace(_className))
                return;

            StradaTemplates.CreateFileFromTemplate(_templateType, _className, _folderPath);
        }
    }

    /// <summary>
    /// Window for selecting from context-aware templates.
    /// </summary>
    public class TemplateSelectionWindow : EditorWindow
    {
        private string _folderPath;
        private System.Collections.Generic.List<TemplateContextDetector.TemplateInfo> _templates;
        private int _selectedIndex;
        private string _className = "NewClass";
        private string _preview = "";
        private Vector2 _scrollPosition;

        public void Initialize(
            string folderPath,
            System.Collections.Generic.List<TemplateContextDetector.TemplateInfo> templates)
        {
            _folderPath = folderPath;
            _templates = templates;
            _selectedIndex = 0;

            titleContent = new GUIContent("Create Strada Template");
            minSize = new Vector2(500, 400);
            maxSize = new Vector2(700, 600);

            UpdatePreview();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Context-Aware Template Creation", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Folder:", _folderPath, EditorStyles.miniLabel);

            var moduleName = TemplateContextDetector.ExtractModuleName(_folderPath);
            if (!string.IsNullOrEmpty(moduleName))
            {
                EditorGUILayout.LabelField("Module:", moduleName, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Available Templates:", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var templateNames = new string[_templates.Count];
            for (int i = 0; i < _templates.Count; i++)
            {
                templateNames[i] = _templates[i].Description;
            }
            _selectedIndex = EditorGUILayout.Popup("Template:", _selectedIndex, templateNames);
            if (EditorGUI.EndChangeCheck())
            {
                UpdatePreview();
            }

            EditorGUILayout.Space(5);

            EditorGUI.BeginChangeCheck();
            _className = EditorGUILayout.TextField("Class Name:", _className);
            if (EditorGUI.EndChangeCheck())
            {
                UpdatePreview();
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Preview:", EditorStyles.boldLabel);
            var previewStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                fontSize = 10
            };

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(200));
            EditorGUILayout.TextArea(_preview, previewStyle, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                Close();
            }

            EditorGUI.BeginDisabledGroup(!StradaTemplates.IsValidClassName(_className));
            if (GUILayout.Button("Create", GUILayout.Width(80)))
            {
                CreateTemplate();
                Close();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private void UpdatePreview()
        {
            if (_templates == null || _templates.Count == 0 || _selectedIndex >= _templates.Count)
            {
                _preview = "No template selected.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_className))
            {
                _preview = "Enter a class name to see preview...";
                return;
            }

            if (!StradaTemplates.IsValidClassName(_className))
            {
                _preview = $"'{_className}' is not a valid C# identifier. Use letters, digits and " +
                           "underscores only, and do not start with a digit.";
                return;
            }

            var template = _templates[_selectedIndex];
            var namespaceName = TemplateContextDetector.ExtractNamespace(_folderPath);
            _preview = StradaTemplates.GenerateTemplate(template.Type, _className, namespaceName);
        }

        private void CreateTemplate()
        {
            if (_templates == null || _templates.Count == 0 || _selectedIndex >= _templates.Count)
                return;

            if (string.IsNullOrWhiteSpace(_className))
                return;

            var template = _templates[_selectedIndex];
            StradaTemplates.CreateFileFromTemplate(template.Type, _className, _folderPath);
        }
    }
}

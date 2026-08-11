using System.Text;
using Strada.Core.Editor.ModuleGenerator.Models;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator
{
    public partial class StradaModuleGenerator
    {
        private static class Styles
        {
            public static readonly GUIStyle HeaderStyle;
            public static readonly GUIStyle SectionHeaderStyle;
            public static readonly GUIStyle GroupHeaderStyle;
            public static readonly GUIStyle PreviewCodeStyle;

            public static readonly Color MainModuleColor = new Color(0.7f, 0.7f, 0.7f);
            public static readonly Color SubModuleColor = new Color(0.5f, 0.8f, 0.5f);
            public static readonly Color ScreenModuleColor = new Color(0.5f, 0.7f, 1.0f);
            public static readonly Color TestModuleColor = new Color(1.0f, 0.7f, 0.4f);
            public static readonly Color SuccessColor = new Color(0.4f, 0.8f, 0.4f);
            public static readonly Color SelectButtonColor = new Color(0.3f, 0.5f, 0.7f);

            static Styles()
            {
                HeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 18,
                    margin = new RectOffset(5, 5, 10, 10)
                };

                SectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 13,
                    margin = new RectOffset(0, 0, 10, 5)
                };

                GroupHeaderStyle = new GUIStyle(EditorStyles.foldout)
                {
                    fontStyle = FontStyle.Bold
                };

                PreviewCodeStyle = new GUIStyle(EditorStyles.textArea)
                {
                    font = Font.CreateDynamicFontFromOSFont("Consolas", 11),
                    wordWrap = false,
                    richText = true
                };

                RichTextLabelStyle = new GUIStyle(EditorStyles.label) { richText = true };
                WordWrapRichTextLabelStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            }

            public static readonly GUIStyle RichTextLabelStyle;
            public static readonly GUIStyle WordWrapRichTextLabelStyle;
        }

        private bool _validationDirty = true;
        private bool _lastValidationPassed;

        /// <summary>
        /// Flags the cached validation result as stale. Call this from every control that mutates
        /// the module definition.
        /// </summary>
        private void MarkValidationDirty()
        {
            _validationDirty = true;
        }

        private void OnFocus()
        {
            // Modules may have been added or removed on disk while the window was in the
            // background, so the cached "module already exists" answer can no longer be trusted.
            _validationDirty = true;
        }

        /// <summary>
        /// Re-runs ValidateAll only when an input actually changed.
        /// </summary>
        /// <remarks>
        /// ValidateAll reaches ModuleDiscovery.ModuleExists, which walks Assets/Modules
        /// recursively, calls GetTypes() on every loaded assembly and runs an AssetDatabase
        /// search. OnGUI runs at least twice per repaint and continuously while the pointer is
        /// over the window, so running it unconditionally made that scan the dominant cost of
        /// the window. Refreshing here - before any control that reads _validationMessages -
        /// keeps the Layout and Repaint passes of one frame in agreement.
        /// </remarks>
        private void RefreshValidationIfDirty()
        {
            if (!_validationDirty)
                return;

            _lastValidationPassed = ValidateAll();
            _validationDirty = false;
        }

        private bool CanGenerateCached()
        {
            return _generationState == GenerationState.Idle &&
                   !string.IsNullOrEmpty(_moduleDefinition?.ModuleName) &&
                   _lastValidationPassed;
        }

        private void DrawHeader()
        {
            RefreshValidationIfDirty();

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Strada Module Generator v{Version}", Styles.HeaderStyle);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(EditorGUIUtility.IconContent("_Help"), GUILayout.Width(30), GUILayout.Height(20)))
            {
                Application.OpenURL("https://github.com/strada-framework/strada-core");
            }

            if (GUILayout.Button(EditorGUIUtility.IconContent("d_Settings"), GUILayout.Width(30), GUILayout.Height(20)))
            {
                SettingsService.OpenProjectSettings("Project/Strada/Module Generator");
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Create a new Strada module with proper structure, services, controllers, and ECS components.",
                MessageType.Info);

            EditorGUILayout.Space(10);
        }

        private void DrawLeftPanel()
        {
            DrawModuleConfiguration();
            EditorGUILayout.Space(10);
            DrawComponentSelection();
            EditorGUILayout.Space(10);
            DrawModuleHierarchy();
        }

        private void DrawRightPanel()
        {
            DrawPreviewPanel();
        }

        private void DrawModuleConfiguration()
        {
            EditorGUILayout.LabelField("Module Configuration", Styles.SectionHeaderStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Module Name", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _moduleDefinition.ModuleName = EditorGUILayout.TextField(_moduleDefinition.ModuleName);
            if (EditorGUI.EndChangeCheck())
            {
                _moduleDefinition.ModuleName = SanitizeModuleName(_moduleDefinition.ModuleName);

                // Revalidate immediately rather than only flagging: DrawNameValidationStatus
                // below reads _validationMessages in this same pass.
                _validationDirty = true;
                RefreshValidationIfDirty();
            }

            DrawNameValidationStatus();

            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Module Type", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _moduleDefinition.ModuleType = (ModuleType)EditorGUILayout.EnumPopup(_moduleDefinition.ModuleType);
            if (EditorGUI.EndChangeCheck())
            {
                _moduleDefinition.ApplyTypeDefaults();
                MarkValidationDirty();
            }

            DrawModuleTypeDescription();

            EditorGUILayout.Space(5);

            if (_moduleDefinition.ModuleType == ModuleType.Sub ||
                _moduleDefinition.ModuleType == ModuleType.Screen ||
                _moduleDefinition.ModuleType == ModuleType.Test)
            {
                EditorGUILayout.LabelField("Parent Module", EditorStyles.boldLabel);
                DrawParentModuleSelector();
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Namespace", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _moduleDefinition.Namespace = EditorGUILayout.TextField(_moduleDefinition.Namespace);
            if (EditorGUI.EndChangeCheck())
            {
                MarkValidationDirty();
            }
            if (!string.IsNullOrEmpty(_moduleDefinition.ModuleName))
            {
                EditorGUILayout.LabelField($".{_moduleDefinition.ModuleName}", GUILayout.Width(150));
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Target Path", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _moduleDefinition.TargetPath = EditorGUILayout.TextField(_moduleDefinition.TargetPath);
            if (EditorGUI.EndChangeCheck())
            {
                MarkValidationDirty();
            }
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                var path = EditorUtility.OpenFolderPanel("Select Module Location", _moduleDefinition.TargetPath, "");
                if (!string.IsNullOrEmpty(path))
                {
                    if (path.StartsWith(Application.dataPath))
                        _moduleDefinition.TargetPath = "Assets" + path.Substring(Application.dataPath.Length);

                    MarkValidationDirty();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawNameValidationStatus()
        {
            if (string.IsNullOrEmpty(_moduleDefinition.ModuleName))
            {
                EditorGUILayout.HelpBox("Enter a module name to continue.", MessageType.None);
                return;
            }

            var error = _validationMessages?.Find(m => m.Severity == ValidationSeverity.Error && m.Field == "ModuleName");
            if (error != null)
            {
                EditorGUILayout.HelpBox(error.Message, MessageType.Error);
                return;
            }

            var warning = _validationMessages?.Find(m => m.Severity == ValidationSeverity.Warning && m.Field == "ModuleName");
            if (warning != null)
            {
                EditorGUILayout.HelpBox(warning.Message, MessageType.Warning);
                return;
            }

            var oldColor = GUI.contentColor;
            GUI.contentColor = Styles.SuccessColor;
            EditorGUILayout.LabelField("✓ Valid module name");
            GUI.contentColor = oldColor;
        }

        private void DrawModuleTypeDescription()
        {
            var description = _moduleDefinition.ModuleType switch
            {
                ModuleType.Main => "Standalone module with full structure and assembly definition.",
                ModuleType.Sub => "Child module that inherits parent's assembly definition.",
                ModuleType.Screen => "Screen/UI module with View and Mediator components.",
                ModuleType.Test => "Test module for unit and integration testing.",
                _ => ""
            };

            var color = _moduleDefinition.ModuleType switch
            {
                ModuleType.Main => Styles.MainModuleColor,
                ModuleType.Sub => Styles.SubModuleColor,
                ModuleType.Screen => Styles.ScreenModuleColor,
                ModuleType.Test => Styles.TestModuleColor,
                _ => Color.gray
            };

            var oldColor = GUI.color;
            GUI.color = new Color(color.r, color.g, color.b, 0.3f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.color = oldColor;
            EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        private void DrawParentModuleSelector()
        {
            if (string.IsNullOrEmpty(_moduleDefinition.ParentModuleName))
            {
                EditorGUILayout.HelpBox("Select a parent module from the hierarchy below.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();

                var oldColor = GUI.contentColor;
                GUI.contentColor = Styles.SuccessColor;
                EditorGUILayout.LabelField($"✓ {_moduleDefinition.ParentModuleName}Module");
                GUI.contentColor = oldColor;

                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    _moduleDefinition.ParentModuleName = "";
                    _moduleDefinition.ParentModulePath = "";
                    _moduleDefinition.TargetPath = "Assets/Modules";
                    MarkValidationDirty();
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawComponentSelection()
        {
            EditorGUILayout.LabelField("Components to Generate", Styles.SectionHeaderStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var components = _moduleDefinition.Components;

            EditorGUI.BeginChangeCheck();

            _ecsGroupExpanded = EditorGUILayout.Foldout(_ecsGroupExpanded, "ECS Components", true, Styles.GroupHeaderStyle);
            if (_ecsGroupExpanded)
            {
                EditorGUI.indentLevel++;
                components.EcsSystem = EditorGUILayout.Toggle("System", components.EcsSystem);
                components.EcsComponent = EditorGUILayout.Toggle("Component", components.EcsComponent);
                components.EntityMediator = EditorGUILayout.Toggle("Entity Mediator", components.EntityMediator);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(3);

            _mvcsGroupExpanded = EditorGUILayout.Foldout(_mvcsGroupExpanded, "MVCS Pattern", true, Styles.GroupHeaderStyle);
            if (_mvcsGroupExpanded)
            {
                EditorGUI.indentLevel++;
                components.ServiceInterface = EditorGUILayout.Toggle("Service Interface", components.ServiceInterface);
                components.Service = EditorGUILayout.Toggle("Service", components.Service);
                components.Controller = EditorGUILayout.Toggle("Controller", components.Controller);
                components.Model = EditorGUILayout.Toggle("Model", components.Model);
                components.View = EditorGUILayout.Toggle("View", components.View);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(3);

            _dataGroupExpanded = EditorGUILayout.Foldout(_dataGroupExpanded, "Data & Events", true, Styles.GroupHeaderStyle);
            if (_dataGroupExpanded)
            {
                EditorGUI.indentLevel++;
                components.ConfigData = EditorGUILayout.Toggle("ConfigData (CD_*)", components.ConfigData);
                components.ValueObject = EditorGUILayout.Toggle("ValueObject", components.ValueObject);
                components.Events = EditorGUILayout.Toggle("Events", components.Events);
                components.Signals = EditorGUILayout.Toggle("Signals", components.Signals);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(3);

            _infraGroupExpanded = EditorGUILayout.Foldout(_infraGroupExpanded, "Infrastructure", true, Styles.GroupHeaderStyle);
            if (_infraGroupExpanded)
            {
                EditorGUI.indentLevel++;

                EditorGUI.BeginDisabledGroup(_moduleDefinition.ModuleType != ModuleType.Main);
                components.ModuleConfig = EditorGUILayout.Toggle("ModuleConfig", components.ModuleConfig);
                components.AssemblyDefinition = EditorGUILayout.Toggle("Assembly Definition", components.AssemblyDefinition);
                EditorGUI.EndDisabledGroup();

                components.RuntimeTests = EditorGUILayout.Toggle("Runtime Tests", components.RuntimeTests);
                components.EditorTests = EditorGUILayout.Toggle("Editor Tests", components.EditorTests);
                components.EditorScripts = EditorGUILayout.Toggle("Editor Scripts", components.EditorScripts);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(3);

            _foldersGroupExpanded = EditorGUILayout.Foldout(_foldersGroupExpanded, "Optional Folders", true, Styles.GroupHeaderStyle);
            if (_foldersGroupExpanded)
            {
                EditorGUI.indentLevel++;
                components.FolderResources = EditorGUILayout.Toggle("Resources", components.FolderResources);
                components.FolderPrefabs = EditorGUILayout.Toggle("Prefabs", components.FolderPrefabs);
                components.FolderScenes = EditorGUILayout.Toggle("Scenes", components.FolderScenes);
                components.FolderSprites = EditorGUILayout.Toggle("Sprites", components.FolderSprites);
                components.FolderArt = EditorGUILayout.Toggle("Art", components.FolderArt);
                components.FolderAudio = EditorGUILayout.Toggle("Audio", components.FolderAudio);
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                MarkValidationDirty();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawModuleHierarchy()
        {
            EditorGUILayout.LabelField("Parent Module", Styles.SectionHeaderStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinHeight(200));

            EditorGUILayout.BeginHorizontal();
            _moduleSearchFilter = EditorGUILayout.TextField(_moduleSearchFilter, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                RefreshExistingModules();
                MarkValidationDirty();
            }
            EditorGUILayout.EndHorizontal();

            _hierarchyScrollPosition = EditorGUILayout.BeginScrollView(_hierarchyScrollPosition);

            if (_existingModules != null)
            {
                foreach (var module in _existingModules)
                {
                    DrawModuleTreeNode(module);
                }
            }

            if (_existingModules == null || _existingModules.Count == 0)
            {
                EditorGUILayout.HelpBox("No modules found in Assets/Modules", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawModuleTreeNode(ModuleInfoData module)
        {
            if (!string.IsNullOrEmpty(_moduleSearchFilter))
            {
                if (!ModuleMatchesSearch(module, _moduleSearchFilter))
                    return;
            }

            EditorGUILayout.BeginHorizontal();

            var indent = module.Depth * 20;
            GUILayout.Space(indent);

            if (module.HasChildren)
            {
                var foldoutContent = module.IsExpanded ? "▼" : "▶";
                if (GUILayout.Button(foldoutContent, EditorStyles.label, GUILayout.Width(15)))
                {
                    module.IsExpanded = !module.IsExpanded;
                }
            }
            else
            {
                GUILayout.Space(18);
            }

            var icon = module.HasModuleConfig ? "d_ScriptableObject Icon" : "d_Folder Icon";
            var iconContent = EditorGUIUtility.IconContent(icon);
            GUILayout.Label(iconContent, GUILayout.Width(18), GUILayout.Height(18));

            var oldColor = GUI.contentColor;
            GUI.contentColor = module.TypeColor;

            var displayText = module.DisplayName;
            if (!string.IsNullOrEmpty(module.TypeLabel))
            {
                displayText = $"{module.DisplayName} <color=#{ColorUtility.ToHtmlStringRGB(module.TypeColor)}>{module.TypeLabel}</color>";
            }

            GUILayout.Label(displayText, Styles.RichTextLabelStyle);

            GUI.contentColor = oldColor;

            GUILayout.FlexibleSpace();

            var oldBgColor = GUI.backgroundColor;
            GUI.backgroundColor = Styles.SelectButtonColor;

            if (GUILayout.Button("Select", GUILayout.Width(55), GUILayout.Height(18)))
            {
                SelectParentModule(module);
            }

            GUI.backgroundColor = oldBgColor;

            EditorGUILayout.EndHorizontal();

            if (module.IsExpanded && module.HasChildren)
            {
                foreach (var child in module.SubModules)
                {
                    DrawModuleTreeNode(child);
                }
            }
        }

        private bool ModuleMatchesSearch(ModuleInfoData module, string search)
        {
            if (module.Name.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (module.HasChildren)
            {
                foreach (var child in module.SubModules)
                {
                    if (ModuleMatchesSearch(child, search))
                        return true;
                }
            }

            return false;
        }

        private void SelectParentModule(ModuleInfoData module)
        {
            _moduleDefinition.ParentModuleName = module.Name;
            _moduleDefinition.ParentModulePath = module.Path;

            if (_moduleDefinition.ModuleType == ModuleType.Main)
            {
                _moduleDefinition.ModuleType = ModuleType.Sub;
                _moduleDefinition.ApplyTypeDefaults();
            }

            _moduleDefinition.TargetPath = module.Path;
            MarkValidationDirty();
        }

        private void DrawPreviewPanel()
        {
            EditorGUILayout.LabelField("Preview", Styles.SectionHeaderStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _selectedPreviewTab = GUILayout.Toolbar(_selectedPreviewTab, new[] { "Structure", "Code Preview" });

            EditorGUILayout.Space(5);

            _previewScrollPosition = EditorGUILayout.BeginScrollView(_previewScrollPosition, GUILayout.MinHeight(400));

            if (_selectedPreviewTab == 0)
            {
                DrawStructurePreview();
            }
            else
            {
                DrawCodePreview();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawStructurePreview()
        {
            if (string.IsNullOrEmpty(_moduleDefinition.ModuleName))
            {
                EditorGUILayout.LabelField("Enter a module name to see preview.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var preview = GenerateStructurePreview();
            EditorGUILayout.LabelField(preview, Styles.WordWrapRichTextLabelStyle);
        }

        private void DrawCodePreview()
        {
            if (string.IsNullOrEmpty(_moduleDefinition.ModuleName))
            {
                EditorGUILayout.LabelField("Enter a module name to see preview.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var files = GetPreviewFileList();
            if (files.Length == 0)
            {
                EditorGUILayout.LabelField("No files to preview.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            _selectedFileIndex = Mathf.Clamp(_selectedFileIndex, 0, files.Length - 1);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(30)))
            {
                _selectedFileIndex = Mathf.Max(0, _selectedFileIndex - 1);
            }

            _selectedFileIndex = EditorGUILayout.Popup(_selectedFileIndex, files);

            if (GUILayout.Button("▶", GUILayout.Width(30)))
            {
                _selectedFileIndex = Mathf.Min(files.Length - 1, _selectedFileIndex + 1);
            }

            var codePreview = GenerateCodePreview(files[_selectedFileIndex]);

            if (GUILayout.Button("Copy", GUILayout.Width(50)))
            {
                EditorGUIUtility.systemCopyBuffer = codePreview;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            EditorGUILayout.TextArea(codePreview, Styles.PreviewCodeStyle, GUILayout.ExpandHeight(true));
        }

        private string GenerateStructurePreview()
        {
            var sb = new StringBuilder();
            var name = _moduleDefinition.ModuleName;
            var components = _moduleDefinition.Components;

            sb.AppendLine($"<color=#88aaff>📁 {_moduleDefinition.TargetPath}/{name}Module/</color>");
            sb.AppendLine($"   <color=#888888>Namespace: {_moduleDefinition.FullNamespace}</color>");
            sb.AppendLine();

            sb.AppendLine("   <color=#ffcc44>📁 Scripts/</color>");

            if (components.ServiceInterface)
            {
                sb.AppendLine("      📁 Interfaces/");
                sb.AppendLine($"         📄 I{name}Service.cs");
            }

            if (components.Controller)
            {
                sb.AppendLine("      📁 Controllers/");
                sb.AppendLine($"         📄 {name}Controller.cs");
            }

            if (components.Service)
            {
                sb.AppendLine("      📁 Services/");
                sb.AppendLine($"         📄 {name}Service.cs");
            }

            if (components.Model)
            {
                sb.AppendLine("      📁 Models/");
                sb.AppendLine($"         📄 {name}Model.cs");
            }

            if (components.View)
            {
                sb.AppendLine("      📁 Views/");
                sb.AppendLine($"         📄 {name}View.cs");
            }

            if (components.EcsSystem)
            {
                sb.AppendLine("      📁 Systems/");
                sb.AppendLine($"         📄 {name}System.cs");
            }

            if (components.EcsComponent)
            {
                sb.AppendLine("      📁 Components/");
                sb.AppendLine($"         📄 {name}Component.cs");
            }

            if (components.Events || components.Signals)
            {
                sb.AppendLine("      📁 Events/");
                if (components.Events)
                    sb.AppendLine($"         📄 {name}Events.cs");
                if (components.Signals)
                    sb.AppendLine($"         📄 {name}Signals.cs");
            }

            if (components.ConfigData || components.ValueObject)
            {
                sb.AppendLine("      📁 Data/");
                if (components.ConfigData)
                {
                    sb.AppendLine("         📁 UnityObjects/");
                    sb.AppendLine($"            📄 CD_{name}.cs");
                }
                if (components.ValueObject)
                {
                    sb.AppendLine("         📁 ValueObjects/");
                    sb.AppendLine($"            📄 {name}Config.cs");
                }
            }

            sb.AppendLine();

            if (components.ModuleConfig)
                sb.AppendLine($"   <color=#44ff88>📄 {name}ModuleConfig.cs</color>");

            if (components.AssemblyDefinition)
                sb.AppendLine($"   📄 {name}.asmdef");

            if (components.EditorScripts)
            {
                sb.AppendLine();
                sb.AppendLine("   📁 Editor/");
                sb.AppendLine($"      📄 {name}.Editor.asmdef");
            }

            if (components.RuntimeTests || components.EditorTests)
            {
                sb.AppendLine();
                sb.AppendLine("   📁 Tests/");
                if (components.RuntimeTests)
                {
                    sb.AppendLine("      📁 Runtime/");
                    sb.AppendLine($"         📄 {name}Tests.cs");
                }
                if (components.EditorTests)
                {
                    sb.AppendLine("      📁 Editor/");
                    sb.AppendLine($"         📄 {name}EditorTests.cs");
                }
                sb.AppendLine($"      📄 {name}.Tests.asmdef");
            }

            var hasOptionalFolders = components.FolderResources || components.FolderPrefabs ||
                                     components.FolderScenes || components.FolderSprites ||
                                     components.FolderArt || components.FolderAudio;

            if (hasOptionalFolders)
            {
                sb.AppendLine();
                sb.AppendLine("   <color=#aaaaaa>Optional Folders:</color>");
                if (components.FolderResources) sb.AppendLine("   📁 Resources/");
                if (components.FolderPrefabs) sb.AppendLine("   📁 Prefabs/");
                if (components.FolderScenes) sb.AppendLine("   📁 Scenes/");
                if (components.FolderSprites) sb.AppendLine("   📁 Sprites/");
                if (components.FolderArt) sb.AppendLine("   📁 Art/");
                if (components.FolderAudio) sb.AppendLine("   📁 Audio/");
            }

            if (_moduleDefinition.Dependencies.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("   <color=#888888>Dependencies:</color>");
                foreach (var dep in _moduleDefinition.Dependencies)
                {
                    sb.AppendLine($"      → {dep}");
                }
            }

            return sb.ToString();
        }

        private string[] GetPreviewFileList()
        {
            var files = new System.Collections.Generic.List<string>();
            var name = _moduleDefinition.ModuleName;
            var components = _moduleDefinition.Components;

            if (components.ModuleConfig) files.Add($"{name}ModuleConfig.cs");
            if (components.ServiceInterface) files.Add($"I{name}Service.cs");
            if (components.Service) files.Add($"{name}Service.cs");
            if (components.Controller) files.Add($"{name}Controller.cs");
            if (components.Model) files.Add($"{name}Model.cs");
            if (components.View) files.Add($"{name}View.cs");
            if (components.EcsSystem) files.Add($"{name}System.cs");
            if (components.EcsComponent) files.Add($"{name}Component.cs");
            if (components.ConfigData) files.Add($"CD_{name}.cs");
            if (components.ValueObject) files.Add($"{name}Config.cs");
            if (components.Events) files.Add($"{name}Events.cs");
            if (components.Signals) files.Add($"{name}Signals.cs");

            return files.ToArray();
        }

        private string GenerateCodePreview(string fileName)
        {
            var name = _moduleDefinition.ModuleName;
            var ns = _moduleDefinition.FullNamespace;

            return TemplateProcessor.GeneratePreview(fileName, name, ns, _settings, _moduleDefinition.Components);
        }

        private void DrawValidationStatus()
        {
            if (_validationMessages == null || _validationMessages.Count == 0)
                return;

            EditorGUILayout.Space(5);

            foreach (var msg in _validationMessages)
            {
                if (msg.Field == "ModuleName") continue;

                if (msg.Severity == ValidationSeverity.Error)
                    EditorGUILayout.HelpBox(msg.Message, MessageType.Error);
                else if (msg.Severity == ValidationSeverity.Warning)
                    EditorGUILayout.HelpBox(msg.Message, MessageType.Warning);
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            _moduleDefinition.RegisterInBootstrapper = EditorGUILayout.ToggleLeft("Register in GameBootstrapper", _moduleDefinition.RegisterInBootstrapper);
            _moduleDefinition.CreateModuleConfigAsset = EditorGUILayout.ToggleLeft("Create ModuleConfig Asset", _moduleDefinition.CreateModuleConfigAsset);
            _moduleDefinition.OpenFolderAfterCreate = EditorGUILayout.ToggleLeft("Open Folder", _moduleDefinition.OpenFolderAfterCreate);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            var canGenerate = CanGenerateCached();
            var buttonColor = canGenerate ? new Color(0.2f, 0.6f, 0.3f) : Color.gray;
            var buttonText = _generationState switch
            {
                GenerationState.InProgress => "Creating...",
                GenerationState.Completed => "Created!",
                GenerationState.Failed => "Failed - See Console",
                _ => "Create Module (Ctrl+Enter)"
            };

            var oldBgColor = GUI.backgroundColor;
            GUI.backgroundColor = buttonColor;

            EditorGUI.BeginDisabledGroup(!canGenerate || _generationState == GenerationState.InProgress);
            if (GUILayout.Button(buttonText, GUILayout.Height(40)))
            {
                StartGeneration();
            }
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = oldBgColor;
        }

        private static string SanitizeModuleName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";

            var result = new StringBuilder();

            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c))
                    result.Append(c);
            }

            if (result.Length > 0 && char.IsLower(result[0]))
            {
                result[0] = char.ToUpperInvariant(result[0]);
            }

            return result.ToString();
        }
    }
}

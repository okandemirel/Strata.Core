using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Strada.Core.Bootstrap;
using Strada.Core.Modules;

namespace Strada.Core.Editor.Inspectors
{
    /// <summary>
    /// Custom editor for GameBootstrapperConfig ScriptableObjects.
    /// Provides an overview of all modules and their initialization order.
    /// </summary>
    [CustomEditor(typeof(GameBootstrapperConfig))]
    public class GameBootstrapperConfigEditor : UnityEditor.Editor
    {
        private SerializedProperty _modulesProp;
        private SerializedProperty _verboseLoggingProp;
        private SerializedProperty _validateOnStartProp;
        private SerializedProperty _failOnValidationErrorProp;
        private SerializedProperty _asyncInitializationProp;

        private ReorderableList _modulesList;
        private bool _showValidation = true;
        private List<string> _validationErrors = new List<string>();

        private static readonly Color ValidColor = new Color(0.3f, 0.8f, 0.3f, 1f);
        private static readonly Color ErrorColor = new Color(0.8f, 0.3f, 0.3f, 1f);
        private static readonly Color WarningColor = new Color(0.9f, 0.7f, 0.2f, 1f);

        private void OnEnable()
        {
            _modulesProp = serializedObject.FindProperty("_modules");
            _verboseLoggingProp = serializedObject.FindProperty("_verboseLogging");
            _validateOnStartProp = serializedObject.FindProperty("_validateOnStart");
            _failOnValidationErrorProp = serializedObject.FindProperty("_failOnValidationError");
            _asyncInitializationProp = serializedObject.FindProperty("_asyncInitialization");

            SetupModulesList();
            ValidateConfiguration();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawHeader();
            EditorGUILayout.Space(10);

            DrawModulesSection();
            EditorGUILayout.Space(10);

            DrawSettingsSection();
            EditorGUILayout.Space(10);

            DrawValidationSection();
            EditorGUILayout.Space(10);

            DrawActions();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawHeader()
        {
            var config = target as GameBootstrapperConfig;
            if (config == null)
            {
                EditorGUILayout.HelpBox("Target is not a GameBootstrapperConfig.", MessageType.Error);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            var statusColor = _validationErrors.Count == 0 ? ValidColor : ErrorColor;
            DrawColoredBox(statusColor);

            EditorGUILayout.LabelField("Game Bootstrapper Config", EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            var enabledCount = config.EnabledModuleCount;
            var totalCount = config.Modules.Count;
            EditorGUILayout.LabelField($"Modules: {enabledCount}/{totalCount}", EditorStyles.miniLabel, GUILayout.Width(100));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawModulesSection()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Modules", EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Find All", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                FindAllModuleConfigs();
            }

            if (GUILayout.Button("Sort", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                SortModulesByPriority();
            }

            if (GUILayout.Button("+", EditorStyles.miniButtonRight, GUILayout.Width(25)))
            {
                _modulesProp.arraySize++;
            }

            EditorGUILayout.EndHorizontal();

            _modulesList.DoLayoutList();

            DrawInitializationOrder();
        }

        private void DrawInitializationOrder()
        {
            var config = target as GameBootstrapperConfig;
            var enabledModules = new List<ModuleConfig>();
            foreach (var module in config.GetEnabledModules())
            {
                enabledModules.Add(module);
            }

            if (enabledModules.Count > 0)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Initialization Order:", EditorStyles.miniLabel);

                for (int i = 0; i < enabledModules.Count; i++)
                {
                    var module = enabledModules[i];
                    EditorGUILayout.LabelField($"  {i + 1}. {module.ModuleName} (P: {module.Priority}, Systems: {module.Systems.Count})", EditorStyles.miniLabel);
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(_verboseLoggingProp, new GUIContent("Verbose Logging"));
            EditorGUILayout.PropertyField(_validateOnStartProp, new GUIContent("Validate On Start"));
            EditorGUILayout.PropertyField(_failOnValidationErrorProp, new GUIContent("Fail On Validation Error"));
            EditorGUILayout.PropertyField(_asyncInitializationProp, new GUIContent("Async Initialization"));

            EditorGUI.indentLevel--;
        }

        private void DrawValidationSection()
        {
            EditorGUILayout.BeginHorizontal();
            _showValidation = EditorGUILayout.Foldout(_showValidation, "Validation", true, EditorStyles.foldoutHeader);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Validate", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                ValidateConfiguration();
            }

            EditorGUILayout.EndHorizontal();

            if (_showValidation)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                if (_validationErrors.Count == 0)
                {
                    DrawColoredLabel("✓ Configuration is valid", ValidColor);
                }
                else
                {
                    DrawColoredLabel($"✗ {_validationErrors.Count} validation error(s)", ErrorColor);

                    EditorGUILayout.Space(5);

                    foreach (var error in _validationErrors)
                    {
                        EditorGUILayout.HelpBox(error, MessageType.Error);
                    }
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Open Dashboard"))
            {
                EditorApplication.ExecuteMenuItem("Strada/Dashboard");
            }

            if (GUILayout.Button("View Dependency Graph"))
            {
                // Called directly rather than through ExecuteMenuItem: the window is registered
                // under "Strada/Debugger/Dependency Graph", so the old path resolved to nothing
                // and the button silently did nothing but log a "menu not found" error. A direct
                // call is also the only form the compiler can check.
                Graph.DependencyGraphWindow.ShowWindow();
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawColoredBox(Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUILayout.Box("", GUILayout.Width(10), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUI.color = previousColor;
        }

        private static void DrawColoredLabel(string text, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
            GUI.color = previousColor;
        }

        private void SetupModulesList()
        {
            _modulesList = new ReorderableList(serializedObject, _modulesProp, true, true, false, true);

            _modulesList.drawHeaderCallback = rect =>
            {
                var col1 = new Rect(rect.x, rect.y, 18, rect.height);
                var col2 = new Rect(col1.xMax + 4, rect.y, rect.width - 80, rect.height);
                var col3 = new Rect(col2.xMax + 4, rect.y, 50, rect.height);

                EditorGUI.LabelField(col1, "");
                EditorGUI.LabelField(col2, "Module Config");
                EditorGUI.LabelField(col3, "Priority");
            };

            _modulesList.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                var element = _modulesProp.GetArrayElementAtIndex(index);
                rect.y += 2;
                rect.height = EditorGUIUtility.singleLineHeight;
                EditorGUI.PropertyField(rect, element, GUIContent.none);
            };

            _modulesList.onReorderCallback = list =>
            {
                ValidateConfiguration();
            };

            _modulesList.onRemoveCallback = list =>
            {
                ReorderableList.defaultBehaviours.DoRemoveButton(list);
                ValidateConfiguration();
            };
        }

        private void ValidateConfiguration()
        {
            var config = target as GameBootstrapperConfig;
            config.Validate(out _validationErrors);
        }

        private void FindAllModuleConfigs()
        {
            var configs = AssetDatabase.FindAssets("t:ModuleConfig")
                .Select(guid => AssetDatabase.LoadAssetAtPath<ModuleConfig>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(config => config != null)
                .ToList();

            if (configs.Count == 0)
            {
                EditorUtility.DisplayDialog("No ModuleConfigs Found",
                    "No ModuleConfig assets were found in the project.\n\n" +
                    "Create a new ModuleConfig by right-clicking in the Project window and selecting:\n" +
                    "Create > Strada > Module Config",
                    "OK");
                return;
            }

            configs.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Add All"), false, () =>
            {
                AddModuleConfigs(configs);
            });

            menu.AddSeparator("");

            foreach (var config in configs)
            {
                var configCopy = config;
                var existing = HasModule(config);
                var menuLabel = existing ?
                    $"✓ {config.ModuleName} (already added)" :
                    $"{config.ModuleName} (P: {config.Priority})";

                if (!existing)
                {
                    menu.AddItem(new GUIContent(menuLabel), false, () =>
                    {
                        AddModuleConfig(configCopy);
                    });
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent(menuLabel));
                }
            }

            menu.ShowAsContext();
        }

        private bool HasModule(ModuleConfig config)
        {
            for (int i = 0; i < _modulesProp.arraySize; i++)
            {
                var element = _modulesProp.GetArrayElementAtIndex(i);
                var configProp = element.FindPropertyRelative("_config");
                if (configProp.objectReferenceValue == config)
                {
                    return true;
                }
            }
            return false;
        }

        private void AddModuleConfigs(List<ModuleConfig> configs)
        {
            Undo.RecordObject(target, "Add Module Configs");
            int added = 0;
            foreach (var config in configs)
            {
                if (!HasModule(config))
                {
                    AddModuleConfigInternal(config);
                    added++;
                }
            }

            serializedObject.ApplyModifiedProperties();
            SortModulesByPriority();
            ValidateConfiguration();

            EditorUtility.DisplayDialog("Modules Added",
                $"Added {added} modules to the configuration.",
                "OK");
        }

        private void AddModuleConfig(ModuleConfig config)
        {
            Undo.RecordObject(target, "Add Module Config");
            AddModuleConfigInternal(config);
            serializedObject.ApplyModifiedProperties();
        }

        private void AddModuleConfigInternal(ModuleConfig config)
        {
            _modulesProp.arraySize++;
            var newElement = _modulesProp.GetArrayElementAtIndex(_modulesProp.arraySize - 1);

            newElement.FindPropertyRelative("_config").objectReferenceValue = config;
            newElement.FindPropertyRelative("_enabled").boolValue = true;
        }

        private void SortModulesByPriority()
        {
            Undo.RecordObject(target, "Sort Modules By Priority");

            var entries = new List<(int index, int priority)>();
            for (int i = 0; i < _modulesProp.arraySize; i++)
            {
                var element = _modulesProp.GetArrayElementAtIndex(i);
                var configProp = element.FindPropertyRelative("_config");
                var moduleConfig = configProp.objectReferenceValue as ModuleConfig;
                var priority = moduleConfig?.Priority ?? int.MaxValue;
                entries.Add((i, priority));
            }

            entries.Sort((a, b) => a.priority.CompareTo(b.priority));

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].index != i)
                {
                    _modulesProp.MoveArrayElement(entries[i].index, i);
                    for (int j = i + 1; j < entries.Count; j++)
                    {
                        if (entries[j].index < entries[i].index && entries[j].index >= i)
                        {
                            entries[j] = (entries[j].index + 1, entries[j].priority);
                        }
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
            ValidateConfiguration();
        }
    }
}

using System;
using System.Collections.Generic;
using Strada.Core.Editor.ModuleGenerator.Config;
using Strada.Core.Editor.ModuleGenerator.Models;
using Strada.Core.Editor.ModuleGenerator.Pipeline;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.ModuleGenerator
{
    /// <summary>
    /// Advanced module generator for Strada framework.
    /// Creates complete module structures with proper architecture.
    /// </summary>
    public partial class StradaModuleGenerator : EditorWindow
    {
        private const string Version = "2.0";
        private const string WindowTitle = "Strada Module Generator";

        private static readonly Vector2 MinWindowSize = new Vector2(750, 800);
        private static readonly Vector2 DefaultWindowSize = new Vector2(900, 850);

        private ModuleDefinition _moduleDefinition;
        private GenerationContext _context;
        private GenerationPipeline _pipeline;
        private List<ValidationMessage> _validationMessages;

        private StradaGeneratorSettings _settings;
        private DirectoryStructureConfig _directoryConfig;

        private GenerationState _generationState = GenerationState.Idle;
        private string _lastGeneratedModulePath;

        private Vector2 _mainScrollPosition;
        private Vector2 _previewScrollPosition;
        private Vector2 _hierarchyScrollPosition;

        private int _selectedPreviewTab;
        private int _selectedFileIndex;

        private bool _ecsGroupExpanded = true;
        private bool _mvcsGroupExpanded = true;
        private bool _dataGroupExpanded = true;
        private bool _infraGroupExpanded = true;
        private bool _foldersGroupExpanded = true;

        private List<ModuleInfoData> _existingModules;
        private string _moduleSearchFilter = "";

        [MenuItem("Strada/Module Generator", priority = 1)]
        public static void ShowWindow()
        {
            var window = GetWindow<StradaModuleGenerator>(WindowTitle);
            window.minSize = MinWindowSize;
            window.Show();
        }

        [MenuItem("Assets/Create/Strada/New Module Here", priority = 0)]
        public static void CreateModuleAtSelection()
        {
            var path = GetSelectedFolderPath();
            var window = GetWindow<StradaModuleGenerator>(WindowTitle);
            window.SetTargetPath(path);
            window.Show();
        }

        private static string GetSelectedFolderPath()
        {
            var path = "Assets/Modules";

            foreach (var obj in Selection.GetFiltered<UnityEngine.Object>(SelectionMode.Assets))
            {
                path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) && System.IO.Directory.Exists(path))
                    return path;

                if (!string.IsNullOrEmpty(path))
                {
                    path = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
            }

            return path;
        }

        public void SetTargetPath(string path, bool validate = true)
        {
            if (_moduleDefinition == null) return;
            _moduleDefinition.TargetPath = path;
            if (validate) ValidateTargetPath();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle, EditorGUIUtility.IconContent("d_Prefab Icon").image);

            LoadSettings();
            InitializeModuleDefinition();
            RefreshExistingModules();

            _pipeline = new GenerationPipeline();
            _validationMessages = new List<ValidationMessage>();
        }

        private void OnDisable()
        {
            SaveState();
        }

        private void OnGUI()
        {
            _mainScrollPosition = EditorGUILayout.BeginScrollView(_mainScrollPosition);

            DrawHeader();

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.42f));
            DrawLeftPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawRightPanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            DrawValidationStatus();
            DrawActions();

            EditorGUILayout.EndScrollView();

            HandleKeyboardShortcuts();
        }

        private void LoadSettings()
        {
            _settings = StradaGeneratorSettings.GetOrCreateSettings();
            _directoryConfig = DirectoryStructureConfig.GetOrCreateConfig();
        }

        private void InitializeModuleDefinition()
        {
            _moduleDefinition = new ModuleDefinition();
            LoadState();
        }

        private void RefreshExistingModules()
        {
            _existingModules = ModuleDiscovery.DiscoverModules();
        }

        private void SaveState()
        {
            if (_moduleDefinition == null) return;

            EditorPrefs.SetString("Strada_Gen_Namespace", _moduleDefinition.Namespace);
            EditorPrefs.SetString("Strada_Gen_TargetPath", _moduleDefinition.TargetPath);
            EditorPrefs.SetInt("Strada_Gen_ModuleType", (int)_moduleDefinition.ModuleType);
            EditorPrefs.SetBool("Strada_Gen_RegisterBootstrapper", _moduleDefinition.RegisterInBootstrapper);
            EditorPrefs.SetBool("Strada_Gen_CreateAsset", _moduleDefinition.CreateModuleConfigAsset);
        }

        private void LoadState()
        {
            if (_moduleDefinition == null) return;

            _moduleDefinition.Namespace = EditorPrefs.GetString("Strada_Gen_Namespace", "Game.Modules");
            _moduleDefinition.TargetPath = EditorPrefs.GetString("Strada_Gen_TargetPath", "Assets/Modules");
            _moduleDefinition.ModuleType = (ModuleType)EditorPrefs.GetInt("Strada_Gen_ModuleType", 0);
            _moduleDefinition.RegisterInBootstrapper = EditorPrefs.GetBool("Strada_Gen_RegisterBootstrapper", true);
            _moduleDefinition.CreateModuleConfigAsset = EditorPrefs.GetBool("Strada_Gen_CreateAsset", true);
        }

        private void HandleKeyboardShortcuts()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.control && e.keyCode == KeyCode.Return)
                {
                    if (CanGenerate())
                    {
                        StartGeneration();
                        e.Use();
                    }
                }
            }
        }

        private bool CanGenerate()
        {
            return _generationState == GenerationState.Idle &&
                   !string.IsNullOrEmpty(_moduleDefinition?.ModuleName) &&
                   ValidateAll();
        }
    }

    public enum GenerationState
    {
        Idle,
        InProgress,
        Completed,
        Failed
    }
}

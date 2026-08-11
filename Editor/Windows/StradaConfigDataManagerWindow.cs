using System;
using System.Collections.Generic;
using System.Reflection;
using Strada.Core.Data;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Central management window for all CD_ (Config Data) ScriptableObjects.
    /// Provides quick access, creation, validation, and organization of configs.
    /// Implements the Quantum-inspired ScriptableObject pattern.
    /// </summary>
    public class StradaConfigDataManagerWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private string _searchFilter = "";
        private string _typeFilter = "";
        private ConfigCategory _selectedCategory = ConfigCategory.All;
        private List<ConfigAsset> _cachedConfigs;
        private bool _needsRefresh = true;
        private HashSet<ConfigAsset> _selectedConfigs = new HashSet<ConfigAsset>();
        private Dictionary<ConfigCategory, bool> _categoryFoldouts = new Dictionary<ConfigCategory, bool>();
        private Dictionary<ConfigAsset, ValidationResult> _validationResults = new Dictionary<ConfigAsset, ValidationResult>();

        // Filtering and grouping used to run from OnGUI - twice per pass, since both the stats
        // header and the list asked for the filtered set - so they are computed only when the
        // filter fields or the underlying config set actually change.
        private List<ConfigAsset> _filteredConfigs;
        private readonly Dictionary<ConfigCategory, List<ConfigAsset>> _groupedConfigs =
            new Dictionary<ConfigCategory, List<ConfigAsset>>();
        private readonly List<ConfigCategory> _sortedCategories = new List<ConfigCategory>();
        private int _configsVersion;
        private int _filterVersion = -1;
        private string _filterNameSnapshot;
        private string _filterTypeSnapshot;
        private ConfigCategory _filterCategorySnapshot = ConfigCategory.All;
        private bool _showValidationErrors = false;
        private bool _selectAll = false;

        private bool _showCreationWizard = false;
        private string _newConfigName = "";
        private ConfigCategory _newConfigCategory = ConfigCategory.Other;
        private int _selectedConfigTypeIndex = 0;
        private string[] _availableConfigTypes;
        private Type[] _configTypeList;

        private GUIStyle _configNameStyle;
        private GUIStyle _configNameErrorStyle;
        private bool _stylesInitialized;

        public enum ConfigCategory
        {
            All,
            Player,
            Enemy,
            Weapon,
            Level,
            Input,
            Audio,
            UI,
            Game,
            Other
        }

        public class ConfigAsset
        {
            public ScriptableObject Asset;
            public string Name;
            public string Path;
            public ConfigCategory Category;
            public Type AssetType;
            public bool HasValidateMethod;

            /// <summary>
            /// Identity comes from the underlying asset, not the wrapper. Every refresh
            /// builds brand-new wrappers, so reference identity would silently orphan every
            /// entry in the selection set and the validation result map.
            /// </summary>
            public override bool Equals(object obj)
            {
                if (!(obj is ConfigAsset other)) return false;

                // A wrapper around a destroyed or missing asset has no stable identity, so
                // fall back to reference equality for it.
                if (ReferenceEquals(Asset, null) || ReferenceEquals(other.Asset, null))
                    return ReferenceEquals(this, other);

                return Asset.GetHashCode() == other.Asset.GetHashCode();
            }

            public override int GetHashCode()
            {
                return ReferenceEquals(Asset, null) ? 0 : Asset.GetHashCode();
            }
        }

        public class ValidationResult
        {
            public bool IsValid;
            public string ErrorMessage;
            public DateTime ValidatedAt;
        }

        public static void ShowWindow()
        {
            var window = GetWindow<StradaConfigDataManagerWindow>("Config Data Manager");
            window.minSize = new Vector2(900, 600);
            window.Show();
        }

        private void OnEnable()
        {
            _needsRefresh = true;
            InitializeCategoryFoldouts();
            CacheConfigTypes();
        }

        private void InitializeCategoryFoldouts()
        {
            foreach (ConfigCategory category in Enum.GetValues(typeof(ConfigCategory)))
            {
                if (category != ConfigCategory.All && !_categoryFoldouts.ContainsKey(category))
                {
                    _categoryFoldouts[category] = true;
                }
            }
        }

        private void CacheConfigTypes()
        {
            var configDataType = typeof(ConfigData);
            var types = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && configDataType.IsAssignableFrom(type))
                        {
                            types.Add(type);
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (var type in ex.Types)
                    {
                        if (type == null)
                            continue;

                        if (type.IsClass && !type.IsAbstract && configDataType.IsAssignableFrom(type))
                        {
                            types.Add(type);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            _configTypeList = types.ToArray();
            _availableConfigTypes = new string[types.Count];
            for (int i = 0; i < types.Count; i++)
            {
                _availableConfigTypes[i] = types[i].Name;
            }
        }


        private void InitializeStyles()
        {
            if (_stylesInitialized) return;
            _configNameStyle = new GUIStyle(EditorStyles.linkLabel);
            _configNameErrorStyle = new GUIStyle(EditorStyles.linkLabel)
            {
                normal = { textColor = Color.red }
            };
            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();

            if (_needsRefresh)
            {
                RefreshConfigList();
                _needsRefresh = false;
            }

            DrawToolbar();
            DrawConfigStats();
            EditorGUILayout.Space(5);

            if (_showCreationWizard)
            {
                DrawCreationWizard();
            }
            else
            {
                DrawBulkOperationsBar();
                DrawConfigList();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Name:", GUILayout.Width(40));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            GUILayout.Space(5);

            GUILayout.Label("Type:", GUILayout.Width(35));
            _typeFilter = EditorGUILayout.TextField(_typeFilter, EditorStyles.toolbarSearchField, GUILayout.Width(120));

            GUILayout.Space(10);

            GUILayout.Label("Category:", GUILayout.Width(60));
            _selectedCategory = (ConfigCategory)EditorGUILayout.EnumPopup(_selectedCategory, EditorStyles.toolbarPopup, GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            _showValidationErrors = GUILayout.Toggle(_showValidationErrors, "Show Errors Only", EditorStyles.toolbarButton, GUILayout.Width(100));

            GUILayout.Space(5);

            if (GUILayout.Button(_showCreationWizard ? "Cancel" : "Create New", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                _showCreationWizard = !_showCreationWizard;
                if (_showCreationWizard)
                {
                    _newConfigName = "";
                    _newConfigCategory = ConfigCategory.Other;
                }
            }

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                _needsRefresh = true;
                _validationResults.Clear();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawConfigStats()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            var totalConfigs = _cachedConfigs?.Count ?? 0;
            var filteredCount = GetFilteredConfigs().Count;
            var selectedCount = _selectedConfigs.Count;
            int errorCount = 0;
            foreach (var kvp in _validationResults)
            {
                if (!kvp.Value.IsValid)
                    errorCount++;
            }

            GUILayout.Label($"Total: {totalConfigs}", EditorStyles.boldLabel);
            GUILayout.Space(20);
            GUILayout.Label($"Showing: {filteredCount}", EditorStyles.boldLabel);
            GUILayout.Space(20);
            GUILayout.Label($"Selected: {selectedCount}", EditorStyles.boldLabel);

            if (errorCount > 0)
            {
                GUILayout.Space(20);
                var prevColor = GUI.color;
                GUI.color = Color.red;
                GUILayout.Label($"Errors: {errorCount}", EditorStyles.boldLabel);
                GUI.color = prevColor;
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawBulkOperationsBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            var newSelectAll = EditorGUILayout.Toggle(_selectAll, GUILayout.Width(20));
            if (newSelectAll != _selectAll)
            {
                _selectAll = newSelectAll;
                if (_selectAll)
                {
                    foreach (var config in GetFilteredConfigs())
                    {
                        _selectedConfigs.Add(config);
                    }
                }
                else
                {
                    _selectedConfigs.Clear();
                }
            }
            GUILayout.Label("Select All", GUILayout.Width(70));

            GUILayout.Space(20);

            GUI.enabled = _selectedConfigs.Count > 0;

            if (GUILayout.Button($"Validate Selected ({_selectedConfigs.Count})", GUILayout.Width(150)))
            {
                ValidateSelectedConfigs();
            }

            if (GUILayout.Button("Clear Selection", GUILayout.Width(100)))
            {
                _selectedConfigs.Clear();
                _selectAll = false;
            }

            GUILayout.Space(10);

            if (GUILayout.Button("Validate All", GUILayout.Width(100)))
            {
                ValidateAllConfigs();
            }

            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawCreationWizard()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            EditorGUILayout.LabelField("Create New Config", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Name:", GUILayout.Width(80));
            _newConfigName = EditorGUILayout.TextField(_newConfigName);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_newConfigName))
            {
                var finalName = _newConfigName.StartsWith("CD_") ? _newConfigName : $"CD_{_newConfigName}";
                EditorGUILayout.HelpBox($"Asset will be created as: {finalName}", MessageType.Info);
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Category:", GUILayout.Width(80));
            _newConfigCategory = (ConfigCategory)EditorGUILayout.EnumPopup(_newConfigCategory);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            if (_availableConfigTypes != null && _availableConfigTypes.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Type:", GUILayout.Width(80));
                _selectedConfigTypeIndex = EditorGUILayout.Popup(_selectedConfigTypeIndex, _availableConfigTypes);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("No ConfigData types found. Create a class inheriting from ConfigData first.", MessageType.Warning);
            }

            EditorGUILayout.Space(15);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            GUI.enabled = !string.IsNullOrEmpty(_newConfigName) && _configTypeList != null && _configTypeList.Length > 0;
            if (GUILayout.Button("Create Config", GUILayout.Width(120), GUILayout.Height(30)))
            {
                CreateNewConfig();
            }
            GUI.enabled = true;

            if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(30)))
            {
                _showCreationWizard = false;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void CreateNewConfig()
        {
            if (_configTypeList == null || _selectedConfigTypeIndex >= _configTypeList.Length)
                return;

            var configType = _configTypeList[_selectedConfigTypeIndex];
            var finalName = _newConfigName.StartsWith("CD_") ? _newConfigName : $"CD_{_newConfigName}";

            var path = EditorUtility.SaveFilePanelInProject(
                "Save Config",
                finalName,
                "asset",
                "Choose location for the new config asset");

            if (string.IsNullOrEmpty(path))
                return;

            var instance = ScriptableObject.CreateInstance(configType);
            instance.name = finalName;

            AssetDatabase.CreateAsset(instance, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = instance;
            EditorGUIUtility.PingObject(instance);

            _showCreationWizard = false;
            _needsRefresh = true;

            Debug.Log($"Created config: {finalName} at {path}");
        }

        private void DrawConfigList()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            var filteredConfigs = GetFilteredConfigs();

            if (filteredConfigs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    _cachedConfigs?.Count > 0
                        ? "No configs match the current filter."
                        : "No CD_ configs found.\n\nCreate configs using the 'Create New' button above or Assets > Create > Strada menu.",
                    MessageType.Info);
            }
            else
            {
                for (int i = 0; i < _sortedCategories.Count; i++)
                {
                    var category = _sortedCategories[i];
                    DrawCategoryGroup(category, _groupedConfigs[category]);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCategoryGroup(ConfigCategory category, List<ConfigAsset> configs)
        {
            if (!_categoryFoldouts.ContainsKey(category))
            {
                _categoryFoldouts[category] = true;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            
            _categoryFoldouts[category] = EditorGUILayout.Foldout(_categoryFoldouts[category], "", true);
            
            var categoryIcon = GetCategoryIcon(category);
            GUILayout.Label(categoryIcon, GUILayout.Width(20), GUILayout.Height(20));
            
            EditorGUILayout.LabelField($"{category} ({configs.Count})", EditorStyles.boldLabel);
            
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Select All", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                foreach (var config in configs)
                {
                    _selectedConfigs.Add(config);
                }
            }

            if (GUILayout.Button("Validate", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                foreach (var config in configs)
                {
                    ValidateConfig(config);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (_categoryFoldouts[category])
            {
                EditorGUI.indentLevel++;
                // Already sorted by EnsureFilteredConfigs.
                for (int i = 0; i < configs.Count; i++)
                {
                    DrawConfigItem(configs[i]);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private GUIContent GetCategoryIcon(ConfigCategory category)
        {
            string iconName = category switch
            {
                ConfigCategory.Player => "d_UnityEditor.AnimationWindow",
                ConfigCategory.Enemy => "d_UnityEditor.SceneHierarchyWindow",
                ConfigCategory.Weapon => "d_Toolbar Minus",
                ConfigCategory.Level => "d_SceneAsset Icon",
                ConfigCategory.Input => "d_UnityEditor.GameView",
                ConfigCategory.Audio => "d_AudioSource Icon",
                ConfigCategory.UI => "d_RectTransform Icon",
                ConfigCategory.Game => "d_UnityEditor.ConsoleWindow",
                _ => "d_ScriptableObject Icon"
            };
            
            return EditorGUIUtility.IconContent(iconName);
        }

        private void DrawConfigItem(ConfigAsset config)
        {
            var hasError = _validationResults.TryGetValue(config, out var result) && !result.IsValid;

            if (_showValidationErrors && !hasError)
                return;

            var isSelected = _selectedConfigs.Contains(config);

            var bgColor = GUI.backgroundColor;
            if (hasError)
            {
                GUI.backgroundColor = new Color(1f, 0.5f, 0.5f, 0.3f);
            }
            else if (isSelected)
            {
                GUI.backgroundColor = new Color(0.5f, 0.7f, 1f, 0.3f);
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUI.backgroundColor = bgColor;

            var newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(20));
            if (newSelected != isSelected)
            {
                if (newSelected)
                    _selectedConfigs.Add(config);
                else
                    _selectedConfigs.Remove(config);
            }

            if (hasError)
            {
                var errorIcon = EditorGUIUtility.IconContent("console.erroricon.sml");
                errorIcon.tooltip = result.ErrorMessage;
                GUILayout.Label(errorIcon, GUILayout.Width(20), GUILayout.Height(20));
            }
            else if (_validationResults.ContainsKey(config))
            {
                var validIcon = EditorGUIUtility.IconContent("d_greenLight");
                validIcon.tooltip = "Validation passed";
                GUILayout.Label(validIcon, GUILayout.Width(20), GUILayout.Height(20));
            }
            else
            {
                var icon = AssetPreview.GetMiniTypeThumbnail(config.AssetType ?? typeof(ScriptableObject));
                GUILayout.Label(icon, GUILayout.Width(20), GUILayout.Height(20));
            }

            if (GUILayout.Button(config.Name, hasError ? _configNameErrorStyle : _configNameStyle))
            {
                Selection.activeObject = config.Asset;
                EditorGUIUtility.PingObject(config.Asset);
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label(config.AssetType?.Name ?? "Unknown", EditorStyles.miniLabel, GUILayout.Width(150));

            GUILayout.Label(TruncatePath(config.Path, 40), EditorStyles.miniLabel, GUILayout.Width(250));

            if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                Selection.activeObject = config.Asset;
            }

            GUI.enabled = config.HasValidateMethod;
            if (GUILayout.Button("Validate", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                ValidateConfig(config);
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            if (hasError && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(result.ErrorMessage, MessageType.Error);
                EditorGUI.indentLevel--;
            }
        }

        private string TruncatePath(string path, int maxLength)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
                return path;
            
            return "..." + path.Substring(path.Length - maxLength + 3);
        }

        /// <summary>
        /// Refreshes the cached config list by scanning for all CD_ prefixed ScriptableObjects.
        /// </summary>
        public void RefreshConfigList()
        {
            _cachedConfigs = DiscoverConfigs();
            _configsVersion++;

            PruneStaleState();
        }

        /// <summary>
        /// Drops selection and validation entries whose asset is no longer part of the config
        /// set. ConfigAsset compares by underlying asset, so entries for assets that still
        /// exist survive the refresh; only genuinely gone ones are removed.
        /// </summary>
        private void PruneStaleState()
        {
            if (_selectedConfigs.Count == 0 && _validationResults.Count == 0) return;

            var live = new HashSet<ConfigAsset>(_cachedConfigs);

            _selectedConfigs.RemoveWhere(c => !live.Contains(c));

            if (_validationResults.Count > 0)
            {
                var stale = new List<ConfigAsset>();
                foreach (var kvp in _validationResults)
                {
                    if (!live.Contains(kvp.Key))
                        stale.Add(kvp.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    _validationResults.Remove(stale[i]);
                }
            }

            if (_selectedConfigs.Count == 0)
                _selectAll = false;
        }

        /// <summary>
        /// Discovers all CD_ prefixed ScriptableObjects in the project.
        /// </summary>
        /// <returns>List of discovered config assets</returns>
        public static List<ConfigAsset> DiscoverConfigs()
        {
            var configs = new List<ConfigAsset>();

            var guids = AssetDatabase.FindAssets("t:ScriptableObject");
            
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

                if (asset != null && asset.name.StartsWith("CD_"))
                {
                    var assetType = asset.GetType();
                    var hasValidate = assetType.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance) != null;

                    configs.Add(new ConfigAsset
                    {
                        Asset = asset,
                        Name = asset.name,
                        Path = path,
                        Category = DetermineCategory(asset.name, assetType),
                        AssetType = assetType,
                        HasValidateMethod = hasValidate
                    });
                }
            }

            return configs;
        }

        /// <summary>
        /// Gets the filtered list of configs based on current search and category filters.
        /// </summary>
        /// <remarks>
        /// The returned list is cached and reused until the filters or the config set change;
        /// callers must treat it as read-only.
        /// </remarks>
        public List<ConfigAsset> GetFilteredConfigs()
        {
            EnsureFilteredConfigs();
            return _filteredConfigs;
        }

        /// <summary>
        /// Rebuilds the filtered list and its per-category grouping only when something they
        /// depend on has changed.
        /// </summary>
        private void EnsureFilteredConfigs()
        {
            if (_filteredConfigs != null &&
                _filterVersion == _configsVersion &&
                _filterCategorySnapshot == _selectedCategory &&
                string.Equals(_filterNameSnapshot, _searchFilter, StringComparison.Ordinal) &&
                string.Equals(_filterTypeSnapshot, _typeFilter, StringComparison.Ordinal))
            {
                return;
            }

            _filteredConfigs = FilterConfigs(_cachedConfigs, _searchFilter, _typeFilter, _selectedCategory);
            _filterVersion = _configsVersion;
            _filterCategorySnapshot = _selectedCategory;
            _filterNameSnapshot = _searchFilter;
            _filterTypeSnapshot = _typeFilter;

            _groupedConfigs.Clear();
            for (int i = 0; i < _filteredConfigs.Count; i++)
            {
                var config = _filteredConfigs[i];
                if (!_groupedConfigs.TryGetValue(config.Category, out var list))
                {
                    list = new List<ConfigAsset>();
                    _groupedConfigs[config.Category] = list;
                }
                list.Add(config);
            }

            // Sorting here rather than in DrawCategoryGroup means the per-category copy and
            // sort happen once per filter change instead of once per repaint.
            foreach (var kvp in _groupedConfigs)
            {
                kvp.Value.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            }

            _sortedCategories.Clear();
            _sortedCategories.AddRange(_groupedConfigs.Keys);
            _sortedCategories.Sort((a, b) => ((int)a).CompareTo((int)b));
        }

        /// <summary>
        /// Filters configs by name, type, and category.
        /// </summary>
        public static List<ConfigAsset> FilterConfigs(
            List<ConfigAsset> configs,
            string nameFilter,
            string typeFilter,
            ConfigCategory categoryFilter)
        {
            if (configs == null)
                return new List<ConfigAsset>();

            var result = new List<ConfigAsset>();

            for (int i = 0; i < configs.Count; i++)
            {
                var c = configs[i];

                if (categoryFilter != ConfigCategory.All && c.Category != categoryFilter)
                    continue;

                if (!string.IsNullOrEmpty(nameFilter) &&
                    c.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!string.IsNullOrEmpty(typeFilter) &&
                    (c.AssetType == null ||
                     c.AssetType.Name.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                result.Add(c);
            }

            return result;
        }

        /// <summary>
        /// Determines the category of a config based on its name and type.
        /// </summary>
        public static ConfigCategory DetermineCategory(string name, Type assetType = null)
        {
            var lowerName = name.ToLower();
            var typeName = assetType?.Name.ToLower() ?? "";

            var category = MatchCategory(lowerName);
            if (category != ConfigCategory.Other)
                return category;

            return MatchCategory(typeName);
        }

        private static ConfigCategory MatchCategory(string text)
        {
            if (string.IsNullOrEmpty(text)) return ConfigCategory.Other;

            if (text.Contains("player")) return ConfigCategory.Player;
            if (text.Contains("enemy") || text.Contains("ai")) return ConfigCategory.Enemy;
            if (text.Contains("weapon") || text.Contains("gun") || text.Contains("sword")) return ConfigCategory.Weapon;
            if (text.Contains("level") || text.Contains("stage") || text.Contains("map")) return ConfigCategory.Level;
            if (text.Contains("input") || text.Contains("control")) return ConfigCategory.Input;
            if (text.Contains("audio") || text.Contains("sound") || text.Contains("music")) return ConfigCategory.Audio;
            if (text.Contains("ui") || text.Contains("menu") || text.Contains("hud")) return ConfigCategory.UI;
            if (text.Contains("game") || text.Contains("settings") || text.Contains("config")) return ConfigCategory.Game;

            return ConfigCategory.Other;
        }

        /// <summary>
        /// Validates a single config and stores the result.
        /// </summary>
        public ValidationResult ValidateConfig(ConfigAsset config)
        {
            var result = new ValidationResult
            {
                IsValid = true,
                ErrorMessage = null,
                ValidatedAt = DateTime.Now
            };

            if (config.Asset == null)
            {
                result.IsValid = false;
                result.ErrorMessage = "Asset is null or has been destroyed";
                _validationResults[config] = result;
                return result;
            }

            var validateMethod = config.Asset.GetType().GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance);
            if (validateMethod != null)
            {
                try
                {
                    var returnType = validateMethod.ReturnType;
                    
                    if (returnType == typeof(bool))
                    {
                        result.IsValid = (bool)validateMethod.Invoke(config.Asset, null);
                        if (!result.IsValid)
                        {
                            result.ErrorMessage = "Validation failed";
                        }
                    }
                    else if (returnType == typeof(string))
                    {
                        var errorMsg = (string)validateMethod.Invoke(config.Asset, null);
                        result.IsValid = string.IsNullOrEmpty(errorMsg);
                        result.ErrorMessage = errorMsg;
                    }
                    else
                    {
                        validateMethod.Invoke(config.Asset, null);
                        result.IsValid = true;
                    }

                    EditorUtility.SetDirty(config.Asset);
                }
                catch (Exception ex)
                {
                    result.IsValid = false;
                    result.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
                }
            }

            _validationResults[config] = result;
            return result;
        }

        /// <summary>
        /// Validates all selected configs.
        /// </summary>
        public void ValidateSelectedConfigs()
        {
            var configs = new List<ConfigAsset>(_selectedConfigs);
            ValidateConfigs(configs);
        }

        /// <summary>
        /// Validates all discovered configs.
        /// </summary>
        public void ValidateAllConfigs()
        {
            if (_cachedConfigs == null)
                return;

            ValidateConfigs(_cachedConfigs);
        }

        /// <summary>
        /// Validates a list of configs and displays progress.
        /// </summary>
        public void ValidateConfigs(List<ConfigAsset> configs)
        {
            var total = configs.Count;
            var current = 0;
            var errors = 0;

            try
            {
                foreach (var config in configs)
                {
                    current++;
                    EditorUtility.DisplayProgressBar(
                        "Validating Configs",
                        $"Validating {config.Name} ({current}/{total})",
                        (float)current / total);

                    var result = ValidateConfig(config);
                    if (!result.IsValid)
                    {
                        errors++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (errors > 0)
            {
                Debug.LogWarning($"Validation complete: {errors} error(s) found in {total} configs");
                _showValidationErrors = true;
            }
            else
            {
                Debug.Log($"Validation complete: All {total} configs passed");
            }

            Repaint();
        }

        /// <summary>
        /// Gets the cached configs list.
        /// </summary>
        public List<ConfigAsset> GetCachedConfigs()
        {
            return _cachedConfigs;
        }

        /// <summary>
        /// Gets the validation results dictionary.
        /// </summary>
        public Dictionary<ConfigAsset, ValidationResult> GetValidationResults()
        {
            return _validationResults;
        }
    }
}

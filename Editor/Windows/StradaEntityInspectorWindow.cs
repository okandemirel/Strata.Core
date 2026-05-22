using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Storage;
using Strada.Core.ECS.World;
using Strada.Core.Editor.DataProviders;
using Strada.Core.Editor.DataProviders.Models;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Enhanced entity inspector window with split view, real-time updates,
    /// component editing, and search functionality.
    /// Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6
    /// </summary>
    // FRAMEWORK DESIGN: The Entity Inspector reads runtime internals (entity versions,
    // sparse-set dense arrays, component storages) via reflection. This is editor-only
    // tooling — it never ships in player builds, so there is no perf or surface-area
    // concern. The reflection is also why the inspector is the canonical place to debug
    // ECS state without forcing every ECS type to expose a public debug API. If a piece
    // of state becomes inspectable from production code, it should get a proper accessor
    // and the inspector should switch to that — but reflection remains the right tool
    // for editor-internal probes that don't justify a public contract.
    public class StradaEntityInspectorWindow : EditorWindow
    {
        /// <summary>
        /// Search mode for entity filtering.
        /// </summary>
        public enum EntitySearchMode
        {
            All,
            ById,
            ByComponentType,
            ByFieldValue
        }

        private const float MinEntityListWidth = 200f;
        private const float MaxEntityListWidth = 400f;
        private const float DefaultEntityListWidth = 280f;
        private float _entityListWidth = DefaultEntityListWidth;
        private bool _isResizing;

        private Vector2 _entityListScrollPosition;
        private Vector2 _componentScrollPosition;

        private int _selectedEntityId = -1;
        private HashSet<int> _expandedComponents = new HashSet<int>();

        private string _searchQuery = "";
        private EntitySearchMode _searchMode = EntitySearchMode.All;
        private List<int> _filteredEntityIds = new List<int>();
        private List<int> _allEntityIds = new List<int>();

        private enum ViewMode { Inspector, Memory }
        private ViewMode _viewMode = ViewMode.Inspector;
        private Vector2 _memoryScrollPosition;

        private bool _autoRefresh = true;
        private float _refreshInterval = 0.5f;
        private double _lastRefreshTime;

        private List<Type> _availableComponentTypes = new List<Type>();
        private string _addComponentSearch = "";
        private bool _showAddComponentDropdown;
        private Vector2 _addComponentScrollPosition;

        private GUIStyle _headerStyle;
        private GUIStyle _entityItemStyle;
        private GUIStyle _selectedEntityStyle;
        private GUIStyle _componentHeaderStyle;
        private GUIStyle _fieldLabelStyle;
        private bool _stylesInitialized;

        private static readonly string[] ViewModeLabels = { "Inspector", "Memory" };

        private WorldDataProvider _worldDataProvider;

        // Bookmarking
        private HashSet<int> _bookmarkedEntities = new HashSet<int>();

        // Component copy/paste clipboard
        private static Type _clipboardComponentType;
        private static object _clipboardComponentValue;

        // Archetype grouping
        private bool _archetypeGroupingEnabled;
        private Dictionary<string, bool> _archetypeFoldouts = new Dictionary<string, bool>();

        public static void ShowWindow()
        {
            var window = GetWindow<StradaEntityInspectorWindow>("Entity Inspector");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }


        private void OnEnable()
        {
            _worldDataProvider = WorldDataProvider.Instance;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            CacheComponentTypes();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                RefreshEntityList();
                CacheComponentTypes();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _allEntityIds.Clear();
                _filteredEntityIds.Clear();
                _selectedEntityId = -1;
                _expandedComponents.Clear();
                _bookmarkedEntities.Clear();
                _archetypeFoldouts.Clear();
            }
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (!Application.isPlaying) return;

            if (_autoRefresh && EditorApplication.timeSinceStartup - _lastRefreshTime > _refreshInterval)
            {
                RefreshEntityList();
                DetectDestroyedEntities();
                _lastRefreshTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin = new RectOffset(5, 5, 8, 4)
            };

            _entityItemStyle = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(8, 4, 4, 4),
                margin = new RectOffset(2, 2, 1, 1)
            };

            _selectedEntityStyle = new GUIStyle(_entityItemStyle)
            {
                normal = { background = CreateColorTexture(new Color(0.24f, 0.49f, 0.91f, 0.4f)) }
            };

            _componentHeaderStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(0, 0, 4, 2)
            };

            _fieldLabelStyle = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(16, 4, 2, 2)
            };

            _stylesInitialized = true;
        }

        private Texture2D CreateColorTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void OnGUI()
        {
            InitializeStyles();

            if (!Application.isPlaying)
            {
                DrawNotPlayingMessage();
                return;
            }

            if (!_worldDataProvider.IsAvailable)
            {
                DrawNoWorldMessage();
                return;
            }

            DrawToolbar();

            if (_viewMode == ViewMode.Inspector)
            {
                DrawSplitView();
            }
            else
            {
                DrawMemoryView();
            }
        }

        private void DrawNotPlayingMessage()
        {
            DrawCenteredHelpBox(
                "Entity Inspector is only available in Play Mode.\n\nEnter Play Mode to inspect ECS entities.",
                MessageType.Info);
        }

        private void DrawNoWorldMessage()
        {
            DrawCenteredHelpBox(
                "No active ECS World found.\n\nCreate a World using World.Create() to begin.",
                MessageType.Warning);
        }

        private void DrawCenteredHelpBox(string message, MessageType type)
        {
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox(message, type, true);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }


        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Search:", GUILayout.Width(45));
            var newSearch = EditorGUILayout.TextField(_searchQuery, EditorStyles.toolbarSearchField, GUILayout.Width(150));
            if (newSearch != _searchQuery)
            {
                _searchQuery = newSearch;
                ApplySearchFilter();
            }

            var newMode = (EntitySearchMode)EditorGUILayout.EnumPopup(_searchMode, EditorStyles.toolbarDropDown, GUILayout.Width(100));
            if (newMode != _searchMode)
            {
                _searchMode = newMode;
                ApplySearchFilter();
            }

            GUILayout.Space(10);

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto Refresh", EditorStyles.toolbarButton, GUILayout.Width(85));

            if (_autoRefresh)
            {
                GUILayout.Label("Interval:", GUILayout.Width(50));
                _refreshInterval = EditorGUILayout.Slider(_refreshInterval, 0.1f, 2.0f, GUILayout.Width(80));
            }

            GUILayout.Space(5);

            _archetypeGroupingEnabled = GUILayout.Toggle(_archetypeGroupingEnabled, "Group by Archetype", EditorStyles.toolbarButton, GUILayout.Width(115));

            GUILayout.FlexibleSpace();

            GUILayout.Label($"Entities: {_allEntityIds.Count}", EditorStyles.toolbarButton);
            if (_filteredEntityIds.Count != _allEntityIds.Count)
            {
                GUILayout.Label($"Filtered: {_filteredEntityIds.Count}", EditorStyles.toolbarButton);
            }

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(55)))
            {
                RefreshEntityList();
            }

            GUILayout.Space(10);
            _viewMode = (ViewMode)GUILayout.Toolbar((int)_viewMode, ViewModeLabels, EditorStyles.toolbarButton, GUILayout.Width(150));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSplitView()
        {
            EditorGUILayout.BeginHorizontal();

            DrawEntityListPanel();

            DrawResizeHandle();

            DrawComponentDetailsPanel();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntityListPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(_entityListWidth));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Entities", _headerStyle);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
            {
                CreateEntity();
            }

            EditorGUI.BeginDisabledGroup(_selectedEntityId < 0);
            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(22)))
            {
                DestroyEntity(_selectedEntityId);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            _entityListScrollPosition = EditorGUILayout.BeginScrollView(_entityListScrollPosition);

            if (_filteredEntityIds.Count == 0)
            {
                if (_allEntityIds.Count == 0)
                {
                    EditorGUILayout.HelpBox("No entities in the world.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("No entities match the search criteria.", MessageType.Info);
                }
            }
            else
            {
                // Always draw bookmarked entities at the top
                var bookmarkedInList = _filteredEntityIds.Where(id => _bookmarkedEntities.Contains(id)).ToList();
                var nonBookmarkedInList = _filteredEntityIds.Where(id => !_bookmarkedEntities.Contains(id)).ToList();

                if (bookmarkedInList.Count > 0)
                {
                    EditorGUILayout.LabelField("Bookmarked", EditorStyles.miniBoldLabel);
                    foreach (var entityId in bookmarkedInList)
                    {
                        DrawEntityListItem(entityId);
                    }

                    if (nonBookmarkedInList.Count > 0)
                    {
                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    }
                }

                if (_archetypeGroupingEnabled)
                {
                    DrawArchetypeGroupedEntities(nonBookmarkedInList);
                }
                else
                {
                    foreach (var entityId in nonBookmarkedInList)
                    {
                        DrawEntityListItem(entityId);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawArchetypeGroupedEntities(List<int> entityIds)
        {
            var archetypeGroups = new Dictionary<string, List<int>>();

            foreach (var entityId in entityIds)
            {
                var archetypeKey = GetArchetypeKey(entityId);
                if (!archetypeGroups.ContainsKey(archetypeKey))
                {
                    archetypeGroups[archetypeKey] = new List<int>();
                }
                archetypeGroups[archetypeKey].Add(entityId);
            }

            foreach (var kvp in archetypeGroups.OrderBy(g => g.Key))
            {
                var archetypeKey = kvp.Key;
                var entities = kvp.Value;

                if (!_archetypeFoldouts.ContainsKey(archetypeKey))
                {
                    _archetypeFoldouts[archetypeKey] = true;
                }

                var displayName = string.IsNullOrEmpty(archetypeKey) ? "(No Components)" : archetypeKey;
                var foldoutLabel = $"{displayName} ({entities.Count})";
                _archetypeFoldouts[archetypeKey] = EditorGUILayout.Foldout(_archetypeFoldouts[archetypeKey], foldoutLabel, true);

                if (_archetypeFoldouts[archetypeKey])
                {
                    EditorGUI.indentLevel++;
                    foreach (var entityId in entities)
                    {
                        DrawEntityListItem(entityId);
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }

        private string GetArchetypeKey(int entityId)
        {
            try
            {
                var components = _worldDataProvider.GetEntityComponents(entityId);
                var typeNames = components.Select(c => c.ComponentType.Name).OrderBy(n => n);
                return string.Join(", ", typeNames);
            }
            catch
            {
                return "";
            }
        }

        private void DrawEntityListItem(int entityId)
        {
            var isSelected = entityId == _selectedEntityId;
            var style = isSelected ? _selectedEntityStyle : _entityItemStyle;
            var isBookmarked = _bookmarkedEntities.Contains(entityId);

            var rect = EditorGUILayout.BeginHorizontal(style);

            // Handle right-click context menu on entity
            if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
            {
                ShowEntityContextMenu(entityId);
                Event.current.Use();
            }

            // Bookmark toggle (star icon)
            var starLabel = isBookmarked ? "\u2605" : "\u2606";
            if (GUILayout.Button(starLabel, EditorStyles.miniLabel, GUILayout.Width(18)))
            {
                if (isBookmarked)
                    _bookmarkedEntities.Remove(entityId);
                else
                    _bookmarkedEntities.Add(entityId);
            }

            var componentCount = GetEntityComponentCount(entityId);
            var buttonContent = new GUIContent($"Entity [{entityId}]", $"Entity ID: {entityId}\nComponents: {componentCount}");

            if (GUILayout.Button(buttonContent, EditorStyles.label, GUILayout.ExpandWidth(true)))
            {
                _selectedEntityId = entityId;
            }

            GUILayout.Label($"[{componentCount}]", GUILayout.Width(35));

            EditorGUILayout.EndHorizontal();
        }

        private void ShowEntityContextMenu(int entityId)
        {
            var menu = new GenericMenu();
            var isBookmarked = _bookmarkedEntities.Contains(entityId);

            menu.AddItem(new GUIContent("Copy Entity ID"), false, () =>
            {
                EditorGUIUtility.systemCopyBuffer = entityId.ToString();
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent(isBookmarked ? "Remove Bookmark" : "Bookmark"), false, () =>
            {
                if (isBookmarked)
                    _bookmarkedEntities.Remove(entityId);
                else
                    _bookmarkedEntities.Add(entityId);
                Repaint();
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Destroy Entity"), false, () =>
            {
                DestroyEntity(entityId);
            });

            menu.ShowAsContext();
        }

        private void DrawResizeHandle()
        {
            var resizeRect = GUILayoutUtility.GetRect(4f, 4f, GUILayout.ExpandHeight(true));
            EditorGUIUtility.AddCursorRect(resizeRect, MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.MouseDown && resizeRect.Contains(Event.current.mousePosition))
            {
                _isResizing = true;
                Event.current.Use();
            }

            if (_isResizing)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    _entityListWidth = Mathf.Clamp(Event.current.mousePosition.x, MinEntityListWidth, MaxEntityListWidth);
                    Repaint();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    _isResizing = false;
                }
            }

            EditorGUI.DrawRect(resizeRect, new Color(0.15f, 0.15f, 0.15f, 1f));
        }


        private void DrawComponentDetailsPanel()
        {
            EditorGUILayout.BeginVertical();

            EditorGUILayout.LabelField("Component Details", _headerStyle);

            if (_selectedEntityId < 0)
            {
                EditorGUILayout.HelpBox("Select an entity from the list to view and edit its components.", MessageType.Info);
            }
            else if (!EntityExists(_selectedEntityId))
            {
                EditorGUILayout.HelpBox("Selected entity has been destroyed.", MessageType.Warning);
                _selectedEntityId = -1;
            }
            else
            {
                DrawEntityHeader();
                DrawAddComponentButton();

                _componentScrollPosition = EditorGUILayout.BeginScrollView(_componentScrollPosition);
                DrawEntityComponents();
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEntityHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Entity ID: {_selectedEntityId}", EditorStyles.boldLabel);
            var version = GetEntityVersion(_selectedEntityId);
            EditorGUILayout.LabelField($"Version: {version}", GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAddComponentButton()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("+ Add Component", GUILayout.Height(24)))
            {
                _showAddComponentDropdown = !_showAddComponentDropdown;
                _addComponentSearch = "";
            }

            EditorGUILayout.EndHorizontal();

            if (_showAddComponentDropdown)
            {
                DrawAddComponentDropdown();
            }
        }

        private void DrawAddComponentDropdown()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Search:", GUILayout.Width(50));
            _addComponentSearch = EditorGUILayout.TextField(_addComponentSearch);
            if (GUILayout.Button("\u00d7", GUILayout.Width(20)))
            {
                _showAddComponentDropdown = false;
            }
            EditorGUILayout.EndHorizontal();

            _addComponentScrollPosition = EditorGUILayout.BeginScrollView(_addComponentScrollPosition, GUILayout.MaxHeight(200));

            var filteredTypes = _availableComponentTypes
                .Where(t => string.IsNullOrEmpty(_addComponentSearch) ||
                           t.Name.IndexOf(_addComponentSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Name)
                .ToList();

            foreach (var componentType in filteredTypes)
            {
                var hasComponent = EntityHasComponent(_selectedEntityId, componentType);

                EditorGUI.BeginDisabledGroup(hasComponent);
                if (GUILayout.Button(componentType.Name, EditorStyles.miniButton))
                {
                    AddComponentToEntity(_selectedEntityId, componentType);
                    _showAddComponentDropdown = false;
                }
                EditorGUI.EndDisabledGroup();
            }

            if (filteredTypes.Count == 0)
            {
                EditorGUILayout.LabelField("No matching component types found.", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawEntityComponents()
        {
            var components = _worldDataProvider.GetEntityComponents(_selectedEntityId).ToList();

            if (components.Count == 0)
            {
                EditorGUILayout.HelpBox("This entity has no components.", MessageType.Info);
                return;
            }

            foreach (var component in components)
            {
                DrawComponentSection(component);
            }
        }

        private void DrawComponentSection(ComponentInfo component)
        {
            var componentHash = component.ComponentType.GetHashCode();
            var isExpanded = _expandedComponents.Contains(componentHash);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var headerRect = EditorGUILayout.BeginHorizontal();

            // Handle right-click context menu on component header
            if (Event.current.type == EventType.ContextClick && headerRect.Contains(Event.current.mousePosition))
            {
                ShowComponentContextMenu(component);
                Event.current.Use();
            }

            var newExpanded = EditorGUILayout.Foldout(isExpanded, component.ComponentType.Name, true, _componentHeaderStyle);
            if (newExpanded != isExpanded)
            {
                if (newExpanded)
                    _expandedComponents.Add(componentHash);
                else
                    _expandedComponents.Remove(componentHash);
            }

            GUILayout.FlexibleSpace();

            // Copy button
            if (GUILayout.Button("Copy", EditorStyles.miniButtonLeft, GUILayout.Width(38)))
            {
                CopyComponent(component);
            }

            // Paste button
            EditorGUI.BeginDisabledGroup(_clipboardComponentType == null || _clipboardComponentType != component.ComponentType);
            if (GUILayout.Button("Paste", EditorStyles.miniButtonRight, GUILayout.Width(40)))
            {
                PasteComponent(component.ComponentType);
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(4);

            if (GUILayout.Button("\u00d7", GUILayout.Width(20), GUILayout.Height(18)))
            {
                RemoveComponentFromEntity(_selectedEntityId, component.ComponentType);
            }

            EditorGUILayout.EndHorizontal();

            if (newExpanded && component.Value != null)
            {
                EditorGUI.indentLevel++;
                DrawComponentFields(component);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void ShowComponentContextMenu(ComponentInfo component)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Copy Component"), false, () =>
            {
                CopyComponent(component);
            });

            if (_clipboardComponentType != null && _clipboardComponentType == component.ComponentType)
            {
                menu.AddItem(new GUIContent("Paste Component Values"), false, () =>
                {
                    PasteComponent(component.ComponentType);
                });
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Paste Component Values"));
            }

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Reset to Default"), false, () =>
            {
                ResetComponentToDefault(component.ComponentType);
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Remove Component"), false, () =>
            {
                RemoveComponentFromEntity(_selectedEntityId, component.ComponentType);
            });

            menu.ShowAsContext();
        }

        private void CopyComponent(ComponentInfo component)
        {
            _clipboardComponentType = component.ComponentType;
            _clipboardComponentValue = CopyValueDeep(component.Value, component.ComponentType);
        }

        private void PasteComponent(Type componentType)
        {
            if (_clipboardComponentType == null || _clipboardComponentType != componentType) return;
            if (_clipboardComponentValue == null) return;
            if (_selectedEntityId < 0) return;

            try
            {
                var pastedValue = CopyValueDeep(_clipboardComponentValue, componentType);
                _worldDataProvider.SetComponentBoxed(_selectedEntityId, componentType, pastedValue);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EntityInspector] Failed to paste component: {ex.Message}");
            }
        }

        private void ResetComponentToDefault(Type componentType)
        {
            if (_selectedEntityId < 0) return;

            try
            {
                var defaultValue = Activator.CreateInstance(componentType);
                _worldDataProvider.SetComponentBoxed(_selectedEntityId, componentType, defaultValue);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EntityInspector] Failed to reset component: {ex.Message}");
            }
        }

        private object CopyValueDeep(object source, Type type)
        {
            if (source == null) return null;

            // For value types, boxing already creates a copy, but we want to ensure
            // nested reference fields are also copied if any exist
            var copy = Activator.CreateInstance(type);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var fieldValue = field.GetValue(source);
                field.SetValue(copy, fieldValue);
            }
            return copy;
        }


        private void DrawComponentFields(ComponentInfo component)
        {
            var componentType = component.ComponentType;
            var value = component.Value;
            var modified = false;

            foreach (var field in componentType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var fieldValue = field.GetValue(value);
                var newValue = DrawField(field.Name, field.FieldType, fieldValue);

                if (!Equals(newValue, fieldValue))
                {
                    field.SetValue(value, newValue);
                    modified = true;
                }
            }

            if (modified)
            {
                _worldDataProvider.SetComponentBoxed(_selectedEntityId, componentType, value);
            }
        }

        private object DrawField(string name, Type fieldType, object value)
        {
            // Check if this is a nested struct (non-Unity, non-primitive value type with public fields)
            if (IsNestedStructType(fieldType))
            {
                return DrawNestedStructField(name, fieldType, value);
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(name, _fieldLabelStyle, GUILayout.Width(120));

            object result = value;

            try
            {
                if (fieldType == typeof(int))
                {
                    result = EditorGUILayout.IntField((int)(value ?? 0));
                }
                else if (fieldType == typeof(float))
                {
                    result = EditorGUILayout.FloatField((float)(value ?? 0f));
                }
                else if (fieldType == typeof(double))
                {
                    result = EditorGUILayout.DoubleField((double)(value ?? 0.0));
                }
                else if (fieldType == typeof(bool))
                {
                    result = EditorGUILayout.Toggle((bool)(value ?? false));
                }
                else if (fieldType == typeof(string))
                {
                    result = EditorGUILayout.TextField((string)(value ?? ""));
                }
                else if (fieldType == typeof(Vector2))
                {
                    result = EditorGUILayout.Vector2Field("", (Vector2)(value ?? Vector2.zero));
                }
                else if (fieldType == typeof(Vector3))
                {
                    result = EditorGUILayout.Vector3Field("", (Vector3)(value ?? Vector3.zero));
                }
                else if (fieldType == typeof(Vector4))
                {
                    result = EditorGUILayout.Vector4Field("", (Vector4)(value ?? Vector4.zero));
                }
                else if (fieldType == typeof(Vector2Int))
                {
                    result = EditorGUILayout.Vector2IntField("", (Vector2Int)(value ?? Vector2Int.zero));
                }
                else if (fieldType == typeof(Vector3Int))
                {
                    result = EditorGUILayout.Vector3IntField("", (Vector3Int)(value ?? Vector3Int.zero));
                }
                else if (fieldType == typeof(Quaternion))
                {
                    var q = (Quaternion)(value ?? Quaternion.identity);
                    var euler = EditorGUILayout.Vector3Field("", q.eulerAngles);
                    result = Quaternion.Euler(euler);
                }
                else if (fieldType == typeof(Color))
                {
                    result = EditorGUILayout.ColorField((Color)(value ?? Color.white));
                }
                else if (fieldType == typeof(Color32))
                {
                    var c32 = (Color32)(value ?? new Color32(255, 255, 255, 255));
                    result = (Color32)EditorGUILayout.ColorField(c32);
                }
                else if (fieldType == typeof(Rect))
                {
                    result = EditorGUILayout.RectField((Rect)(value ?? Rect.zero));
                }
                else if (fieldType == typeof(RectInt))
                {
                    result = EditorGUILayout.RectIntField((RectInt)(value ?? new RectInt()));
                }
                else if (fieldType == typeof(Bounds))
                {
                    result = EditorGUILayout.BoundsField((Bounds)(value ?? new Bounds()));
                }
                else if (fieldType == typeof(BoundsInt))
                {
                    result = EditorGUILayout.BoundsIntField((BoundsInt)(value ?? new BoundsInt()));
                }
                else if (fieldType.IsEnum)
                {
                    result = EditorGUILayout.EnumPopup((Enum)(value ?? Enum.GetValues(fieldType).GetValue(0)));
                }
                else if (fieldType == typeof(long))
                {
                    result = EditorGUILayout.LongField((long)(value ?? 0L));
                }
                else if (fieldType == typeof(byte))
                {
                    result = (byte)Mathf.Clamp(EditorGUILayout.IntField((byte)(value ?? 0)), 0, 255);
                }
                else if (fieldType == typeof(short))
                {
                    result = (short)Mathf.Clamp(EditorGUILayout.IntField((short)(value ?? 0)), short.MinValue, short.MaxValue);
                }
                else if (fieldType == typeof(uint))
                {
                    result = (uint)Mathf.Max(EditorGUILayout.LongField((uint)(value ?? 0u)), 0);
                }
                else if (fieldType == typeof(ulong))
                {
                    var ulongVal = (ulong)(value ?? 0ul);
                    var strVal = EditorGUILayout.TextField(ulongVal.ToString());
                    if (ulong.TryParse(strVal, out var parsed))
                        result = parsed;
                }
                else
                {
                    EditorGUILayout.LabelField(value?.ToString() ?? "null", EditorStyles.helpBox);
                }
            }
            catch
            {
                EditorGUILayout.LabelField(value?.ToString() ?? "null", EditorStyles.helpBox);
            }

            EditorGUILayout.EndHorizontal();
            return result;
        }

        private bool IsNestedStructType(Type type)
        {
            if (type == null || !type.IsValueType || type.IsPrimitive || type.IsEnum)
                return false;

            // Exclude known Unity types that have dedicated editors
            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                type == typeof(Vector2Int) || type == typeof(Vector3Int) ||
                type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Color32) ||
                type == typeof(Rect) || type == typeof(RectInt) ||
                type == typeof(Bounds) || type == typeof(BoundsInt))
                return false;

            // Exclude primitive-like numeric types
            if (type == typeof(decimal) || type == typeof(IntPtr) || type == typeof(UIntPtr))
                return false;

            // Must have at least one public instance field to qualify
            var publicFields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            return publicFields.Length > 0;
        }

        private object DrawNestedStructField(string name, Type fieldType, object value)
        {
            if (value == null)
            {
                value = Activator.CreateInstance(fieldType);
            }

            EditorGUILayout.LabelField(name, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            var modified = false;
            var boxedValue = value;

            foreach (var subField in fieldType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var subFieldValue = subField.GetValue(boxedValue);
                var newSubValue = DrawField(subField.Name, subField.FieldType, subFieldValue);

                if (!Equals(newSubValue, subFieldValue))
                {
                    subField.SetValue(boxedValue, newSubValue);
                    modified = true;
                }
            }

            EditorGUI.indentLevel--;

            return modified ? boxedValue : value;
        }

        private void CreateEntity()
        {
            try
            {
                var entityManager = World.Current?.EntityManager;
                if (entityManager == null)
                {
                    Debug.LogWarning("[EntityInspector] No EntityManager available to create entity.");
                    return;
                }

                var createMethod = typeof(EntityManager).GetMethod("CreateEntity",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                if (createMethod != null)
                {
                    var newEntityId = createMethod.Invoke(entityManager, null);
                    Debug.Log($"[EntityInspector] Created entity: {newEntityId}");
                    RefreshEntityList();

                    if (newEntityId is int id)
                    {
                        _selectedEntityId = id;
                    }
                }
                else
                {
                    Debug.LogWarning("[EntityInspector] CreateEntity method not found on EntityManager.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EntityInspector] Failed to create entity: {ex.Message}");
            }
        }

        private void DestroyEntity(int entityId)
        {
            try
            {
                var entityManager = World.Current?.EntityManager;
                if (entityManager == null)
                {
                    Debug.LogWarning("[EntityInspector] No EntityManager available to destroy entity.");
                    return;
                }

                var destroyMethod = typeof(EntityManager).GetMethod("DestroyEntity",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);

                if (destroyMethod != null)
                {
                    destroyMethod.Invoke(entityManager, new object[] { entityId });
                    Debug.Log($"[EntityInspector] Destroyed entity: {entityId}");

                    _bookmarkedEntities.Remove(entityId);

                    if (_selectedEntityId == entityId)
                    {
                        _selectedEntityId = -1;
                    }

                    RefreshEntityList();
                }
                else
                {
                    Debug.LogWarning("[EntityInspector] DestroyEntity(int) method not found on EntityManager.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EntityInspector] Failed to destroy entity: {ex.Message}");
            }
        }

        private void RefreshEntityList()
        {
            _allEntityIds.Clear();
            _allEntityIds.AddRange(_worldDataProvider.GetEntityIds());
            ApplySearchFilter();
        }

        private void ApplySearchFilter()
        {
            _filteredEntityIds.Clear();

            if (string.IsNullOrEmpty(_searchQuery))
            {
                _filteredEntityIds.AddRange(_allEntityIds);
                return;
            }

            foreach (var entityId in _allEntityIds)
            {
                if (MatchesSearchCriteria(entityId))
                {
                    _filteredEntityIds.Add(entityId);
                }
            }
        }

        private bool MatchesSearchCriteria(int entityId)
        {
            switch (_searchMode)
            {
                case EntitySearchMode.ById:
                    return entityId.ToString().Contains(_searchQuery);

                case EntitySearchMode.ByComponentType:
                    return MatchesByComponentType(entityId);

                case EntitySearchMode.ByFieldValue:
                    return MatchesByFieldValue(entityId);

                case EntitySearchMode.All:
                default:
                    return entityId.ToString().Contains(_searchQuery) ||
                           MatchesByComponentType(entityId) ||
                           MatchesByFieldValue(entityId);
            }
        }

        private bool MatchesByComponentType(int entityId)
        {
            var components = _worldDataProvider.GetEntityComponents(entityId);
            return components.Any(c => c.ComponentType.Name.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool MatchesByFieldValue(int entityId)
        {
            var components = _worldDataProvider.GetEntityComponents(entityId);
            foreach (var component in components)
            {
                foreach (var field in component.Fields)
                {
                    var valueStr = field.Value?.ToString() ?? "";
                    if (valueStr.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void DetectDestroyedEntities()
        {
            var destroyedEntities = _allEntityIds.Where(id => !EntityExists(id)).ToList();
            foreach (var id in destroyedEntities)
            {
                _allEntityIds.Remove(id);
                _filteredEntityIds.Remove(id);
                _bookmarkedEntities.Remove(id);
            }

            if (_selectedEntityId >= 0 && !EntityExists(_selectedEntityId))
            {
                _selectedEntityId = -1;
            }
        }

        private bool EntityExists(int entityId)
        {
            return _worldDataProvider.EntityExists(entityId);
        }

        private int GetEntityComponentCount(int entityId)
        {
            if (!_worldDataProvider.IsAvailable) return 0;
            return World.Current?.EntityManager?.Store?.GetEntityComponentCount(entityId) ?? 0;
        }

        private static FieldInfo _cachedEntityVersionsField;

        private int GetEntityVersion(int entityId)
        {
            if (!_worldDataProvider.IsAvailable) return 0;

            try
            {
                var entityManager = World.Current?.EntityManager;
                if (entityManager == null) return 0;

                if (_cachedEntityVersionsField == null)
                {
                    _cachedEntityVersionsField = typeof(EntityManager).GetField("_entityVersions",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_cachedEntityVersionsField?.GetValue(entityManager) is Dictionary<int, int> versions)
                {
                    return versions.TryGetValue(entityId, out var version) ? version : 0;
                }
            }
            catch { }

            return 0;
        }

        private bool EntityHasComponent(int entityId, Type componentType)
        {
            if (!_worldDataProvider.IsAvailable) return false;
            return World.Current?.EntityManager?.Store?.HasComponent(entityId, componentType) ?? false;
        }

        private void CacheComponentTypes()
        {
            if (_availableComponentTypes.Count > 0) return;

            _availableComponentTypes.Clear();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (IsValidComponentType(type))
                        {
                            _availableComponentTypes.Add(type);
                        }
                    }
                }
                catch
                {
                }
            }

            _availableComponentTypes = _availableComponentTypes.OrderBy(t => t.Name).ToList();
        }

        private bool IsValidComponentType(Type type)
        {
            if (type == null || type.IsAbstract || type.IsInterface)
                return false;

            if (!typeof(IComponent).IsAssignableFrom(type))
                return false;

            if (!type.IsValueType)
                return false;

            return IsUnmanagedType(type);
        }

        private bool IsUnmanagedType(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type.IsPointer)
                return true;

            if (!type.IsValueType)
                return false;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!IsUnmanagedType(field.FieldType))
                    return false;
            }

            return true;
        }

        private void AddComponentToEntity(int entityId, Type componentType)
        {
            if (!_worldDataProvider.IsAvailable) return;

            try
            {
                var entityManager = World.Current?.EntityManager;
                if (entityManager == null) return;

                var component = Activator.CreateInstance(componentType);

                var store = entityManager.Store;
                var getOrCreateMethod = typeof(ComponentStore).GetMethod("GetOrCreateStorage");
                var genericMethod = getOrCreateMethod?.MakeGenericMethod(componentType);
                var storage = genericMethod?.Invoke(store, null);

                if (storage != null)
                {
                    var addMethod = storage.GetType().GetMethod("Add");
                    addMethod?.Invoke(storage, new object[] { entityId, component });
                }

                RefreshEntityList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EntityInspector] Failed to add component: {ex.Message}");
            }
        }

        private void RemoveComponentFromEntity(int entityId, Type componentType)
        {
            if (!_worldDataProvider.IsAvailable) return;

            try
            {
                var store = World.Current?.EntityManager?.Store;
                if (store == null) return;

                var storagesField = typeof(ComponentStore).GetField("_storages",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (storagesField?.GetValue(store) is Dictionary<Type, IComponentStorage> storages)
                {
                    if (storages.TryGetValue(componentType, out var storage))
                    {
                        storage.Remove(entityId);
                    }
                }

                RefreshEntityList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EntityInspector] Failed to remove component: {ex.Message}");
            }
        }

        private void DrawMemoryView()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("Component Memory Usage", _headerStyle);

            _memoryScrollPosition = EditorGUILayout.BeginScrollView(_memoryScrollPosition);

            if (!_worldDataProvider.IsAvailable)
            {
                EditorGUILayout.HelpBox("No World available.", MessageType.Warning);
            }
            else
            {
                DrawMemoryStats();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawMemoryStats()
        {
            var entityManager = World.Current?.EntityManager;
            if (entityManager == null) return;

            var store = entityManager.Store;
            var componentTypes = store.GetComponentTypes().OrderBy(t => t.Name).ToList();

            EditorGUILayout.BeginHorizontal(EditorStyles.boldLabel);
            GUILayout.Label("Component Type", GUILayout.Width(250));
            GUILayout.Label("Count", GUILayout.Width(80));
            GUILayout.Label("Capacity", GUILayout.Width(80));
            GUILayout.Label("Est. Memory", GUILayout.Width(100));
            GUILayout.Label("Utilization", GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            foreach (var type in componentTypes)
            {
                DrawComponentMemoryRow(store, type);
            }
        }

        private void DrawComponentMemoryRow(ComponentStore store, Type type)
        {
            int count = 0;
            int capacity = 0;
            int size = 0;

            try
            {
                var storagesField = typeof(ComponentStore).GetField("_storages", BindingFlags.NonPublic | BindingFlags.Instance);
                var storages = storagesField?.GetValue(store) as System.Collections.IDictionary;

                if (storages != null && storages.Contains(type))
                {
                    var storage = storages[type];
                    var countProp = storage.GetType().GetProperty("Count");
                    count = (int)(countProp?.GetValue(storage) ?? 0);

                    var sparseSetField = storage.GetType().GetField("_sparseSet", BindingFlags.NonPublic | BindingFlags.Instance);
                    var sparseSet = sparseSetField?.GetValue(storage);
                    if (sparseSet != null)
                    {
                        var denseField = sparseSet.GetType().GetField("_dense", BindingFlags.NonPublic | BindingFlags.Instance);
                        var dense = denseField?.GetValue(sparseSet);

                        if (dense != null)
                        {
                            var lengthProp = dense.GetType().GetProperty("Length");
                            capacity = (int)(lengthProp?.GetValue(dense) ?? 0);
                        }
                    }

                    size = System.Runtime.InteropServices.Marshal.SizeOf(type);
                }
            }
            catch { }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(type.Name, GUILayout.Width(250));
            GUILayout.Label(count.ToString(), GUILayout.Width(80));
            GUILayout.Label(capacity.ToString(), GUILayout.Width(80));

            long totalBytes = (long)size * capacity;
            GUILayout.Label(EditorUtility.FormatBytes(totalBytes), GUILayout.Width(100));

            float utilization = capacity > 0 ? (float)count / capacity : 0;
            var prevColor = GUI.color;
            GUI.color = Color.Lerp(Color.red, Color.green, utilization);
            GUILayout.Label($"{utilization:P1}", GUILayout.Width(100));
            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();
        }
    }
}

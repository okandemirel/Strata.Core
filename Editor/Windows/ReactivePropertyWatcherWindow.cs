using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Strada.Core.Editor.Utilities;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Editor window for watching ReactiveProperty values in real-time.
    /// Scans scene MonoBehaviours for ReactiveProperty fields, displays their current values,
    /// tracks value change history with timestamps, renders sparklines for numeric types,
    /// and supports pinning properties to a persistent watch list.
    /// </summary>
    public class ReactivePropertyWatcherWindow : EditorWindow
    {
        private const int MaxHistoryPerProperty = 20;
        private const float DefaultRefreshInterval = 0.25f;
        private const float MinRefreshInterval = 0.1f;
        private const float MaxRefreshInterval = 2.0f;
        private const float SparklineHeight = 20f;
        private const float SparklineWidth = 100f;

        private List<ReactivePropertyEntry> _discoveredProperties = new List<ReactivePropertyEntry>();
        private List<ReactivePropertyEntry> _filteredProperties = new List<ReactivePropertyEntry>();
        private HashSet<string> _watchedPropertyKeys = new HashSet<string>();

        private Vector2 _mainScrollPosition;
        private Vector2 _watchScrollPosition;

        private bool _autoRefresh = true;
        private float _refreshInterval = DefaultRefreshInterval;
        private double _lastRefreshTime;
        private double _lastScanTime;
        private const float ScanInterval = 3.0f;

        private string _filterText = "";
        private FilterMode _filterMode = FilterMode.All;

        private bool _showWatchList = true;
        private bool _showDiscovered = true;

        private Dictionary<string, bool> _expandedEntries = new Dictionary<string, bool>();

        private GUIStyle _headerStyle;
        private GUIStyle _propertyRowStyle;
        private GUIStyle _watchedRowStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _changedValueStyle;
        private bool _stylesInitialized;

        private enum FilterMode
        {
            All,
            ByOwnerType,
            ByPropertyName,
            ByValueType
        }

        /// <summary>
        /// Opens the Reactive Property Watcher window.
        /// </summary>
        [MenuItem("Strada/Tools/Reactive Watcher", priority = 54)]
        public static void ShowWindow()
        {
            var window = GetWindow<ReactivePropertyWatcherWindow>("Reactive Watcher");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _discoveredProperties.Clear();
                _filteredProperties.Clear();
                _expandedEntries.Clear();
                ScanForReactiveProperties();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _discoveredProperties.Clear();
                _filteredProperties.Clear();
                _expandedEntries.Clear();
            }

            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (!Application.isPlaying || !_autoRefresh) return;

            double now = EditorApplication.timeSinceStartup;

            if (now - _lastScanTime > ScanInterval)
            {
                ScanForReactiveProperties();
                _lastScanTime = now;
            }

            if (now - _lastRefreshTime > _refreshInterval)
            {
                RefreshValues();
                _lastRefreshTime = now;
                Repaint();
            }
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(5, 5, 10, 5)
            };

            _propertyRowStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 4, 4),
                margin = new RectOffset(5, 5, 2, 2)
            };

            _watchedRowStyle = new GUIStyle(_propertyRowStyle);

            _valueStyle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };

            _changedValueStyle = new GUIStyle(_valueStyle)
            {
                normal = { textColor = StradaEditorStyles.WarningColor }
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitializeStyles();

            DrawToolbar();
            DrawFilterBar();

            if (!Application.isPlaying)
            {
                DrawNotPlayingMessage();
                return;
            }

            if (_discoveredProperties.Count == 0)
            {
                DrawNoPropertiesMessage();
                return;
            }

            DrawMainContent();
            DrawStatusBar();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(40)))
            {
                ScanForReactiveProperties();
                RefreshValues();
            }

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(55)))
            {
                RefreshValues();
            }

            GUILayout.Space(5);

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto Refresh", EditorStyles.toolbarButton, GUILayout.Width(85));

            if (_autoRefresh)
            {
                GUILayout.Label("Interval:", GUILayout.Width(50));
                _refreshInterval = EditorGUILayout.Slider(
                    _refreshInterval, MinRefreshInterval, MaxRefreshInterval, GUILayout.Width(100));
            }

            GUILayout.Space(10);

            _showWatchList = GUILayout.Toggle(_showWatchList, "Watch List", EditorStyles.toolbarButton, GUILayout.Width(75));
            _showDiscovered = GUILayout.Toggle(_showDiscovered, "All Properties", EditorStyles.toolbarButton, GUILayout.Width(85));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Clear History", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                foreach (var entry in _discoveredProperties)
                {
                    entry.ValueHistory.Clear();
                    entry.ChangeCount = 0;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Filter:", GUILayout.Width(40));
            var newFilter = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarSearchField, GUILayout.Width(200));
            if (newFilter != _filterText)
            {
                _filterText = newFilter;
                ApplyFilter();
            }

            if (!string.IsNullOrEmpty(_filterText))
            {
                if (GUILayout.Button("x", EditorStyles.toolbarButton, GUILayout.Width(20)))
                {
                    _filterText = "";
                    ApplyFilter();
                }
            }

            GUILayout.Space(10);

            GUILayout.Label("By:", GUILayout.Width(20));
            var newMode = (FilterMode)EditorGUILayout.EnumPopup(_filterMode, EditorStyles.toolbarPopup, GUILayout.Width(100));
            if (newMode != _filterMode)
            {
                _filterMode = newMode;
                ApplyFilter();
            }

            GUILayout.FlexibleSpace();

            int watchedCount = _watchedPropertyKeys.Count;
            int totalCount = _discoveredProperties.Count;
            GUILayout.Label($"Watching: {watchedCount} | Total: {totalCount}",
                StradaEditorStyles.MiniLabelStyle, GUILayout.Width(160));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawNotPlayingMessage()
        {
            GUILayout.Space(50);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(480));
            EditorGUILayout.HelpBox(
                "REACTIVE PROPERTY WATCHER\n\n" +
                "Monitor ReactiveProperty values in real-time:\n" +
                "  - Automatic discovery of ReactiveProperty<T> fields on MonoBehaviours\n" +
                "  - Live value updates with change detection\n" +
                "  - Value change history with timestamps (last 20 values)\n" +
                "  - Sparkline visualization for numeric properties\n" +
                "  - Pin properties to a persistent watch list\n" +
                "  - Filter by owner type, property name, or value type\n\n" +
                "Enter Play Mode to start watching reactive properties.",
                MessageType.Info);

            GUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Enter Play Mode", GUILayout.Height(30), GUILayout.Width(150)))
            {
                EditorApplication.isPlaying = true;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawNoPropertiesMessage()
        {
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(480));
            EditorGUILayout.HelpBox(
                "No ReactiveProperty fields discovered.\n\n" +
                "The watcher scans MonoBehaviours in the scene for fields of type:\n" +
                "  - Strada.Core.Sync.ReactiveProperty<T>\n" +
                "  - Any field whose type name contains 'ReactiveProperty'\n\n" +
                "Make sure your MonoBehaviours are active in the scene.\n" +
                "Click 'Scan' to search again.",
                MessageType.Info);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private void DrawMainContent()
        {
            if (_showWatchList && _watchedPropertyKeys.Count > 0)
            {
                _showWatchList = StradaEditorStyles.DrawSectionHeader("Watch List", _showWatchList);

                if (_showWatchList)
                {
                    _watchScrollPosition = EditorGUILayout.BeginScrollView(
                        _watchScrollPosition, GUILayout.MaxHeight(250));

                    var watchedEntries = _discoveredProperties
                        .Where(e => _watchedPropertyKeys.Contains(e.UniqueKey))
                        .ToList();

                    foreach (var entry in watchedEntries)
                    {
                        DrawPropertyRow(entry, true);
                    }

                    EditorGUILayout.EndScrollView();

                    StradaEditorStyles.DrawSeparator();
                }
            }

            if (_showDiscovered)
            {
                _showDiscovered = StradaEditorStyles.DrawSectionHeader(
                    $"All Properties ({_filteredProperties.Count})", _showDiscovered);

                if (_showDiscovered)
                {
                    _mainScrollPosition = EditorGUILayout.BeginScrollView(_mainScrollPosition);

                    foreach (var entry in _filteredProperties)
                    {
                        DrawPropertyRow(entry, false);
                    }

                    if (_filteredProperties.Count == 0 && !string.IsNullOrEmpty(_filterText))
                    {
                        EditorGUILayout.HelpBox("No properties match the current filter.", MessageType.Info);
                    }

                    EditorGUILayout.EndScrollView();
                }
            }
        }

        private void DrawPropertyRow(ReactivePropertyEntry entry, bool isWatchSection)
        {
            bool isWatched = _watchedPropertyKeys.Contains(entry.UniqueKey);
            bool recentlyChanged = entry.TimeSinceLastChange < 1.0;

            var prevBg = GUI.backgroundColor;
            if (recentlyChanged)
            {
                GUI.backgroundColor = new Color(
                    StradaEditorStyles.WarningColor.r,
                    StradaEditorStyles.WarningColor.g,
                    StradaEditorStyles.WarningColor.b,
                    0.15f);
            }

            EditorGUILayout.BeginVertical(isWatched && isWatchSection ? _watchedRowStyle : _propertyRowStyle);
            GUI.backgroundColor = prevBg;

            EditorGUILayout.BeginHorizontal();

            if (!_expandedEntries.ContainsKey(entry.UniqueKey))
            {
                _expandedEntries[entry.UniqueKey] = false;
            }

            _expandedEntries[entry.UniqueKey] = EditorGUILayout.Foldout(
                _expandedEntries[entry.UniqueKey], GUIContent.none, true, EditorStyles.foldout);

            if (!isWatchSection)
            {
                bool newWatch = GUILayout.Toggle(isWatched, "", GUILayout.Width(18));
                if (newWatch != isWatched)
                {
                    if (newWatch)
                        _watchedPropertyKeys.Add(entry.UniqueKey);
                    else
                        _watchedPropertyKeys.Remove(entry.UniqueKey);
                }
            }
            else
            {
                if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(18), GUILayout.Height(16)))
                {
                    _watchedPropertyKeys.Remove(entry.UniqueKey);
                }
            }

            var ownerContent = new GUIContent(entry.OwnerName, $"Type: {entry.OwnerTypeName}");
            if (GUILayout.Button(ownerContent, EditorStyles.label, GUILayout.Width(120)))
            {
                if (entry.OwnerObject != null)
                {
                    EditorGUIUtility.PingObject(entry.OwnerObject);
                    Selection.activeObject = entry.OwnerObject;
                }
            }

            GUILayout.Label(".", GUILayout.Width(5));

            var fieldContent = new GUIContent(entry.FieldName, $"Value type: {entry.ValueTypeName}");
            GUILayout.Label(fieldContent, EditorStyles.boldLabel, GUILayout.Width(120));

            GUILayout.Label("=", GUILayout.Width(12));

            var valueStyleToUse = recentlyChanged ? _changedValueStyle : _valueStyle;
            string displayValue = entry.CurrentValueString ?? "(null)";
            GUILayout.Label(displayValue, valueStyleToUse, GUILayout.MinWidth(80));

            GUILayout.FlexibleSpace();

            if (entry.IsNumeric && entry.ValueHistory.Count > 1)
            {
                DrawSparkline(entry);
            }

            var prevColor = GUI.contentColor;
            GUI.contentColor = entry.ChangeCount > 0 ? StradaEditorStyles.InfoColor : Color.gray;
            GUILayout.Label($"[{entry.ChangeCount}]", StradaEditorStyles.MiniLabelStyle, GUILayout.Width(40));
            GUI.contentColor = prevColor;

            var subscriberCount = entry.SubscriberCount;
            GUI.contentColor = subscriberCount > 0 ? StradaEditorStyles.SuccessColor : Color.gray;
            GUILayout.Label($"S:{subscriberCount}", StradaEditorStyles.MiniLabelStyle, GUILayout.Width(30));
            GUI.contentColor = prevColor;

            EditorGUILayout.EndHorizontal();

            if (_expandedEntries.TryGetValue(entry.UniqueKey, out bool expanded) && expanded)
            {
                DrawExpandedDetails(entry);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawExpandedDetails(ReactivePropertyEntry entry)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Owner Type:", GUILayout.Width(80));
            EditorGUILayout.SelectableLabel(entry.OwnerTypeName, EditorStyles.textField, GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Value Type:", GUILayout.Width(80));
            EditorGUILayout.SelectableLabel(entry.ValueTypeName, EditorStyles.textField, GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Subscribers:", GUILayout.Width(80));
            EditorGUILayout.LabelField(entry.SubscriberCount.ToString());
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Changes:", GUILayout.Width(80));
            EditorGUILayout.LabelField(entry.ChangeCount.ToString());
            EditorGUILayout.EndHorizontal();

            if (entry.ValueHistory.Count > 0)
            {
                GUILayout.Space(4);
                EditorGUILayout.LabelField("Value History:", EditorStyles.boldLabel);

                int startIndex = Mathf.Max(0, entry.ValueHistory.Count - 10);
                for (int i = entry.ValueHistory.Count - 1; i >= startIndex; i--)
                {
                    var historyEntry = entry.ValueHistory[i];
                    EditorGUILayout.BeginHorizontal();

                    var prevColor = GUI.contentColor;
                    GUI.contentColor = Color.gray;
                    EditorGUILayout.LabelField(historyEntry.Timestamp.ToString("HH:mm:ss.fff"),
                        StradaEditorStyles.MiniLabelStyle, GUILayout.Width(90));
                    GUI.contentColor = prevColor;

                    EditorGUILayout.LabelField(historyEntry.ValueString ?? "(null)");
                    EditorGUILayout.EndHorizontal();
                }

                if (entry.ValueHistory.Count > 10)
                {
                    EditorGUILayout.LabelField(
                        $"... {entry.ValueHistory.Count - 10} more entries",
                        EditorStyles.centeredGreyMiniLabel);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Draws a mini sparkline showing the recent numeric value trend.
        /// </summary>
        private void DrawSparkline(ReactivePropertyEntry entry)
        {
            var rect = GUILayoutUtility.GetRect(SparklineWidth, SparklineHeight,
                GUILayout.Width(SparklineWidth), GUILayout.Height(SparklineHeight));

            EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 0.8f));

            var numericValues = new List<float>();
            foreach (var h in entry.ValueHistory)
            {
                if (h.NumericValue.HasValue)
                {
                    numericValues.Add(h.NumericValue.Value);
                }
            }

            if (numericValues.Count < 2) return;

            float minVal = numericValues.Min();
            float maxVal = numericValues.Max();
            float range = maxVal - minVal;

            if (range < 0.0001f)
            {
                float centerY = rect.y + rect.height * 0.5f;
                Handles.color = StradaEditorStyles.InfoColor;
                Handles.DrawLine(
                    new Vector3(rect.x + 1, centerY),
                    new Vector3(rect.xMax - 1, centerY));
                return;
            }

            Handles.color = StradaEditorStyles.InfoColor;
            int pointCount = numericValues.Count;
            float xStep = (rect.width - 2) / Mathf.Max(1, pointCount - 1);

            for (int i = 0; i < pointCount - 1; i++)
            {
                float normalizedA = (numericValues[i] - minVal) / range;
                float normalizedB = (numericValues[i + 1] - minVal) / range;

                float xA = rect.x + 1 + i * xStep;
                float yA = rect.yMax - 1 - normalizedA * (rect.height - 2);
                float xB = rect.x + 1 + (i + 1) * xStep;
                float yB = rect.yMax - 1 - normalizedB * (rect.height - 2);

                Handles.DrawLine(new Vector3(xA, yA), new Vector3(xB, yB));
            }
        }

        private void DrawStatusBar()
        {
            StradaEditorStyles.DrawSeparator();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (_autoRefresh)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = StradaEditorStyles.SuccessColor;
                GUILayout.Label("LIVE", EditorStyles.toolbarButton, GUILayout.Width(40));
                GUI.contentColor = prevColor;
            }

            GUILayout.Label($"Properties: {_discoveredProperties.Count}", GUILayout.Width(100));
            GUILayout.Label($"Watched: {_watchedPropertyKeys.Count}", GUILayout.Width(80));

            int totalChanges = _discoveredProperties.Sum(p => p.ChangeCount);
            GUILayout.Label($"Total Changes: {totalChanges}", GUILayout.Width(110));

            GUILayout.FlexibleSpace();

            GUILayout.Label($"Last scan: {DateTime.Now:HH:mm:ss}",
                StradaEditorStyles.MiniLabelStyle, GUILayout.Width(120));

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Scans all MonoBehaviours in the scene for ReactiveProperty fields.
        /// Preserves existing entries and their history when re-scanning.
        /// </summary>
        private void ScanForReactiveProperties()
        {
            if (!Application.isPlaying) return;

            var existingEntries = new Dictionary<string, ReactivePropertyEntry>();
            foreach (var entry in _discoveredProperties)
            {
                existingEntries[entry.UniqueKey] = entry;
            }

            var newEntries = new List<ReactivePropertyEntry>();

            var monoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            foreach (var mb in monoBehaviours)
            {
                if (mb == null) continue;

                try
                {
                    ScanObjectForReactiveProperties(mb, mb.GetType(), mb, newEntries, existingEntries);
                }
                catch
                {
                    // Skip objects that cannot be reflected
                }
            }

            _discoveredProperties = newEntries;
            ApplyFilter();
        }

        /// <summary>
        /// Scans a single object for ReactiveProperty fields, including private and inherited fields.
        /// </summary>
        private void ScanObjectForReactiveProperties(
            object target,
            Type targetType,
            UnityEngine.Object ownerObject,
            List<ReactivePropertyEntry> results,
            Dictionary<string, ReactivePropertyEntry> existingEntries)
        {
            var type = targetType;
            while (type != null && type != typeof(MonoBehaviour) && type != typeof(UnityEngine.Object) && type != typeof(object))
            {
                var fields = type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (var field in fields)
                {
                    if (IsReactivePropertyField(field))
                    {
                        var reactiveObj = field.GetValue(target);
                        if (reactiveObj == null) continue;

                        string key = GenerateUniqueKey(ownerObject, field);

                        if (existingEntries.TryGetValue(key, out var existing))
                        {
                            existing.OwnerObject = ownerObject;
                            existing.ReactiveInstance = reactiveObj;
                            existing.Field = field;
                            results.Add(existing);
                        }
                        else
                        {
                            var entry = CreateEntry(ownerObject, field, reactiveObj);
                            results.Add(entry);
                        }
                    }
                }

                type = type.BaseType;
            }
        }

        /// <summary>
        /// Determines whether a field is a ReactiveProperty type.
        /// </summary>
        private static bool IsReactivePropertyField(FieldInfo field)
        {
            var fieldType = field.FieldType;

            if (fieldType.IsGenericType)
            {
                var genericDef = fieldType.GetGenericTypeDefinition();
                if (genericDef == typeof(Sync.ReactiveProperty<>))
                    return true;

                string genericName = genericDef.FullName ?? genericDef.Name;
                if (genericName.Contains("ReactiveProperty"))
                    return true;
            }

            string typeName = fieldType.FullName ?? fieldType.Name;
            return typeName.Contains("ReactiveProperty");
        }

        /// <summary>
        /// Creates a new ReactivePropertyEntry for a discovered field.
        /// </summary>
        private static ReactivePropertyEntry CreateEntry(UnityEngine.Object owner, FieldInfo field, object reactiveInstance)
        {
            string valueTypeName = "Unknown";
            if (field.FieldType.IsGenericType)
            {
                var genericArgs = field.FieldType.GetGenericArguments();
                if (genericArgs.Length > 0)
                {
                    valueTypeName = genericArgs[0].Name;
                }
            }

            bool isNumeric = IsNumericValueType(valueTypeName);

            return new ReactivePropertyEntry
            {
                UniqueKey = GenerateUniqueKey(owner, field),
                OwnerObject = owner,
                OwnerName = owner.name,
                OwnerTypeName = owner.GetType().Name,
                FieldName = field.Name,
                Field = field,
                ReactiveInstance = reactiveInstance,
                ValueTypeName = valueTypeName,
                IsNumeric = isNumeric,
                CurrentValueString = null,
                ChangeCount = 0,
                SubscriberCount = 0,
                LastChangeTime = -1,
                ValueHistory = new List<ValueHistoryEntry>()
            };
        }

        private static string GenerateUniqueKey(UnityEngine.Object owner, FieldInfo field)
        {
            // UnityEngine.Object overrides GetHashCode() to return its instance id, so this is
            // identical to the old GetInstanceID() key without touching the obsoleted API
            // (GetInstanceID is an error-level obsolete from Unity 6000.5 onwards).
            int instanceId = owner.GetHashCode();
            return $"{instanceId}:{owner.GetType().FullName}:{field.Name}";
        }

        /// <summary>
        /// Refreshes the current values of all discovered reactive properties.
        /// Detects value changes and records history.
        /// </summary>
        private void RefreshValues()
        {
            for (int i = _discoveredProperties.Count - 1; i >= 0; i--)
            {
                var entry = _discoveredProperties[i];

                try
                {
                    if (entry.OwnerObject == null)
                    {
                        _discoveredProperties.RemoveAt(i);
                        continue;
                    }

                    var reactiveObj = entry.Field.GetValue(entry.OwnerObject);
                    if (reactiveObj == null)
                    {
                        entry.CurrentValueString = "(disposed)";
                        continue;
                    }

                    entry.ReactiveInstance = reactiveObj;

                    string newValueStr = ReadValueAsString(reactiveObj);
                    float? numericValue = entry.IsNumeric ? ReadNumericValue(reactiveObj) : null;
                    int subscriberCount = ReadSubscriberCount(reactiveObj);

                    entry.SubscriberCount = subscriberCount;

                    if (entry.CurrentValueString != null && newValueStr != entry.CurrentValueString)
                    {
                        entry.ChangeCount++;
                        entry.LastChangeTime = EditorApplication.timeSinceStartup;

                        entry.ValueHistory.Add(new ValueHistoryEntry
                        {
                            Timestamp = DateTime.Now,
                            ValueString = newValueStr,
                            NumericValue = numericValue
                        });

                        if (entry.ValueHistory.Count > MaxHistoryPerProperty)
                        {
                            entry.ValueHistory.RemoveAt(0);
                        }
                    }
                    else if (entry.CurrentValueString == null && newValueStr != null)
                    {
                        entry.ValueHistory.Add(new ValueHistoryEntry
                        {
                            Timestamp = DateTime.Now,
                            ValueString = newValueStr,
                            NumericValue = numericValue
                        });
                    }

                    entry.CurrentValueString = newValueStr;
                }
                catch
                {
                    entry.CurrentValueString = "(error)";
                }
            }

            ApplyFilter();
        }

        /// <summary>
        /// Reads the Value property from a ReactiveProperty instance via reflection.
        /// </summary>
        private static string ReadValueAsString(object reactiveInstance)
        {
            var valueProperty = reactiveInstance.GetType().GetProperty("Value",
                BindingFlags.Public | BindingFlags.Instance);

            if (valueProperty == null) return "(no Value property)";

            try
            {
                var value = valueProperty.GetValue(reactiveInstance);
                return FormatValue(value);
            }
            catch
            {
                return "(read error)";
            }
        }

        /// <summary>
        /// Reads the Value property as a numeric float for sparkline rendering.
        /// </summary>
        private static float? ReadNumericValue(object reactiveInstance)
        {
            var valueProperty = reactiveInstance.GetType().GetProperty("Value",
                BindingFlags.Public | BindingFlags.Instance);

            if (valueProperty == null) return null;

            try
            {
                var value = valueProperty.GetValue(reactiveInstance);
                if (value == null) return null;

                return value switch
                {
                    int i => i,
                    float f => f,
                    double d => (float)d,
                    long l => l,
                    short s => s,
                    byte b => b,
                    uint ui => ui,
                    decimal dec => (float)dec,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the SubscriberCount property from a ReactiveProperty instance.
        /// </summary>
        private static int ReadSubscriberCount(object reactiveInstance)
        {
            var prop = reactiveInstance.GetType().GetProperty("SubscriberCount",
                BindingFlags.Public | BindingFlags.Instance);

            if (prop == null) return 0;

            try
            {
                var value = prop.GetValue(reactiveInstance);
                return value is int count ? count : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Formats a value for display, handling common Unity and primitive types.
        /// </summary>
        private static string FormatValue(object value)
        {
            if (value == null) return "(null)";

            var type = value.GetType();

            if (type == typeof(float)) return ((float)value).ToString("F4");
            if (type == typeof(double)) return ((double)value).ToString("F4");
            if (type == typeof(Vector2)) return ((Vector2)value).ToString("F3");
            if (type == typeof(Vector3)) return ((Vector3)value).ToString("F3");
            if (type == typeof(Vector4)) return ((Vector4)value).ToString("F3");
            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value;
                return $"({q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3})";
            }
            if (type == typeof(Color)) return ((Color)value).ToString();
            if (type == typeof(bool)) return ((bool)value) ? "True" : "False";

            return value.ToString();
        }

        /// <summary>
        /// Checks whether a type name represents a numeric type suitable for sparkline display.
        /// </summary>
        private static bool IsNumericValueType(string typeName)
        {
            return typeName switch
            {
                "Int32" or "Single" or "Double" or "Int64" or "Int16" or "Byte"
                    or "UInt32" or "UInt64" or "Decimal" or "SByte" or "UInt16"
                    or "int" or "float" or "double" or "long" or "short" or "byte" => true,
                _ => false
            };
        }

        /// <summary>
        /// Applies the current filter settings to produce the filtered property list.
        /// </summary>
        private void ApplyFilter()
        {
            if (string.IsNullOrEmpty(_filterText))
            {
                _filteredProperties = new List<ReactivePropertyEntry>(_discoveredProperties);
                return;
            }

            _filteredProperties = _discoveredProperties.Where(entry =>
            {
                return _filterMode switch
                {
                    FilterMode.ByOwnerType =>
                        entry.OwnerTypeName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0,
                    FilterMode.ByPropertyName =>
                        entry.FieldName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0,
                    FilterMode.ByValueType =>
                        entry.ValueTypeName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0,
                    _ =>
                        entry.OwnerTypeName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        entry.OwnerName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        entry.FieldName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        entry.ValueTypeName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0
                };
            }).ToList();
        }

        /// <summary>
        /// Represents a single discovered ReactiveProperty field and its monitoring state.
        /// </summary>
        private class ReactivePropertyEntry
        {
            public string UniqueKey;
            public UnityEngine.Object OwnerObject;
            public string OwnerName;
            public string OwnerTypeName;
            public string FieldName;
            public FieldInfo Field;
            public object ReactiveInstance;
            public string ValueTypeName;
            public bool IsNumeric;
            public string CurrentValueString;
            public int ChangeCount;
            public int SubscriberCount;
            public double LastChangeTime;
            public List<ValueHistoryEntry> ValueHistory;

            /// <summary>
            /// Returns the time in seconds since the last value change, or infinity if never changed.
            /// </summary>
            public double TimeSinceLastChange
            {
                get
                {
                    if (LastChangeTime < 0) return double.PositiveInfinity;
                    return EditorApplication.timeSinceStartup - LastChangeTime;
                }
            }
        }

        /// <summary>
        /// Stores a single historical value snapshot with its timestamp and optional numeric representation.
        /// </summary>
        private class ValueHistoryEntry
        {
            public DateTime Timestamp;
            public string ValueString;
            public float? NumericValue;
        }
    }
}

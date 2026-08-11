using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Strada.Core.Editor.DataProviders;
using Strada.Core.Editor.DataProviders.Models;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Editor window for debugging EventBus messages.
    /// Provides message logging, filtering, payload inspection,
    /// breakpoints, statistics, chain tracking, export, and bookmarking.
    /// Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6
    /// </summary>
    public class BusDebuggerWindow : EditorWindow
    {
        private const int MaxLogEntries = 1000;
        private const float MinMessageListWidth = 300f;
        private const float MaxMessageListWidth = 600f;
        private const float DefaultMessageListWidth = 450f;
        private const float StatisticsWindowSeconds = 5f;
        private const double MessageChainThresholdMs = 1.0;

        private float _messageListWidth = DefaultMessageListWidth;
        private bool _isResizing;

        private Vector2 _messageListScrollPosition;
        private Vector2 _detailScrollPosition;

        private int _selectedMessageIndex = -1;

        private string _typeFilterPattern = "";
        private MessageKind? _kindFilter;
        private bool _showFilterOptions;

        private enum Tab { Log, FlowGraph, Statistics }
        private Tab _currentTab = Tab.Log;
        private Vector2 _graphScrollPosition;
        private List<Type> _cachedEventTypes = new List<Type>();
        private bool _graphNeedsRefresh = true;

        private bool _isPaused;
        private bool _autoScroll = true;

        private List<MessageLogEntry> _displayedEntries = new List<MessageLogEntry>();
        private double _lastRefreshTime;
        private float _refreshInterval = 0.1f;

        // Breakpoints
        private HashSet<string> _breakpoints = new HashSet<string>();
        private bool _showBreakpointManager;

        // Bookmarks. Keyed on the entry reference: the provider hands back the same
        // MessageLogEntry instances on every refresh, so identity survives a refresh where a
        // list index would not.
        private bool _showBookmarkedOnly;
        private Dictionary<MessageLogEntry, bool> _bookmarkedEntries = new Dictionary<MessageLogEntry, bool>();
        private readonly List<MessageLogEntry> _staleBookmarks = new List<MessageLogEntry>();

        // Statistics
        private Vector2 _statisticsScrollPosition;

        private GUIStyle _headerStyle;
        private GUIStyle _messageItemStyle;
        private GUIStyle _selectedMessageStyle;
        private GUIStyle _eventStyle;
        private GUIStyle _commandStyle;
        private GUIStyle _queryStyle;
        private GUIStyle _warningIconStyle;
        private GUIStyle _breakpointStyle;
        private GUIStyle _bookmarkStyle;
        private bool _stylesInitialized;

        private readonly Color _eventColor = new Color(0.4f, 0.7f, 0.4f);
        private readonly Color _commandColor = new Color(0.5f, 0.6f, 0.9f);
        private readonly Color _queryColor = new Color(0.9f, 0.7f, 0.4f);
        private readonly Color _warningColor = new Color(1.0f, 0.6f, 0.2f);
        private readonly Color _selectedColor = new Color(0.24f, 0.49f, 0.91f, 0.4f);
        private readonly Color _breakpointColor = new Color(0.9f, 0.2f, 0.2f);
        private readonly Color _bookmarkColor = new Color(0.9f, 0.8f, 0.2f);
        private readonly Color _resizeHandleColor = new Color(0.25f, 0.25f, 0.25f, 1f);

        private BusDataProvider _busDataProvider;

        public static void ShowWindow()
        {
            var window = GetWindow<BusDebuggerWindow>("Bus Debugger");
            window.minSize = new Vector2(600, 400);
        }

        private void OnEnable()
        {
            _busDataProvider = BusDataProvider.Instance;
            _busDataProvider.OnDataChanged += OnDataChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            if (_busDataProvider != null)
            {
                _busDataProvider.OnDataChanged -= OnDataChanged;
            }
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnDataChanged()
        {
            if (!_isPaused)
            {
                RefreshDisplayedEntries();
                CheckBreakpoints();
                Repaint();
            }
        }

        /// <summary>
        /// Checks incoming messages against breakpoints. If a breakpoint type is
        /// found among the newest entries, pauses logging and selects that message.
        /// </summary>
        private void CheckBreakpoints()
        {
            if (_breakpoints.Count == 0 || _isPaused) return;

            for (int i = _displayedEntries.Count - 1; i >= 0; i--)
            {
                var entry = _displayedEntries[i];
                var typeName = entry.MessageType?.Name;
                if (typeName != null && _breakpoints.Contains(typeName))
                {
                    _isPaused = true;
                    _selectedMessageIndex = i;
                    _autoScroll = false;
                    break;
                }
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _displayedEntries.Clear();
                _selectedMessageIndex = -1;
                _bookmarkedEntries.Clear();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _displayedEntries.Clear();
                _selectedMessageIndex = -1;
                _isPaused = false;
                _bookmarkedEntries.Clear();
            }
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (!Application.isPlaying || _isPaused) return;

            if (EditorApplication.timeSinceStartup - _lastRefreshTime > _refreshInterval)
            {
                RefreshDisplayedEntries();
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

            _messageItemStyle = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(4, 4, 3, 3),
                margin = new RectOffset(2, 2, 1, 1),
                richText = true
            };

            _selectedMessageStyle = new GUIStyle(_messageItemStyle)
            {
                normal = { background = CreateColorTexture(_selectedColor) }
            };

            _eventStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = _eventColor },
                fontStyle = FontStyle.Bold
            };

            _commandStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = _commandColor },
                fontStyle = FontStyle.Bold
            };

            _queryStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = _queryColor },
                fontStyle = FontStyle.Bold
            };

            _warningIconStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = _warningColor },
                fontStyle = FontStyle.Bold,
                fontSize = 14
            };

            _breakpointStyle = new GUIStyle(EditorStyles.miniButton)
            {
                normal = { textColor = _breakpointColor },
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(2, 2, 1, 1)
            };

            _bookmarkStyle = new GUIStyle(EditorStyles.miniButton)
            {
                padding = new RectOffset(2, 2, 1, 1)
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

            if (!_busDataProvider.IsAvailable)
            {
                DrawNoBusMessage();
                return;
            }

            DrawToolbar();

            if (_currentTab == Tab.Log)
            {
                DrawFilterBar();
                DrawSplitView();
            }
            else if (_currentTab == Tab.FlowGraph)
            {
                DrawFlowGraph();
            }
            else if (_currentTab == Tab.Statistics)
            {
                DrawStatisticsPanel();
            }
        }

        private void DrawNotPlayingMessage()
        {
            GUILayout.Space(50);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(450));
            EditorGUILayout.HelpBox(
                "STRADABUS MESSAGE DEBUGGER\n\n" +
                "Real-time monitoring of EventBus messages:\n" +
                "• View Events, Signals, and Queries\n" +
                "• Filter by message type pattern\n" +
                "• Inspect message payloads\n" +
                "• Detect unhandled signals\n" +
                "• Set breakpoints on message types\n" +
                "• View message statistics\n" +
                "• Export logs to JSON\n\n" +
                "Enter Play Mode to start debugging.",
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

        private void DrawNoBusMessage()
        {
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox(
                "No active EventBus found.\n\nEnsure a World with a Bus is created.",
                MessageType.Warning,
                true);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }


        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            _currentTab = (Tab)GUILayout.Toolbar((int)_currentTab, new[] { "Log", "Flow Graph", "Statistics" }, EditorStyles.toolbarButton, GUILayout.Width(230));
            GUILayout.Space(10);

            var isLogging = _busDataProvider.IsLogging;
            var logIcon = isLogging ? "\u25CF" : "\u25CB";
            var logColor = isLogging ? Color.green : Color.gray;
            var prevColor = GUI.contentColor;
            GUI.contentColor = logColor;

            if (GUILayout.Button($"{logIcon} Log", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                if (isLogging)
                    _busDataProvider.StopLogging();
                else
                    _busDataProvider.StartLogging();
            }

            GUI.contentColor = prevColor;

            var pauseIcon = _isPaused ? "\u25B6" : "\u275A\u275A";
            var pauseTooltip = _isPaused ? "Resume" : "Pause";
            if (GUILayout.Button(new GUIContent(pauseIcon, pauseTooltip), EditorStyles.toolbarButton, GUILayout.Width(30)))
            {
                _isPaused = !_isPaused;
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                ClearLog();
            }

            if (GUILayout.Button("Export", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ExportLogToJson();
            }

            GUILayout.Space(10);

            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", EditorStyles.toolbarButton, GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            var totalCount = _busDataProvider.GetLogEntries().Count;
            var filteredCount = _displayedEntries.Count;
            GUILayout.Label($"Messages: {filteredCount}/{totalCount}", EditorStyles.toolbarButton);

            if (totalCount >= MaxLogEntries)
            {
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = _warningColor;
                GUILayout.Label("Buffer Full", EditorStyles.toolbarButton, GUILayout.Width(70));
                GUI.backgroundColor = prevBg;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFlowGraph()
        {
            if (_graphNeedsRefresh)
            {
                RefreshGraphData();
                _graphNeedsRefresh = false;
            }

            EditorGUILayout.BeginVertical();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh Graph", EditorStyles.toolbarButton))
            {
                _graphNeedsRefresh = true;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            _graphScrollPosition = EditorGUILayout.BeginScrollView(_graphScrollPosition);

            if (_cachedEventTypes.Count == 0)
            {
                EditorGUILayout.HelpBox("No events found or not in Play Mode.", MessageType.Info);
            }
            else
            {
                foreach (var eventType in _cachedEventTypes)
                {
                    DrawEventNode(eventType);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void RefreshGraphData()
        {
            _cachedEventTypes.Clear();
            if (!Application.isPlaying) return;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsValueType && !type.IsPrimitive && !type.IsEnum &&
                           (type.Name.EndsWith("Event") || type.Name.EndsWith("Signal")))
                        {
                            _cachedEventTypes.Add(type);
                        }
                    }
                }
                catch {}
            }
            _cachedEventTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        private void DrawEventNode(Type eventType)
        {
            var subscribers = _busDataProvider.GetSubscriberDetails(eventType);
            if (subscribers.Count == 0) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(eventType.Name, EditorStyles.boldLabel, GUILayout.Width(200));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{subscribers.Count} Subscribers", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            foreach (var sub in subscribers)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("\u21B3", GUILayout.Width(15));

                var targetName = sub.Target?.GetType().Name ?? "Static/Unknown";
                var methodName = sub.Method?.Name ?? "Unknown";

                if (GUILayout.Button($"{targetName}.{methodName}", EditorStyles.label))
                {
                    if (sub.Target is MonoBehaviour mb)
                    {
                        EditorGUIUtility.PingObject(mb);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
            GUILayout.Space(5);
        }

        private void DrawFilterBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            _showFilterOptions = GUILayout.Toggle(_showFilterOptions, "Filters", EditorStyles.toolbarButton, GUILayout.Width(55));

            if (_showFilterOptions)
            {
                GUILayout.Space(10);

                GUILayout.Label("Type:", GUILayout.Width(35));
                var newPattern = EditorGUILayout.TextField(_typeFilterPattern, EditorStyles.toolbarSearchField, GUILayout.Width(200));
                if (newPattern != _typeFilterPattern)
                {
                    _typeFilterPattern = newPattern;
                    RefreshDisplayedEntries();
                }

                if (!string.IsNullOrEmpty(_typeFilterPattern))
                {
                    GUILayout.Label("(* = wildcard)", EditorStyles.miniLabel, GUILayout.Width(80));
                }

                GUILayout.Space(10);

                GUILayout.Label("Kind:", GUILayout.Width(35));
                var kindOptions = new[] { "All", "Event", "Command", "Query" };
                var currentKindIndex = _kindFilter.HasValue ? (int)_kindFilter.Value + 1 : 0;
                var newKindIndex = EditorGUILayout.Popup(currentKindIndex, kindOptions, EditorStyles.toolbarPopup, GUILayout.Width(80));

                var newKindFilter = newKindIndex == 0 ? (MessageKind?)null : (MessageKind)(newKindIndex - 1);
                if (newKindFilter != _kindFilter)
                {
                    _kindFilter = newKindFilter;
                    RefreshDisplayedEntries();
                }

                GUILayout.Space(10);

                // Bookmarked only toggle
                var newShowBookmarked = GUILayout.Toggle(_showBookmarkedOnly, "Bookmarked", EditorStyles.toolbarButton, GUILayout.Width(75));
                if (newShowBookmarked != _showBookmarkedOnly)
                {
                    _showBookmarkedOnly = newShowBookmarked;
                    Repaint();
                }

                GUILayout.Space(10);

                if (GUILayout.Button("Clear Filters", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    _typeFilterPattern = "";
                    _kindFilter = null;
                    _showBookmarkedOnly = false;
                    RefreshDisplayedEntries();
                }
            }

            GUILayout.FlexibleSpace();

            // Manage Breakpoints toggle
            _showBreakpointManager = GUILayout.Toggle(_showBreakpointManager, $"Breakpoints ({_breakpoints.Count})", EditorStyles.toolbarButton, GUILayout.Width(100));

            GUILayout.Space(5);

            DrawLegend();

            EditorGUILayout.EndHorizontal();

            // Draw breakpoint manager section if toggled
            if (_showBreakpointManager)
            {
                DrawBreakpointManager();
            }
        }

        /// <summary>
        /// Draws the Manage Breakpoints section below the filter bar.
        /// Lists all active breakpoints with the ability to remove them.
        /// </summary>
        private void DrawBreakpointManager()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Manage Breakpoints", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_breakpoints.Count > 0 && GUILayout.Button("Clear All", EditorStyles.miniButton, GUILayout.Width(60)))
            {
                _breakpoints.Clear();
            }
            EditorGUILayout.EndHorizontal();

            if (_breakpoints.Count == 0)
            {
                EditorGUILayout.LabelField("No breakpoints set. Click 'B' next to a message type to add one.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                string toRemove = null;
                foreach (var bp in _breakpoints)
                {
                    EditorGUILayout.BeginHorizontal();

                    var prevContentColor = GUI.contentColor;
                    GUI.contentColor = _breakpointColor;
                    GUILayout.Label("\u25CF", GUILayout.Width(14));
                    GUI.contentColor = prevContentColor;

                    GUILayout.Label(bp, GUILayout.ExpandWidth(true));

                    if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(20)))
                    {
                        toRemove = bp;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                if (toRemove != null)
                {
                    _breakpoints.Remove(toRemove);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawLegend()
        {
            GUILayout.Label("Legend:", GUILayout.Width(50));

            DrawLegendSwatch(_eventColor, "Event", 35);
            DrawLegendSwatch(_commandColor, "Cmd", 30);
            DrawLegendSwatch(_queryColor, "Query", 35);

            GUILayout.Label("\u26A0", _warningIconStyle, GUILayout.Width(15));
            GUILayout.Label("No Handler", GUILayout.Width(65));
        }

        private void DrawLegendSwatch(Color color, string label, float labelWidth)
        {
            var rect = GUILayoutUtility.GetRect(10, 10);
            EditorGUI.DrawRect(rect, color);
            GUILayout.Label(label, GUILayout.Width(labelWidth));
        }

        private void DrawSplitView()
        {
            EditorGUILayout.BeginHorizontal();

            DrawMessageListPanel();

            DrawResizeHandle();

            DrawMessageDetailsPanel();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawMessageListPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(_messageListWidth));

            EditorGUILayout.LabelField("Message Log", _headerStyle);

            _messageListScrollPosition = EditorGUILayout.BeginScrollView(_messageListScrollPosition);

            if (_displayedEntries.Count == 0)
            {
                if (_busDataProvider.IsLogging)
                {
                    EditorGUILayout.HelpBox("Waiting for messages...\nPublish events, commands, or queries to see them here.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("Logging is disabled.\nClick 'Log' to start capturing messages.", MessageType.Info);
                }
            }
            else
            {
                for (int i = 0; i < _displayedEntries.Count; i++)
                {
                    var entry = _displayedEntries[i];

                    // Bookmark filter: skip non-bookmarked entries when filter is active
                    if (_showBookmarkedOnly && !IsBookmarked(entry))
                    {
                        continue;
                    }

                    DrawMessageListItem(i, entry);
                }

                if (_autoScroll && !_isPaused && Event.current.type == EventType.Repaint)
                {
                    _messageListScrollPosition.y = float.MaxValue;
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }


        private void DrawMessageListItem(int index, MessageLogEntry entry)
        {
            var isSelected = index == _selectedMessageIndex;
            var style = isSelected ? _selectedMessageStyle : _messageItemStyle;

            EditorGUILayout.BeginHorizontal(style);

            // Bookmark star button
            var isBookmarked = IsBookmarked(entry);
            var prevContentColor = GUI.contentColor;
            GUI.contentColor = isBookmarked ? _bookmarkColor : Color.gray;
            if (GUILayout.Button(isBookmarked ? "\u2605" : "\u2606", _bookmarkStyle, GUILayout.Width(18)))
            {
                ToggleBookmark(index, entry);
            }
            GUI.contentColor = prevContentColor;

            // Breakpoint or warning icon
            var typeName = entry.MessageType?.Name;
            bool hasBreakpoint = typeName != null && _breakpoints.Contains(typeName);

            if (hasBreakpoint)
            {
                prevContentColor = GUI.contentColor;
                GUI.contentColor = _breakpointColor;
                GUILayout.Label("\u25CF", GUILayout.Width(14));
                GUI.contentColor = prevContentColor;
            }
            else if (entry.Kind == MessageKind.Command && !entry.HasHandler)
            {
                GUILayout.Label("\u26A0", _warningIconStyle, GUILayout.Width(14));
            }
            else
            {
                GUILayout.Space(14);
            }

            var timeStr = entry.Timestamp.ToString("HH:mm:ss.fff");
            GUILayout.Label(timeStr, EditorStyles.miniLabel, GUILayout.Width(75));

            var kindStyle = GetKindStyle(entry.Kind);
            var kindLabel = GetKindLabel(entry.Kind);
            GUILayout.Label(kindLabel, kindStyle, GUILayout.Width(45));

            var displayTypeName = typeName ?? "Unknown";
            if (GUILayout.Button(displayTypeName, EditorStyles.label, GUILayout.ExpandWidth(true)))
            {
                _selectedMessageIndex = index;
            }

            // Breakpoint toggle button
            var bpActive = typeName != null && _breakpoints.Contains(typeName);
            var prevBgColor = GUI.backgroundColor;
            if (bpActive)
            {
                GUI.backgroundColor = _breakpointColor;
            }
            if (GUILayout.Button("B", _breakpointStyle, GUILayout.Width(20)))
            {
                if (typeName != null)
                {
                    if (_breakpoints.Contains(typeName))
                        _breakpoints.Remove(typeName);
                    else
                        _breakpoints.Add(typeName);
                }
            }
            GUI.backgroundColor = prevBgColor;

            if (entry.Kind == MessageKind.Event)
            {
                GUILayout.Label($"[{entry.SubscriberCount}]", EditorStyles.miniLabel, GUILayout.Width(30));
            }
            else if (entry.Kind == MessageKind.Query && entry.ProcessingTimeMs > 0)
            {
                GUILayout.Label($"{entry.ProcessingTimeMs:F2}ms", EditorStyles.miniLabel, GUILayout.Width(50));
            }
            else
            {
                GUILayout.Space(30);
            }

            EditorGUILayout.EndHorizontal();
        }

        private GUIStyle GetKindStyle(MessageKind kind)
        {
            return kind switch
            {
                MessageKind.Event => _eventStyle,
                MessageKind.Command => _commandStyle,
                MessageKind.Query => _queryStyle,
                _ => EditorStyles.miniLabel
            };
        }

        private string GetKindLabel(MessageKind kind)
        {
            return kind switch
            {
                MessageKind.Event => "EVT",
                MessageKind.Command => "CMD",
                MessageKind.Query => "QRY",
                _ => "???"
            };
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
                    _messageListWidth = Mathf.Clamp(Event.current.mousePosition.x, MinMessageListWidth, MaxMessageListWidth);
                    Repaint();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    _isResizing = false;
                }
            }

            EditorGUI.DrawRect(resizeRect, _resizeHandleColor);
        }

        private void DrawMessageDetailsPanel()
        {
            EditorGUILayout.BeginVertical();

            EditorGUILayout.LabelField("Message Details", _headerStyle);

            if (_selectedMessageIndex < 0 || _selectedMessageIndex >= _displayedEntries.Count)
            {
                EditorGUILayout.HelpBox("Select a message from the list to view its details.", MessageType.Info);
            }
            else
            {
                var entry = _displayedEntries[_selectedMessageIndex];
                DrawMessageDetails(entry);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawMessageDetails(MessageLogEntry entry)
        {
            _detailScrollPosition = EditorGUILayout.BeginScrollView(_detailScrollPosition);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawDetailRow("Type:", entry.MessageType?.FullName ?? "Unknown");
            DrawDetailRow("Kind:", entry.Kind.ToString(), GetKindStyle(entry.Kind));
            DrawDetailRow("Timestamp:", entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));

            if (entry.Kind == MessageKind.Event)
            {
                DrawDetailRow("Subscribers:", entry.SubscriberCount.ToString());
            }

            if (entry.Kind == MessageKind.Command)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Has Handler:", EditorStyles.boldLabel, GUILayout.Width(80));
                if (entry.HasHandler)
                {
                    EditorGUILayout.LabelField("Yes", EditorStyles.label);
                }
                else
                {
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = _warningColor;
                    EditorGUILayout.LabelField("\u26A0 No - Command will not be processed!", EditorStyles.boldLabel);
                    GUI.contentColor = prevColor;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (entry.Kind == MessageKind.Query && entry.ProcessingTimeMs > 0)
            {
                DrawDetailRow("Processing:", $"{entry.ProcessingTimeMs:F3} ms");
            }

            // Breakpoint status
            var typeName = entry.MessageType?.Name;
            if (typeName != null && _breakpoints.Contains(typeName))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Breakpoint:", EditorStyles.boldLabel, GUILayout.Width(80));
                var prevContentColor = GUI.contentColor;
                GUI.contentColor = _breakpointColor;
                EditorGUILayout.LabelField("\u25CF Active");
                GUI.contentColor = prevContentColor;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            EditorGUILayout.LabelField("Payload", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (entry.Payload != null)
            {
                DrawPayloadFields(entry.Payload);
            }
            else
            {
                EditorGUILayout.LabelField("(null)", EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndVertical();

            // Message chain tracking
            GUILayout.Space(10);
            DrawTriggeredMessages(entry);

            EditorGUILayout.EndScrollView();
        }

        private static void DrawDetailRow(string label, string value)
        {
            DrawDetailRow(label, value, EditorStyles.label);
        }

        private static void DrawDetailRow(string label, string value, GUIStyle valueStyle)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(80));
            EditorGUILayout.LabelField(value, valueStyle);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws triggered messages section - messages that arrived within 1ms
        /// of the selected message, implying a causal chain.
        /// </summary>
        private void DrawTriggeredMessages(MessageLogEntry selectedEntry)
        {
            var triggeredMessages = new List<MessageLogEntry>();
            var selectedIndex = _displayedEntries.IndexOf(selectedEntry);
            if (selectedIndex < 0) return;

            // Look at subsequent messages within the chain threshold
            for (int i = selectedIndex + 1; i < _displayedEntries.Count; i++)
            {
                var candidate = _displayedEntries[i];
                var timeDiffMs = (candidate.Timestamp - selectedEntry.Timestamp).TotalMilliseconds;

                if (timeDiffMs <= MessageChainThresholdMs && timeDiffMs >= 0)
                {
                    triggeredMessages.Add(candidate);
                }
                else
                {
                    break;
                }
            }

            if (triggeredMessages.Count == 0) return;

            EditorGUILayout.LabelField("Triggered Messages", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Messages arriving within {MessageChainThresholdMs}ms (possible causal chain):", EditorStyles.miniLabel);

            GUILayout.Space(3);

            foreach (var triggered in triggeredMessages)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("\u21B3", GUILayout.Width(15));

                var trigKindStyle = GetKindStyle(triggered.Kind);
                var trigKindLabel = GetKindLabel(triggered.Kind);
                GUILayout.Label(trigKindLabel, trigKindStyle, GUILayout.Width(35));

                var trigTypeName = triggered.MessageType?.Name ?? "Unknown";
                var timeDiff = (triggered.Timestamp - selectedEntry.Timestamp).TotalMilliseconds;
                GUILayout.Label($"{trigTypeName} (+{timeDiff:F3}ms)", EditorStyles.label);

                GUILayout.FlexibleSpace();

                // Allow clicking to select the triggered message
                var trigIndex = _displayedEntries.IndexOf(triggered);
                if (trigIndex >= 0 && GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    _selectedMessageIndex = trigIndex;
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPayloadFields(object payload)
        {
            var type = payload.GetType();
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (fields.Length == 0)
            {
                EditorGUILayout.LabelField("(no public fields)", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            foreach (var field in fields)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(field.Name, GUILayout.Width(150));

                var value = field.GetValue(payload);
                var valueStr = FormatValue(value);
                EditorGUILayout.SelectableLabel(valueStr, EditorStyles.textField, GUILayout.Height(18));

                EditorGUILayout.EndHorizontal();
            }
        }

        private string FormatValue(object value)
        {
            if (value == null) return "(null)";

            var type = value.GetType();

            if (type == typeof(Vector2)) return ((Vector2)value).ToString("F3");
            if (type == typeof(Vector3)) return ((Vector3)value).ToString("F3");
            if (type == typeof(Vector4)) return ((Vector4)value).ToString("F3");
            if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value;
                return $"({q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3})";
            }
            if (type == typeof(Color)) return ((Color)value).ToString();
            if (type == typeof(float)) return ((float)value).ToString("F4");
            if (type == typeof(double)) return ((double)value).ToString("F4");

            return value.ToString();
        }

        // =====================================================================
        // Statistics Panel
        // =====================================================================

        /// <summary>
        /// Draws the Statistics tab showing message throughput, frequency,
        /// processing times, and kind distribution.
        /// </summary>
        private void DrawStatisticsPanel()
        {
            _statisticsScrollPosition = EditorGUILayout.BeginScrollView(_statisticsScrollPosition);

            EditorGUILayout.BeginVertical();

            if (_displayedEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("No messages to analyze. Start logging and generate some messages.", MessageType.Info);
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndScrollView();
                return;
            }

            // Messages per second (rolling average over last 5 seconds)
            DrawMessagesPerSecond();

            GUILayout.Space(10);

            // Most frequent message types (top 10)
            DrawMostFrequentTypes();

            GUILayout.Space(10);

            // Average processing time per query type
            DrawAverageQueryProcessingTimes();

            GUILayout.Space(10);

            // Message kind distribution
            DrawKindDistribution();

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
        }

        private void DrawMessagesPerSecond()
        {
            EditorGUILayout.LabelField("Messages Per Second", _headerStyle);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var now = DateTime.Now;
            var windowStart = now.AddSeconds(-StatisticsWindowSeconds);
            var recentCount = 0;

            for (int i = _displayedEntries.Count - 1; i >= 0; i--)
            {
                if (_displayedEntries[i].Timestamp >= windowStart)
                    recentCount++;
                else
                    break;
            }

            var messagesPerSecond = recentCount / StatisticsWindowSeconds;
            EditorGUILayout.LabelField($"Rolling average ({StatisticsWindowSeconds:F0}s window): {messagesPerSecond:F1} msg/s");
            EditorGUILayout.LabelField($"Total messages: {_displayedEntries.Count}");

            if (_displayedEntries.Count >= 2)
            {
                var totalSpan = (_displayedEntries[_displayedEntries.Count - 1].Timestamp - _displayedEntries[0].Timestamp).TotalSeconds;
                if (totalSpan > 0)
                {
                    var overallRate = _displayedEntries.Count / totalSpan;
                    EditorGUILayout.LabelField($"Overall average: {overallRate:F1} msg/s");
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawMostFrequentTypes()
        {
            EditorGUILayout.LabelField("Most Frequent Message Types (Top 10)", _headerStyle);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var typeCounts = new Dictionary<string, int>();
            foreach (var entry in _displayedEntries)
            {
                var name = entry.MessageType?.Name ?? "Unknown";
                if (typeCounts.ContainsKey(name))
                    typeCounts[name]++;
                else
                    typeCounts[name] = 1;
            }

            var topTypes = typeCounts
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .ToList();

            if (topTypes.Count == 0)
            {
                EditorGUILayout.LabelField("(no data)", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                var maxCount = topTypes[0].Value;

                foreach (var kv in topTypes)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(kv.Key, GUILayout.Width(200));
                    EditorGUILayout.LabelField(kv.Value.ToString(), GUILayout.Width(50));

                    // Draw a proportional bar
                    var barRect = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
                    if (Event.current.type == EventType.Repaint)
                    {
                        var fillFraction = maxCount > 0 ? (float)kv.Value / maxCount : 0f;
                        var fillRect = new Rect(barRect.x, barRect.y + 2, barRect.width * fillFraction, barRect.height - 4);
                        EditorGUI.DrawRect(fillRect, _commandColor);
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAverageQueryProcessingTimes()
        {
            EditorGUILayout.LabelField("Average Processing Time Per Query Type", _headerStyle);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var queryTimes = new Dictionary<string, List<double>>();
            foreach (var entry in _displayedEntries)
            {
                if (entry.Kind != MessageKind.Query || entry.ProcessingTimeMs <= 0) continue;

                var name = entry.MessageType?.Name ?? "Unknown";
                if (!queryTimes.ContainsKey(name))
                    queryTimes[name] = new List<double>();
                queryTimes[name].Add(entry.ProcessingTimeMs);
            }

            if (queryTimes.Count == 0)
            {
                EditorGUILayout.LabelField("(no query data)", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                var queryStats = queryTimes
                    .Select(kv => new
                    {
                        Name = kv.Key,
                        Count = kv.Value.Count,
                        Avg = kv.Value.Average(),
                        Min = kv.Value.Min(),
                        Max = kv.Value.Max()
                    })
                    .OrderByDescending(x => x.Avg)
                    .ToList();

                // Header row
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Query Type", EditorStyles.boldLabel, GUILayout.Width(200));
                EditorGUILayout.LabelField("Count", EditorStyles.boldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Avg (ms)", EditorStyles.boldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField("Min (ms)", EditorStyles.boldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField("Max (ms)", EditorStyles.boldLabel, GUILayout.Width(70));
                EditorGUILayout.EndHorizontal();

                foreach (var stat in queryStats)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(stat.Name, GUILayout.Width(200));
                    EditorGUILayout.LabelField(stat.Count.ToString(), GUILayout.Width(50));
                    EditorGUILayout.LabelField($"{stat.Avg:F3}", GUILayout.Width(70));
                    EditorGUILayout.LabelField($"{stat.Min:F3}", GUILayout.Width(70));
                    EditorGUILayout.LabelField($"{stat.Max:F3}", GUILayout.Width(70));
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawKindDistribution()
        {
            EditorGUILayout.LabelField("Message Kind Distribution", _headerStyle);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var totalCount = _displayedEntries.Count;
            if (totalCount == 0)
            {
                EditorGUILayout.LabelField("(no data)", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            var eventCount = _displayedEntries.Count(e => e.Kind == MessageKind.Event);
            var commandCount = _displayedEntries.Count(e => e.Kind == MessageKind.Command);
            var queryCount = _displayedEntries.Count(e => e.Kind == MessageKind.Query);

            float eventPct = (float)eventCount / totalCount;
            float commandPct = (float)commandCount / totalCount;
            float queryPct = (float)queryCount / totalCount;

            // Draw colored bar (pie chart approximation)
            var barRect = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                var eventRect = new Rect(barRect.x, barRect.y, barRect.width * eventPct, barRect.height);
                var commandRect = new Rect(barRect.x + barRect.width * eventPct, barRect.y, barRect.width * commandPct, barRect.height);
                var queryRect = new Rect(barRect.x + barRect.width * (eventPct + commandPct), barRect.y, barRect.width * queryPct, barRect.height);

                EditorGUI.DrawRect(eventRect, _eventColor);
                EditorGUI.DrawRect(commandRect, _commandColor);
                EditorGUI.DrawRect(queryRect, _queryColor);
            }

            GUILayout.Space(5);

            // Labels
            EditorGUILayout.BeginHorizontal();

            var prevContentColor = GUI.contentColor;

            GUI.contentColor = _eventColor;
            EditorGUILayout.LabelField($"Events: {eventCount} ({eventPct:P1})", GUILayout.Width(150));

            GUI.contentColor = _commandColor;
            EditorGUILayout.LabelField($"Commands: {commandCount} ({commandPct:P1})", GUILayout.Width(160));

            GUI.contentColor = _queryColor;
            EditorGUILayout.LabelField($"Queries: {queryCount} ({queryPct:P1})", GUILayout.Width(150));

            GUI.contentColor = prevContentColor;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // =====================================================================
        // Export
        // =====================================================================

        /// <summary>
        /// Exports the current displayed log entries to a JSON file.
        /// Uses EditorUtility.SaveFilePanel to let the user choose the file path.
        /// </summary>
        private void ExportLogToJson()
        {
            if (_displayedEntries.Count == 0)
            {
                EditorUtility.DisplayDialog("Export Log", "No messages to export.", "OK");
                return;
            }

            var path = EditorUtility.SaveFilePanel(
                "Export Bus Log",
                "",
                "bus_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json",
                "json");

            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"exportTimestamp\": \"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}\",");
                sb.AppendLine($"  \"messageCount\": {_displayedEntries.Count},");
                sb.AppendLine("  \"messages\": [");

                for (int i = 0; i < _displayedEntries.Count; i++)
                {
                    var entry = _displayedEntries[i];
                    var comma = i < _displayedEntries.Count - 1 ? "," : "";
                    var typeName = entry.MessageType?.FullName ?? "Unknown";
                    var payloadJson = SerializePayload(entry.Payload);
                    var isBookmarked = IsBookmarked(entry);
                    var hasBreakpoint = entry.MessageType?.Name != null && _breakpoints.Contains(entry.MessageType.Name);

                    sb.AppendLine("    {");
                    sb.AppendLine($"      \"timestamp\": \"{entry.Timestamp:yyyy-MM-ddTHH:mm:ss.fff}\",");
                    sb.AppendLine($"      \"kind\": \"{entry.Kind}\",");
                    sb.AppendLine($"      \"messageType\": \"{EscapeJsonString(typeName)}\",");
                    sb.AppendLine($"      \"subscriberCount\": {entry.SubscriberCount},");
                    sb.AppendLine($"      \"hasHandler\": {(entry.HasHandler ? "true" : "false")},");
                    sb.AppendLine($"      \"processingTimeMs\": {entry.ProcessingTimeMs:F4},");
                    sb.AppendLine($"      \"bookmarked\": {(isBookmarked ? "true" : "false")},");
                    sb.AppendLine($"      \"breakpoint\": {(hasBreakpoint ? "true" : "false")},");
                    sb.AppendLine($"      \"payload\": {payloadJson}");
                    sb.AppendLine($"    }}{comma}");
                }

                sb.AppendLine("  ]");
                sb.AppendLine("}");

                System.IO.File.WriteAllText(path, sb.ToString());
                Debug.Log($"[BusDebugger] Log exported to: {path}");
                EditorUtility.DisplayDialog("Export Log", $"Successfully exported {_displayedEntries.Count} messages to:\n{path}", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BusDebugger] Failed to export log: {ex.Message}");
                EditorUtility.DisplayDialog("Export Error", $"Failed to export log:\n{ex.Message}", "OK");
            }
        }

        private string SerializePayload(object payload)
        {
            if (payload == null) return "null";

            var type = payload.GetType();
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (fields.Length == 0) return "{}";

            var sb = new StringBuilder();
            sb.Append("{ ");

            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var value = field.GetValue(payload);
                var valueStr = FormatValue(value);

                sb.Append($"\"{EscapeJsonString(field.Name)}\": \"{EscapeJsonString(valueStr)}\"");
                if (i < fields.Length - 1) sb.Append(", ");
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            return str
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        // =====================================================================
        // Bookmarking
        // =====================================================================

        /// <summary>
        /// Checks whether a specific entry is bookmarked.
        /// Uses the entry reference for identity tracking.
        /// </summary>
        private bool IsBookmarked(MessageLogEntry entry)
        {
            return _bookmarkedEntries.TryGetValue(entry, out var bookmarked) && bookmarked;
        }

        /// <summary>
        /// Toggles the bookmark state for a message entry.
        /// </summary>
        private void ToggleBookmark(int index, MessageLogEntry entry)
        {
            // Un-bookmarking removes the key rather than storing false: the key holds the
            // entry and its boxed payload alive, and a false entry is indistinguishable from
            // an absent one everywhere else.
            if (_bookmarkedEntries.ContainsKey(entry))
            {
                _bookmarkedEntries.Remove(entry);
            }
            else
            {
                _bookmarkedEntries[entry] = true;
            }
        }

        // =====================================================================
        // Data Refresh & Filtering
        // =====================================================================

        /// <summary>
        /// Refreshes the displayed entries based on current filter settings.
        /// </summary>
        private void RefreshDisplayedEntries()
        {
            var filter = BuildMessageFilter();
            _busDataProvider.GetLogEntriesNonAlloc(_displayedEntries, filter);
            PruneEvictedBookmarks();
        }

        /// <summary>
        /// Drops bookmarks whose entry the provider has already evicted from its ring buffer.
        /// Without this every bookmark keeps a MessageLogEntry — and the boxed message payload
        /// it points at — alive for the lifetime of the window.
        /// </summary>
        private void PruneEvictedBookmarks()
        {
            if (_bookmarkedEntries.Count == 0 || _displayedEntries.Count == 0) return;

            // Only safe while the displayed list is the whole log. With a filter applied the
            // missing entries are merely hidden, and pruning would silently discard bookmarks.
            if (!string.IsNullOrEmpty(_typeFilterPattern) || _kindFilter.HasValue) return;

            // The provider only ever trims from the front, so anything older than the oldest
            // surviving entry is gone for good.
            var oldestRetained = _displayedEntries[0].Timestamp;

            _staleBookmarks.Clear();
            foreach (var kvp in _bookmarkedEntries)
            {
                if (kvp.Key.Timestamp < oldestRetained)
                {
                    _staleBookmarks.Add(kvp.Key);
                }
            }

            for (int i = 0; i < _staleBookmarks.Count; i++)
            {
                _bookmarkedEntries.Remove(_staleBookmarks[i]);
            }

            _staleBookmarks.Clear();
        }

        /// <summary>
        /// Builds a MessageFilter from the current UI filter settings.
        /// </summary>
        private MessageFilter BuildMessageFilter()
        {
            return new MessageFilter
            {
                TypePattern = string.IsNullOrEmpty(_typeFilterPattern) ? null : _typeFilterPattern,
                Kind = _kindFilter,
                MaxResults = MaxLogEntries
            };
        }

        /// <summary>
        /// Checks if a message type matches the filter pattern.
        /// Supports wildcards (* for any characters) and partial matches.
        /// </summary>
        /// <remarks>
        /// Forwards to <see cref="BusDataProvider.MatchesTypePattern"/>, which is what the
        /// window's filter actually runs through. This used to be a second implementation
        /// that anchored every pattern as an exact match, so it disagreed with the shipped
        /// path for patterns without wildcards.
        /// </remarks>
        /// <param name="typeName">The type name to check.</param>
        /// <param name="pattern">The filter pattern.</param>
        /// <returns>True if the type matches the pattern.</returns>
        internal static bool MatchesTypePattern(string typeName, string pattern)
        {
            return BusDataProvider.MatchesTypePattern(typeName, pattern);
        }

        /// <summary>
        /// Clears all logged messages.
        /// </summary>
        private void ClearLog()
        {
            _busDataProvider.ClearLog();
            _displayedEntries.Clear();
            _selectedMessageIndex = -1;
            _bookmarkedEntries.Clear();
            Repaint();
        }

        /// <summary>
        /// Gets the current displayed entries count.
        /// Exposed for testing purposes.
        /// </summary>
        internal int DisplayedEntriesCount => _displayedEntries.Count;

        /// <summary>
        /// Gets whether the debugger is currently paused.
        /// </summary>
        internal bool IsPaused => _isPaused;

        /// <summary>
        /// Gets the current type filter pattern.
        /// </summary>
        internal string TypeFilterPattern => _typeFilterPattern;

        /// <summary>
        /// Gets the current kind filter.
        /// </summary>
        internal MessageKind? KindFilter => _kindFilter;

        /// <summary>
        /// Gets the breakpoints set (by type name).
        /// </summary>
        internal IReadOnlyCollection<string> Breakpoints => _breakpoints;

        /// <summary>
        /// Gets whether the bookmarked-only filter is active.
        /// </summary>
        internal bool ShowBookmarkedOnly => _showBookmarkedOnly;

        /// <summary>
        /// Sets the type filter pattern programmatically.
        /// </summary>
        internal void SetTypeFilter(string pattern)
        {
            _typeFilterPattern = pattern ?? "";
            RefreshDisplayedEntries();
        }

        /// <summary>
        /// Sets the kind filter programmatically.
        /// </summary>
        internal void SetKindFilter(MessageKind? kind)
        {
            _kindFilter = kind;
            RefreshDisplayedEntries();
        }

        /// <summary>
        /// Adds a breakpoint for the specified message type name.
        /// </summary>
        internal void AddBreakpoint(string typeName)
        {
            if (!string.IsNullOrEmpty(typeName))
                _breakpoints.Add(typeName);
        }

        /// <summary>
        /// Removes a breakpoint for the specified message type name.
        /// </summary>
        internal void RemoveBreakpoint(string typeName)
        {
            _breakpoints.Remove(typeName);
        }

        /// <summary>
        /// Toggles a bookmark on the entry at the given index.
        /// </summary>
        internal void ToggleBookmarkAt(int index)
        {
            if (index >= 0 && index < _displayedEntries.Count)
            {
                ToggleBookmark(index, _displayedEntries[index]);
            }
        }

        /// <summary>
        /// Checks whether an entry at the given index is bookmarked.
        /// </summary>
        internal bool IsBookmarkedAt(int index)
        {
            if (index >= 0 && index < _displayedEntries.Count)
            {
                return IsBookmarked(_displayedEntries[index]);
            }
            return false;
        }

        /// <summary>
        /// Gets the displayed entries for testing.
        /// </summary>
        internal IReadOnlyList<MessageLogEntry> GetDisplayedEntries() => _displayedEntries;
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using Strada.Core.Editor.DataProviders;
using Strada.Core.Logging;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using StradaLogType = Strada.Core.Logging.LogType;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Enterprise-grade log viewer window with advanced filtering, virtual scrolling,
    /// and professional UI for debugging Strada applications.
    /// </summary>
    public class StradaLogWindow : EditorWindow, IHasCustomMenu
    {
        private const float ToolbarHeight = 24f;
        private const float FilterPanelWidth = 200f;
        private const float StatusBarHeight = 22f;
        private const float MinDetailsPanelHeight = 100f;
        private const float MaxDetailsPanelHeight = 400f;
        private const float RowHeight = 22f;
        private const float IconSize = 16f;
        private const float ModuleBadgeWidth = 80f;
        private const float TimestampWidth = 85f;
        private const int VirtualScrollBuffer = 5;

        private static class Styles
        {
            public static GUIStyle ToolbarStyle;
            public static GUIStyle ToolbarButtonStyle;
            public static GUIStyle ToolbarSearchFieldStyle;
            public static GUIStyle ToolbarSearchCancelStyle;
            public static GUIStyle FilterPanelStyle;
            public static GUIStyle FilterHeaderStyle;
            public static GUIStyle FilterToggleStyle;
            public static GUIStyle LogRowStyle;
            public static GUIStyle LogRowAltStyle;
            public static GUIStyle LogRowSelectedStyle;
            public static GUIStyle TimestampStyle;
            public static GUIStyle ModuleBadgeStyle;
            public static GUIStyle MessageStyle;
            public static GUIStyle DetailsPanelStyle;
            public static GUIStyle DetailsHeaderStyle;
            public static GUIStyle DetailsLabelStyle;
            public static GUIStyle DetailsValueStyle;
            public static GUIStyle StackTraceStyle;
            public static GUIStyle StackTraceLinkStyle;
            public static GUIStyle StatusBarStyle;
            public static GUIStyle CountBadgeStyle;
            public static GUIStyle SplitterStyle;

            public static bool Initialized;

            public static void Initialize()
            {
                if (Initialized) return;

                ToolbarStyle = new GUIStyle(EditorStyles.toolbar)
                {
                    fixedHeight = ToolbarHeight,
                    padding = new RectOffset(8, 8, 0, 0)
                };

                ToolbarButtonStyle = new GUIStyle(EditorStyles.toolbarButton)
                {
                    padding = new RectOffset(8, 8, 2, 2),
                    fixedHeight = 20
                };

                ToolbarSearchFieldStyle = new GUIStyle("ToolbarSearchTextField")
                {
                    fixedHeight = 18,
                    margin = new RectOffset(4, 4, 3, 0)
                };

                ToolbarSearchCancelStyle = new GUIStyle("ToolbarSearchCancelButton");

                FilterPanelStyle = new GUIStyle
                {
                    padding = new RectOffset(8, 8, 8, 8)
                };

                FilterHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 11,
                    margin = new RectOffset(0, 0, 8, 4)
                };

                FilterToggleStyle = new GUIStyle(EditorStyles.toggle)
                {
                    margin = new RectOffset(0, 0, 2, 2),
                    padding = new RectOffset(18, 0, 0, 0)
                };

                LogRowStyle = new GUIStyle
                {
                    fixedHeight = RowHeight,
                    padding = new RectOffset(4, 4, 2, 2),
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.82f, 0.82f, 0.82f) : Color.black }
                };

                LogRowAltStyle = new GUIStyle(LogRowStyle);

                LogRowSelectedStyle = new GUIStyle(LogRowStyle)
                {
                    normal = { background = MakeTexture(new Color(0.24f, 0.49f, 0.91f, 0.6f)) }
                };

                TimestampStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.4f, 0.4f, 0.4f) },
                    fontSize = 10
                };

                ModuleBadgeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 9,
                    normal = { textColor = Color.white },
                    padding = new RectOffset(4, 4, 1, 1)
                };

                MessageStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip,
                    richText = false
                };

                DetailsPanelStyle = new GUIStyle
                {
                    padding = new RectOffset(12, 12, 12, 12)
                };

                DetailsHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 13,
                    margin = new RectOffset(0, 0, 0, 8)
                };

                DetailsLabelStyle = new GUIStyle(EditorStyles.label)
                {
                    fontStyle = FontStyle.Bold,
                    fixedWidth = 70
                };

                DetailsValueStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true
                };

                StackTraceStyle = new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = false,
                    richText = false,
                    font = GetMonoFont(),
                    fontSize = 11,
                    padding = new RectOffset(8, 8, 8, 8)
                };

                StackTraceLinkStyle = new GUIStyle(EditorStyles.linkLabel)
                {
                    font = GetMonoFont(),
                    fontSize = 11
                };

                StatusBarStyle = new GUIStyle
                {
                    fixedHeight = StatusBarHeight,
                    padding = new RectOffset(8, 8, 4, 4),
                    normal = { background = MakeTexture(EditorGUIUtility.isProSkin ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.76f, 0.76f, 0.76f)) }
                };

                CountBadgeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 9,
                    normal = { textColor = Color.white },
                    padding = new RectOffset(4, 4, 1, 1)
                };

                SplitterStyle = new GUIStyle
                {
                    fixedHeight = 4,
                    normal = { background = MakeTexture(EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.6f, 0.6f, 0.6f)) }
                };

                Initialized = true;
            }

            private static Font GetMonoFont()
            {
                var fonts = Font.GetOSInstalledFontNames();
                for (int i = 0; i < fonts.Length; i++)
                {
                    if (fonts[i].Contains("Consolas") || fonts[i].Contains("Monaco") || fonts[i].Contains("Menlo"))
                        return Font.CreateDynamicFontFromOSFont(fonts[i], 11);
                }
                return EditorStyles.label.font;
            }

            private static Texture2D MakeTexture(Color color)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, color);
                tex.Apply();
                tex.hideFlags = HideFlags.HideAndDontSave;
                return tex;
            }
        }

        private StradaLogDataProvider _dataProvider;
        private List<LogEntry> _filteredEntries = new List<LogEntry>();
        private List<LogEntry> _allEntries = new List<LogEntry>();
        private LogEntry _selectedEntry;
        private int _selectedIndex = -1;

        private HashSet<LogModule> _enabledModules = new HashSet<LogModule>();
        private HashSet<StradaLogType> _enabledTypes = new HashSet<StradaLogType>();
        private string _searchText = "";
        private bool _showDeepLogs = true;
        private bool _autoScroll = true;
        private bool _showTimestamp = true;
        private bool _showFilterPanel = true;
        private bool _collapseRepeated;
        private bool _regexSearch;

        private Vector2 _logListScroll;
        private Vector2 _detailsScroll;
        private Vector2 _filterScroll;
        private float _detailsPanelHeight = 180f;
        private bool _isResizingDetails;
        private float _resizeStartY;
        private float _resizeStartHeight;

        private double _lastClickTime;
        private int _lastClickedIndex = -1;
        private const double DoubleClickTime = 0.3;

        private Texture2D _infoIcon;
        private Texture2D _warningIcon;
        private Texture2D _errorIcon;
        private Texture2D _deepIcon;

        private Dictionary<LogModule, int> _moduleCounts = new Dictionary<LogModule, int>();
        private Dictionary<StradaLogType, int> _typeCounts = new Dictionary<StradaLogType, int>();
        private int _deepLogCount;

        private SearchField _searchField;
        private double _lastRepaintTime;
        private bool _needsRepaint;

        // Compiled once per distinct (pattern, regex-mode) pair; recompiling per entry on a
        // live log stream is far more expensive than the match itself.
        private System.Text.RegularExpressions.Regex _searchRegex;
        private string _searchRegexPattern;
        private bool _searchRegexInvalid;

        private static readonly Color InfoRowColor = new Color(0.22f, 0.22f, 0.22f, 0.0f);
        private static readonly Color InfoRowAltColor = new Color(0.25f, 0.25f, 0.25f, 0.3f);
        private static readonly Color WarningRowColor = new Color(0.6f, 0.5f, 0.2f, 0.15f);
        private static readonly Color ErrorRowColor = new Color(0.6f, 0.2f, 0.2f, 0.2f);
        private static readonly Color DeepRowColor = new Color(0.2f, 0.35f, 0.6f, 0.15f);

        [MenuItem("Strada/Log Viewer %#l", priority = 100)]
        [MenuItem("Window/Strada/Log Viewer", priority = 100)]
        public static void ShowWindow()
        {
            var window = GetWindow<StradaLogWindow>();
            window.titleContent = new GUIContent("Strada Logs", EditorGUIUtility.IconContent("UnityEditor.ConsoleWindow").image);
            window.minSize = new Vector2(800, 500);
        }

        private void OnEnable()
        {
            _dataProvider = StradaLogDataProvider.Instance;
            _dataProvider.OnLogReceived += OnLogReceived;
            _dataProvider.OnLogCleared += OnLogCleared;

            _searchField = new SearchField();
            _searchField.downOrUpArrowKeyPressed += OnSearchFieldArrowKey;

            LoadIcons();
            InitializeFilters();
            RefreshLogs();

            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            if (_dataProvider != null)
            {
                _dataProvider.OnLogReceived -= OnLogReceived;
                _dataProvider.OnLogCleared -= OnLogCleared;
            }

            EditorApplication.update -= OnEditorUpdate;
        }

        private void LoadIcons()
        {
            _infoIcon = EditorGUIUtility.IconContent("console.infoicon.sml").image as Texture2D;
            _warningIcon = EditorGUIUtility.IconContent("console.warnicon.sml").image as Texture2D;
            _errorIcon = EditorGUIUtility.IconContent("console.erroricon.sml").image as Texture2D;
            _deepIcon = EditorGUIUtility.IconContent("d_UnityEditor.SceneHierarchyWindow").image as Texture2D;
        }

        private void InitializeFilters()
        {
            _enabledModules.Clear();
            _enabledTypes.Clear();

            LogModuleRegistry.EnsureInitialized();
            var allModules = LogModuleRegistry.GetAllModules();
            foreach (var module in allModules)
            {
                _enabledModules.Add(module);
            }

            _enabledTypes.Add(StradaLogType.Info);
            _enabledTypes.Add(StradaLogType.Warning);
            _enabledTypes.Add(StradaLogType.Error);
            _enabledTypes.Add(StradaLogType.Exception);
        }

        private void OnEditorUpdate()
        {
            if (_needsRepaint && EditorApplication.timeSinceStartup - _lastRepaintTime > 0.1)
            {
                _needsRepaint = false;
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void OnGUI()
        {
            Styles.Initialize();

            var toolbarRect = new Rect(0, 0, position.width, ToolbarHeight);
            var contentRect = new Rect(0, ToolbarHeight, position.width, position.height - ToolbarHeight - StatusBarHeight);
            var statusBarRect = new Rect(0, position.height - StatusBarHeight, position.width, StatusBarHeight);

            DrawToolbar(toolbarRect);
            DrawContent(contentRect);
            DrawStatusBar(statusBarRect);

            HandleKeyboardInput();
        }

        private void DrawToolbar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal(Styles.ToolbarStyle);

            if (GUILayout.Button(new GUIContent("Clear", "Clear all log entries (Ctrl+K)"), Styles.ToolbarButtonStyle, GUILayout.Width(50)))
            {
                ClearLogs();
            }

            GUILayout.Space(4);

            var pauseContent = _dataProvider.IsPaused
                ? new GUIContent("Resume", EditorGUIUtility.IconContent("PlayButton").image, "Resume log capture")
                : new GUIContent("Pause", EditorGUIUtility.IconContent("PauseButton").image, "Pause log capture");

            if (GUILayout.Button(pauseContent, Styles.ToolbarButtonStyle, GUILayout.Width(65)))
            {
                _dataProvider.IsPaused = !_dataProvider.IsPaused;
            }

            GUILayout.Space(12);

            DrawTypeToggle(StradaLogType.Info, _infoIcon, "Info");
            DrawTypeToggle(StradaLogType.Warning, _warningIcon, "Warnings");
            DrawTypeToggle(StradaLogType.Error, _errorIcon, "Errors");

            GUILayout.Space(8);

            var deepEnabled = StradaLogSettings.Instance.DeepLogsEnabled;
            GUI.enabled = deepEnabled;
            var deepActive = _showDeepLogs && deepEnabled;
            var newDeep = GUILayout.Toggle(deepActive, new GUIContent($" Deep ({_deepLogCount})", _deepIcon), Styles.ToolbarButtonStyle, GUILayout.Width(80));
            if (newDeep != deepActive && deepEnabled)
            {
                _showDeepLogs = newDeep;
                RefreshFilteredEntries();
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            var searchRect = GUILayoutUtility.GetRect(200, 18, GUILayout.MinWidth(150), GUILayout.MaxWidth(300));
            searchRect.y += 3;
            var newSearch = _searchField.OnToolbarGUI(searchRect, _searchText);
            if (newSearch != _searchText)
            {
                _searchText = newSearch;
                RefreshFilteredEntries();
            }

            GUILayout.Space(8);

            _autoScroll = GUILayout.Toggle(_autoScroll, new GUIContent("Auto", "Auto-scroll to latest"), Styles.ToolbarButtonStyle, GUILayout.Width(45));
            _showTimestamp = GUILayout.Toggle(_showTimestamp, new GUIContent("Time", "Show timestamps"), Styles.ToolbarButtonStyle, GUILayout.Width(45));
            _showFilterPanel = GUILayout.Toggle(_showFilterPanel, new GUIContent("Filters", "Show filter panel"), Styles.ToolbarButtonStyle, GUILayout.Width(55));

            GUILayout.Space(4);

            if (GUILayout.Button(new GUIContent("", EditorGUIUtility.IconContent("_Menu").image, "Options"), Styles.ToolbarButtonStyle, GUILayout.Width(24)))
            {
                ShowOptionsMenu();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawTypeToggle(StradaLogType type, Texture2D icon, string tooltip)
        {
            var isActive = _enabledTypes.Contains(type);
            int count = 0;
            _typeCounts.TryGetValue(type, out count);

            if (type == StradaLogType.Error)
            {
                int exceptionCount = 0;
                _typeCounts.TryGetValue(StradaLogType.Exception, out exceptionCount);
                count += exceptionCount;
            }

            var content = new GUIContent($" {count}", icon, tooltip);
            var newActive = GUILayout.Toggle(isActive, content, Styles.ToolbarButtonStyle, GUILayout.Width(55));

            if (newActive != isActive)
            {
                if (newActive)
                {
                    _enabledTypes.Add(type);
                    if (type == StradaLogType.Error)
                        _enabledTypes.Add(StradaLogType.Exception);
                }
                else
                {
                    _enabledTypes.Remove(type);
                    if (type == StradaLogType.Error)
                        _enabledTypes.Remove(StradaLogType.Exception);
                }
                RefreshFilteredEntries();
            }
        }

        private void DrawContent(Rect rect)
        {
            var filterWidth = _showFilterPanel ? FilterPanelWidth : 0;
            var logListRect = new Rect(rect.x + filterWidth, rect.y, rect.width - filterWidth, rect.height - _detailsPanelHeight - 4);
            var splitterRect = new Rect(rect.x + filterWidth, logListRect.yMax, rect.width - filterWidth, 4);
            var detailsRect = new Rect(rect.x + filterWidth, splitterRect.yMax, rect.width - filterWidth, _detailsPanelHeight);

            if (_showFilterPanel)
            {
                var filterRect = new Rect(rect.x, rect.y, FilterPanelWidth, rect.height);
                DrawFilterPanel(filterRect);

                var filterSplitterRect = new Rect(FilterPanelWidth - 1, rect.y, 1, rect.height);
                EditorGUI.DrawRect(filterSplitterRect, EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.6f, 0.6f, 0.6f));
            }

            DrawLogList(logListRect);
            DrawSplitter(splitterRect);
            DrawDetailsPanel(detailsRect);
        }

        private void DrawFilterPanel(Rect rect)
        {
            var bgColor = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.85f, 0.85f, 0.85f);
            EditorGUI.DrawRect(rect, bgColor);

            GUILayout.BeginArea(rect);
            _filterScroll = GUILayout.BeginScrollView(_filterScroll, Styles.FilterPanelStyle);

            GUILayout.Label("Log Types", Styles.FilterHeaderStyle);
            DrawFilterTypeToggle(StradaLogType.Info, "Info", _infoIcon);
            DrawFilterTypeToggle(StradaLogType.Warning, "Warning", _warningIcon);
            DrawFilterTypeToggle(StradaLogType.Error, "Error", _errorIcon);

            GUILayout.Space(12);

            GUILayout.Label("Modules", Styles.FilterHeaderStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(60)))
            {
                SelectAllModules();
            }
            if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(60)))
            {
                DeselectAllModules();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            DrawModuleTierSection("Core", LogModuleTier.StradaCore);
            DrawModuleTierSection("Strada", LogModuleTier.StradaModule);
            DrawModuleTierSection("Game", LogModuleTier.Game);

            GUILayout.Space(12);

            GUILayout.Label("Options", Styles.FilterHeaderStyle);

            var newCollapse = GUILayout.Toggle(_collapseRepeated, " Collapse Repeated", Styles.FilterToggleStyle);
            if (newCollapse != _collapseRepeated)
            {
                _collapseRepeated = newCollapse;
                RefreshFilteredEntries();
            }

            var newRegex = GUILayout.Toggle(_regexSearch, " Regex Search", Styles.FilterToggleStyle);
            if (newRegex != _regexSearch)
            {
                _regexSearch = newRegex;
                RefreshFilteredEntries();
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawFilterTypeToggle(StradaLogType type, string label, Texture2D icon)
        {
            var isActive = _enabledTypes.Contains(type);
            int count = 0;
            _typeCounts.TryGetValue(type, out count);

            if (type == StradaLogType.Error)
            {
                int exceptionCount = 0;
                _typeCounts.TryGetValue(StradaLogType.Exception, out exceptionCount);
                count += exceptionCount;
            }

            GUILayout.BeginHorizontal();
            var newActive = GUILayout.Toggle(isActive, new GUIContent($" {label}", icon), Styles.FilterToggleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"({count})", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            if (newActive != isActive)
            {
                if (newActive)
                {
                    _enabledTypes.Add(type);
                    if (type == StradaLogType.Error)
                        _enabledTypes.Add(StradaLogType.Exception);
                }
                else
                {
                    _enabledTypes.Remove(type);
                    if (type == StradaLogType.Error)
                        _enabledTypes.Remove(StradaLogType.Exception);
                }
                RefreshFilteredEntries();
            }
        }

        private void DrawModuleTierSection(string tierLabel, LogModuleTier tier)
        {
            var modules = LogModuleRegistry.GetModulesByTier(tier);
            if (modules.Count == 0) return;

            GUILayout.Space(4);
            GUILayout.Label(tierLabel, EditorStyles.miniBoldLabel);

            foreach (var module in modules)
            {
                DrawFilterModuleToggle(module);
            }
        }

        private void DrawFilterModuleToggle(LogModule module)
        {
            var isActive = _enabledModules.Contains(module);
            int count = 0;
            _moduleCounts.TryGetValue(module, out count);

            var color = StradaLogSettings.Instance.GetModuleColor(module);
            var displayName = LogModuleRegistry.GetDisplayName(module);

            GUILayout.BeginHorizontal();

            var colorRect = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12));
            colorRect.y += 4;
            colorRect.height = 10;
            EditorGUI.DrawRect(colorRect, color);

            var newActive = GUILayout.Toggle(isActive, $" {displayName}", Styles.FilterToggleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"({count})", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            if (newActive != isActive)
            {
                if (newActive)
                    _enabledModules.Add(module);
                else
                    _enabledModules.Remove(module);
                RefreshFilteredEntries();
            }
        }

        private void DrawLogList(Rect rect)
        {
            var bgColor = EditorGUIUtility.isProSkin ? new Color(0.18f, 0.18f, 0.18f) : new Color(0.92f, 0.92f, 0.92f);
            EditorGUI.DrawRect(rect, bgColor);

            if (_filteredEntries.Count == 0)
            {
                var msgRect = new Rect(rect.x, rect.y + rect.height / 2 - 20, rect.width, 40);
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 12 };
                GUI.Label(msgRect, "No log entries match the current filters", style);
                return;
            }

            var viewRect = new Rect(0, 0, rect.width - 16, _filteredEntries.Count * RowHeight);
            var scrollPos = _logListScroll;

            if (_autoScroll && _filteredEntries.Count > 0)
            {
                scrollPos.y = viewRect.height - rect.height + RowHeight;
            }

            _logListScroll = GUI.BeginScrollView(rect, scrollPos, viewRect);

            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_logListScroll.y / RowHeight) - VirtualScrollBuffer);
            int lastVisible = Mathf.Min(_filteredEntries.Count - 1, Mathf.CeilToInt((_logListScroll.y + rect.height) / RowHeight) + VirtualScrollBuffer);

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                var rowRect = new Rect(0, i * RowHeight, viewRect.width, RowHeight);
                DrawLogRow(rowRect, i, _filteredEntries[i]);
            }

            GUI.EndScrollView();
        }

        private void DrawLogRow(Rect rect, int index, LogEntry entry)
        {
            var isSelected = index == _selectedIndex;
            var isAlt = index % 2 == 1;

            Color bgColor;
            if (isSelected)
                bgColor = new Color(0.24f, 0.49f, 0.91f, 0.6f);
            else if (entry.IsDeepLog)
                bgColor = DeepRowColor;
            else if (entry.Type == StradaLogType.Error || entry.Type == StradaLogType.Exception)
                bgColor = ErrorRowColor;
            else if (entry.Type == StradaLogType.Warning)
                bgColor = WarningRowColor;
            else
                bgColor = isAlt ? InfoRowAltColor : InfoRowColor;

            EditorGUI.DrawRect(rect, bgColor);

            var x = rect.x + 4;

            if (_showTimestamp)
            {
                var timestampRect = new Rect(x, rect.y, TimestampWidth, rect.height);
                GUI.Label(timestampRect, entry.Timestamp.ToString("HH:mm:ss.fff"), Styles.TimestampStyle);
                x += TimestampWidth + 4;
            }

            var icon = GetLogTypeIcon(entry);
            var iconRect = new Rect(x, rect.y + (rect.height - IconSize) / 2, IconSize, IconSize);
            GUI.DrawTexture(iconRect, icon);
            x += IconSize + 6;

            var moduleColor = StradaLogSettings.Instance.GetModuleColor(entry.Module);
            var moduleDisplayName = LogModuleRegistry.GetDisplayName(entry.Module);
            var badgeRect = new Rect(x, rect.y + 3, ModuleBadgeWidth, rect.height - 6);
            EditorGUI.DrawRect(badgeRect, moduleColor);
            GUI.Label(badgeRect, moduleDisplayName, Styles.ModuleBadgeStyle);
            x += ModuleBadgeWidth + 8;

            if (entry.IsDeepLog)
            {
                var deepBadgeRect = new Rect(x, rect.y + 3, 36, rect.height - 6);
                EditorGUI.DrawRect(deepBadgeRect, new Color(0.2f, 0.4f, 0.7f, 0.8f));
                GUI.Label(deepBadgeRect, "DEEP", Styles.ModuleBadgeStyle);
                x += 40;
            }

            var messageRect = new Rect(x, rect.y, rect.width - x - 8, rect.height);
            var message = entry.Message;
            if (message.Length > 300)
                message = message.Substring(0, 300) + "...";
            message = message.Replace('\n', ' ').Replace('\r', ' ');
            GUI.Label(messageRect, message, Styles.MessageStyle);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                HandleLogRowClick(index, entry);
                Event.current.Use();
            }
        }

        private Texture2D GetLogTypeIcon(LogEntry entry)
        {
            if (entry.IsDeepLog) return _deepIcon ?? _infoIcon;

            switch (entry.Type)
            {
                case StradaLogType.Warning: return _warningIcon;
                case StradaLogType.Error:
                case StradaLogType.Exception: return _errorIcon;
                default: return _infoIcon;
            }
        }

        private void HandleLogRowClick(int index, LogEntry entry)
        {
            var currentTime = EditorApplication.timeSinceStartup;

            if (index == _lastClickedIndex && (currentTime - _lastClickTime) < DoubleClickTime)
            {
                StradaLogDataProvider.OpenSourceFile(entry);
                _lastClickedIndex = -1;
            }
            else
            {
                _selectedIndex = index;
                _selectedEntry = entry;
                _lastClickedIndex = index;
                _lastClickTime = currentTime;
            }

            Repaint();
        }

        private void DrawSplitter(Rect rect)
        {
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.6f, 0.6f, 0.6f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                _isResizingDetails = true;
                _resizeStartY = Event.current.mousePosition.y;
                _resizeStartHeight = _detailsPanelHeight;
                Event.current.Use();
            }

            if (_isResizingDetails)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    var delta = _resizeStartY - Event.current.mousePosition.y;
                    _detailsPanelHeight = Mathf.Clamp(_resizeStartHeight + delta, MinDetailsPanelHeight, MaxDetailsPanelHeight);
                    Repaint();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    _isResizingDetails = false;
                }
            }
        }

        private void DrawDetailsPanel(Rect rect)
        {
            var bgColor = EditorGUIUtility.isProSkin ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.88f, 0.88f, 0.88f);
            EditorGUI.DrawRect(rect, bgColor);

            GUILayout.BeginArea(rect);
            _detailsScroll = GUILayout.BeginScrollView(_detailsScroll, Styles.DetailsPanelStyle);

            if (_selectedEntry == null)
            {
                GUILayout.Label("Select a log entry to view details", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                DrawSelectedEntryDetails();
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawSelectedEntryDetails()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Log Details", Styles.DetailsHeaderStyle);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                CopySelectedToClipboard();
            }

            GUI.enabled = !string.IsNullOrEmpty(_selectedEntry.FilePath);
            if (GUILayout.Button("Open Source", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                StradaLogDataProvider.OpenSourceFile(_selectedEntry);
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            DrawDetailRow("Time", _selectedEntry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            DrawDetailRow("Module", LogModuleRegistry.GetDisplayName(_selectedEntry.Module));
            DrawDetailRow("Type", _selectedEntry.Type.ToString() + (_selectedEntry.IsDeepLog ? " (Deep)" : ""));

            if (!string.IsNullOrEmpty(_selectedEntry.FilePath))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Source", Styles.DetailsLabelStyle);
                var sourceText = $"{_selectedEntry.FilePath}:{_selectedEntry.LineNumber}";
                if (GUILayout.Button(sourceText, EditorStyles.linkLabel))
                {
                    StradaLogDataProvider.OpenSourceFile(_selectedEntry);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);

            GUILayout.Label("Message", Styles.DetailsLabelStyle);
            EditorGUILayout.SelectableLabel(_selectedEntry.Message, Styles.DetailsValueStyle, GUILayout.MinHeight(40));

            GUILayout.Space(8);

            GUILayout.Label("Stack Trace", Styles.DetailsLabelStyle);

            if (!string.IsNullOrEmpty(_selectedEntry.StackTrace))
            {
                DrawClickableStackTrace(_selectedEntry.StackTrace);
            }
            else
            {
                GUILayout.Label("(No stack trace available)", EditorStyles.miniLabel);
            }
        }

        private void DrawDetailRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Styles.DetailsLabelStyle);
            GUILayout.Label(value, Styles.DetailsValueStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawClickableStackTrace(string stackTrace)
        {
            var lines = stackTrace.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                int atIndex = line.IndexOf(" (at ", StringComparison.Ordinal);
                if (atIndex >= 0)
                {
                    GUILayout.BeginHorizontal();

                    var methodPart = line.Substring(0, atIndex);
                    GUILayout.Label(methodPart, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));

                    var locationStart = atIndex + 5;
                    var locationEnd = line.IndexOf(')', locationStart);
                    if (locationEnd > locationStart)
                    {
                        var location = line.Substring(locationStart, locationEnd - locationStart);
                        if (GUILayout.Button(location, Styles.StackTraceLinkStyle, GUILayout.ExpandWidth(false)))
                        {
                            OpenStackTraceLine(location);
                        }
                    }

                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.Label(line, EditorStyles.miniLabel);
                }
            }
        }

        private void OpenStackTraceLine(string location)
        {
            int colonIndex = location.LastIndexOf(':');
            if (colonIndex < 0) return;

            var filePath = location.Substring(0, colonIndex);
            var lineStr = location.Substring(colonIndex + 1);

            if (!int.TryParse(lineStr, out int lineNumber)) return;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (asset != null)
            {
                AssetDatabase.OpenAsset(asset, lineNumber);
            }
            else
            {
                var fullPath = System.IO.Path.GetFullPath(filePath);
                if (System.IO.File.Exists(fullPath))
                {
                    UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(fullPath, lineNumber);
                }
            }
        }

        private void DrawStatusBar(Rect rect)
        {
            EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.76f, 0.76f, 0.76f));

            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal();

            GUILayout.Space(8);

            var statusText = _dataProvider.IsPaused ? "PAUSED" : "Recording";
            var statusColor = _dataProvider.IsPaused ? new Color(0.9f, 0.6f, 0.2f) : new Color(0.4f, 0.8f, 0.4f);
            var prevColor = GUI.color;
            GUI.color = statusColor;
            GUILayout.Label(statusText, EditorStyles.miniLabel, GUILayout.Width(60));
            GUI.color = prevColor;

            GUILayout.Label($"Showing {_filteredEntries.Count} of {_allEntries.Count} entries", EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            if (_selectedEntry != null)
            {
                GUILayout.Label($"Selected: {LogModuleRegistry.GetDisplayName(_selectedEntry.Module)} - {_selectedEntry.Type}", EditorStyles.miniLabel);
            }

            GUILayout.Space(8);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void HandleKeyboardInput()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.UpArrow && _selectedIndex > 0)
            {
                _selectedIndex--;
                _selectedEntry = _filteredEntries[_selectedIndex];
                _autoScroll = false;
                ScrollToSelected();
                e.Use();
            }
            else if (e.keyCode == KeyCode.DownArrow && _selectedIndex < _filteredEntries.Count - 1)
            {
                _selectedIndex++;
                _selectedEntry = _filteredEntries[_selectedIndex];
                _autoScroll = false;
                ScrollToSelected();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Return && _selectedEntry != null)
            {
                StradaLogDataProvider.OpenSourceFile(_selectedEntry);
                e.Use();
            }
            else if (e.keyCode == KeyCode.C && e.control && _selectedEntry != null)
            {
                CopySelectedToClipboard();
                e.Use();
            }
            else if (e.keyCode == KeyCode.K && e.control)
            {
                ClearLogs();
                e.Use();
            }
            else if (e.keyCode == KeyCode.F && e.control)
            {
                _searchField.SetFocus();
                e.Use();
            }
        }

        private void ScrollToSelected()
        {
            if (_selectedIndex < 0) return;

            var targetY = _selectedIndex * RowHeight;
            var viewHeight = position.height - ToolbarHeight - StatusBarHeight - _detailsPanelHeight - 4;

            if (targetY < _logListScroll.y)
                _logListScroll.y = targetY;
            else if (targetY + RowHeight > _logListScroll.y + viewHeight)
                _logListScroll.y = targetY + RowHeight - viewHeight;

            Repaint();
        }

        private void OnSearchFieldArrowKey()
        {
            if (_filteredEntries.Count > 0)
            {
                _selectedIndex = 0;
                _selectedEntry = _filteredEntries[0];
                Repaint();
            }
        }

        private void ShowOptionsMenu()
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Show Logs in Console"), StradaLogSettings.Instance.ShowLogs, () =>
            {
                StradaLogSettings.Instance.ShowLogs = !StradaLogSettings.Instance.ShowLogs;
            });

            menu.AddItem(new GUIContent("Enable Deep Logs"), StradaLogSettings.Instance.DeepLogsEnabled, () =>
            {
                StradaLogSettings.Instance.DeepLogsEnabled = !StradaLogSettings.Instance.DeepLogsEnabled;
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Export/Copy All to Clipboard"), false, ExportAllToClipboard);
            menu.AddItem(new GUIContent("Export/Save to File..."), false, ExportToFile);

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("Open Settings"), false, () =>
            {
                SettingsService.OpenProjectSettings("Project/Strada/Logging");
            });

            menu.ShowAsContext();
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Reset Filters"), false, () =>
            {
                InitializeFilters();
                _searchText = "";
                RefreshFilteredEntries();
            });
        }

        private void ClearLogs()
        {
            _dataProvider.Clear();
            StradaLog.Clear();
            _selectedEntry = null;
            _selectedIndex = -1;
            RefreshLogs();
        }

        private void CopySelectedToClipboard()
        {
            if (_selectedEntry == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"[{_selectedEntry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{_selectedEntry.Module}] [{_selectedEntry.Type}]");
            sb.AppendLine(_selectedEntry.Message);
            sb.AppendLine();
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(_selectedEntry.StackTrace);

            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void ExportAllToClipboard()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Strada Log Export - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total Entries: {_filteredEntries.Count}");
            sb.AppendLine(new string('-', 80));
            sb.AppendLine();

            for (int i = 0; i < _filteredEntries.Count; i++)
            {
                var entry = _filteredEntries[i];
                sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Module}] [{entry.Type}] {entry.Message}");
            }

            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void ExportToFile()
        {
            var path = EditorUtility.SaveFilePanel("Export Logs", "", $"strada_logs_{DateTime.Now:yyyyMMdd_HHmmss}", "txt");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Strada Log Export - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Total Entries: {_filteredEntries.Count}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            for (int i = 0; i < _filteredEntries.Count; i++)
            {
                var entry = _filteredEntries[i];
                sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Module}] [{entry.Type}]");
                sb.AppendLine(entry.Message);
                if (!string.IsNullOrEmpty(entry.StackTrace))
                {
                    sb.AppendLine("Stack Trace:");
                    sb.AppendLine(entry.StackTrace);
                }
                sb.AppendLine(new string('-', 40));
                sb.AppendLine();
            }

            System.IO.File.WriteAllText(path, sb.ToString());
            EditorUtility.RevealInFinder(path);
        }

        private void SelectAllModules()
        {
            _enabledModules.Clear();
            var allModules = LogModuleRegistry.GetAllModules();
            foreach (var module in allModules)
            {
                _enabledModules.Add(module);
            }
            RefreshFilteredEntries();
        }

        private void DeselectAllModules()
        {
            _enabledModules.Clear();
            RefreshFilteredEntries();
        }

        private void RefreshLogs()
        {
            _allEntries = _dataProvider.GetEntries();
            UpdateCounts();
            RefreshFilteredEntries();
        }

        private void RefreshFilteredEntries()
        {
            _filteredEntries.Clear();

            for (int i = 0; i < _allEntries.Count; i++)
            {
                var entry = _allEntries[i];

                if (!PassesFilter(entry))
                    continue;

                _filteredEntries.Add(entry);
            }

            if (_selectedEntry != null)
            {
                _selectedIndex = _filteredEntries.IndexOf(_selectedEntry);
                if (_selectedIndex < 0)
                    _selectedEntry = null;
            }

            Repaint();
        }

        /// <summary>
        /// Single definition of "this entry is visible under the current filters".
        /// Both the full refilter and the incremental path for newly arriving entries go
        /// through here; when they had separate copies the incremental one ignored regex
        /// mode entirely, so with Regex Search on no live log line was ever displayed.
        /// </summary>
        private bool PassesFilter(LogEntry entry)
        {
            if (!_enabledModules.Contains(entry.Module))
                return false;

            if (!_enabledTypes.Contains(entry.Type))
                return false;

            if (entry.IsDeepLog && !_showDeepLogs)
                return false;

            if (string.IsNullOrEmpty(_searchText))
                return true;

            if (!_regexSearch)
                return entry.Message.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;

            var regex = GetSearchRegex();
            if (regex == null)
                return false;

            try
            {
                return regex.IsMatch(entry.Message);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // A pathological pattern must not stall the editor; treat it as no match.
                return false;
            }
        }

        /// <summary>
        /// Returns the compiled search regex, rebuilding it only when the pattern changes.
        /// Returns null while the pattern is not valid.
        /// </summary>
        private System.Text.RegularExpressions.Regex GetSearchRegex()
        {
            if (_searchRegexPattern != _searchText)
            {
                _searchRegexPattern = _searchText;
                _searchRegexInvalid = false;
                try
                {
                    _searchRegex = new System.Text.RegularExpressions.Regex(
                        _searchText,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    _searchRegex = null;
                    _searchRegexInvalid = true;
                }
            }

            return _searchRegexInvalid ? null : _searchRegex;
        }

        private void UpdateCounts()
        {
            _moduleCounts.Clear();
            _typeCounts.Clear();
            _deepLogCount = 0;

            for (int i = 0; i < _allEntries.Count; i++)
            {
                var entry = _allEntries[i];

                if (_moduleCounts.ContainsKey(entry.Module))
                    _moduleCounts[entry.Module]++;
                else
                    _moduleCounts[entry.Module] = 1;

                if (_typeCounts.ContainsKey(entry.Type))
                    _typeCounts[entry.Type]++;
                else
                    _typeCounts[entry.Type] = 1;

                if (entry.IsDeepLog)
                    _deepLogCount++;
            }
        }

        private void OnLogReceived(LogEntry entry)
        {
            _allEntries.Add(entry);

            // Auto-enable newly discovered modules
            if (!_enabledModules.Contains(entry.Module) && LogModuleRegistry.IsRegistered(entry.Module))
            {
                _enabledModules.Add(entry.Module);
            }

            if (_moduleCounts.ContainsKey(entry.Module))
                _moduleCounts[entry.Module]++;
            else
                _moduleCounts[entry.Module] = 1;

            if (_typeCounts.ContainsKey(entry.Type))
                _typeCounts[entry.Type]++;
            else
                _typeCounts[entry.Type] = 1;

            if (entry.IsDeepLog)
                _deepLogCount++;

            if (PassesFilter(entry))
            {
                _filteredEntries.Add(entry);
            }

            TrimToMaxEntries();

            _needsRepaint = true;
        }

        /// <summary>
        /// Drops the oldest entries once the window holds more than the provider does.
        /// The provider evicts from its own list, but this window works on a copy taken at
        /// OnEnable and only ever appended to, so without this it grows without bound for
        /// the whole session.
        /// </summary>
        private void TrimToMaxEntries()
        {
            var maxEntries = StradaLogSettings.Instance.MaxLogEntries;
            if (maxEntries <= 0 || _allEntries.Count <= maxEntries)
                return;

            int excess = _allEntries.Count - maxEntries;

            // _filteredEntries is an ordered subset of _allEntries, so anything evicted from
            // the head of one is at the head of the other if it is there at all.
            int filteredRemoved = 0;
            for (int i = 0; i < excess; i++)
            {
                var evicted = _allEntries[i];
                if (filteredRemoved < _filteredEntries.Count &&
                    ReferenceEquals(_filteredEntries[filteredRemoved], evicted))
                {
                    filteredRemoved++;
                }
            }

            _allEntries.RemoveRange(0, excess);

            if (filteredRemoved > 0)
            {
                _filteredEntries.RemoveRange(0, filteredRemoved);

                if (_selectedEntry != null)
                {
                    _selectedIndex = _filteredEntries.IndexOf(_selectedEntry);
                    if (_selectedIndex < 0)
                        _selectedEntry = null;
                }
            }
        }

        private void OnLogCleared()
        {
            _allEntries.Clear();
            _filteredEntries.Clear();
            _selectedEntry = null;
            _selectedIndex = -1;
            _moduleCounts.Clear();
            _typeCounts.Clear();
            _deepLogCount = 0;
            Repaint();
        }
    }
}

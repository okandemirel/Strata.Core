using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Strada.Core.ECS;
using Strada.Core.ECS.World;
using Strada.Core.Editor.DataProviders.Models;
using Strada.Core.Editor.Profiling;
using UnityEditor;
using UnityEngine;
using UpdatePhase = Strada.Core.Editor.DataProviders.Models.UpdatePhase;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Editor window for profiling ECS system execution times.
    /// Provides real-time timing display, phase grouping, threshold highlighting,
    /// detailed metrics, sparkline charts, timeline view, alert system,
    /// summary statistics, sort options, and JSON export functionality.
    /// </summary>
    public class SystemProfilerWindow : EditorWindow
    {
        private float _warningThresholdMs = 1.0f;
        private float _criticalThresholdMs = 5.0f;

        private SystemProfiler _profiler;
        private bool _isRecording;
        private double _lastUpdateTime;
        private float _updateInterval = 0.1f;

        private Vector2 _scrollPosition;
        private Dictionary<UpdatePhase, bool> _phaseFoldouts;
        private Dictionary<Type, bool> _systemDetailFoldouts;
        private string _searchFilter = "";

        // Profiler reads are per-repaint hot paths: GetSamples copies a system's whole
        // 1000-entry buffer into a fresh List, and GetMetricsByPhase re-walks every sample of
        // every buffer twice. Both only change when new samples are collected, so results are
        // cached and the generation counter is bumped wherever the sample data changes.
        private int _dataGeneration;
        private Dictionary<UpdatePhase, List<SystemMetrics>> _metricsByPhaseCache;
        private int _metricsCacheGeneration = -1;
        private readonly Dictionary<Type, IReadOnlyList<SystemTimingSample>> _sampleCache =
            new Dictionary<Type, IReadOnlyList<SystemTimingSample>>();
        private int _sampleCacheGeneration = -1;

        private GUIStyle _headerStyle;
        private GUIStyle _phaseHeaderStyle;
        private GUIStyle _systemRowStyle;
        private GUIStyle _metricsStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _criticalStyle;
        private bool _stylesInitialized;

        private readonly Color _normalColor = new Color(0.7f, 0.9f, 0.7f);
        private readonly Color _warningColor = new Color(1.0f, 0.85f, 0.4f);
        private readonly Color _criticalColor = new Color(1.0f, 0.4f, 0.4f);
        private readonly Color _phaseHeaderColor = new Color(0.3f, 0.5f, 0.7f);

        // --- Sparkline constants ---
        private const float SparklineWidth = 100f;
        private const float SparklineHeight = 20f;
        private const int SparklineMaxSamples = 60;

        // --- Timeline view state ---
        private bool _showTimeline;
        private Vector2 _timelineScrollPosition;
        private const float TimelineTrackHeight = 24f;
        private const float TimelineHeaderWidth = 160f;
        private readonly Color _timelineBackgroundColor = new Color(0.15f, 0.15f, 0.15f);
        private readonly Color _timelineGridColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        private readonly Color _timelineBarNormalColor = new Color(0.4f, 0.7f, 0.4f, 0.9f);
        private readonly Color _timelineBarWarningColor = new Color(1.0f, 0.75f, 0.2f, 0.9f);
        private readonly Color _timelineBarCriticalColor = new Color(1.0f, 0.3f, 0.3f, 0.9f);

        // --- Alert system state ---
        private readonly List<AlertEntry> _activeAlerts = new List<AlertEntry>();
        private GUIStyle _alertBannerStyle;

        // --- Summary statistics state ---
        private bool _showSummary = true;

        // --- Sort options ---
        private SortMode _currentSortMode = SortMode.ExecutionTime;
        private readonly string[] _sortModeLabels = { "Name", "Execution Time", "Sample Count", "Phase" };

        private enum SortMode
        {
            Name,
            ExecutionTime,
            SampleCount,
            Phase
        }

        /// <summary>
        /// Represents an active alert for a system exceeding the critical threshold.
        /// </summary>
        private class AlertEntry
        {
            public string SystemName;
            public double ExecutionTimeMs;
            public double CreatedTime;
            public const double AutoDismissSeconds = 5.0;

            public bool IsExpired =>
                EditorApplication.timeSinceStartup - CreatedTime >= AutoDismissSeconds;
        }

        public static void ShowWindow()
        {
            var window = GetWindow<SystemProfilerWindow>("System Profiler");
            window.minSize = new Vector2(600, 400);
        }

        private void OnEnable()
        {
            _profiler = new SystemProfiler();
            _phaseFoldouts = new Dictionary<UpdatePhase, bool>();
            _systemDetailFoldouts = new Dictionary<Type, bool>();

            foreach (UpdatePhase phase in Enum.GetValues(typeof(UpdatePhase)))
            {
                _phaseFoldouts[phase] = true;
            }

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            _profiler?.Dispose();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                RefreshSystemList();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _isRecording = false;
                _profiler?.StopRecording();
                _activeAlerts.Clear();
            }
            Repaint();
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(5, 5, 10, 5)
            };

            _phaseHeaderStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };

            _systemRowStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 5, 5),
                margin = new RectOffset(20, 5, 2, 2)
            };

            _metricsStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                padding = new RectOffset(30, 5, 2, 2)
            };

            _warningStyle = new GUIStyle(EditorStyles.helpBox);
            _criticalStyle = new GUIStyle(EditorStyles.helpBox);

            _alertBannerStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 5, 5),
                margin = new RectOffset(0, 0, 0, 2)
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            DrawToolbar();

            if (!Application.isPlaying)
            {
                DrawPlayModeMessage();
                return;
            }

            DrawAlertBanners();

            DrawThresholdSettings();

            DrawSummaryStatisticsPanel();

            if (_showTimeline)
            {
                _timelineScrollPosition = EditorGUILayout.BeginScrollView(_timelineScrollPosition);
                DrawTimelineView();
                EditorGUILayout.EndScrollView();
            }
            else
            {
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
                DrawSystemsByPhase();
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            var recordIcon = _isRecording ? "\u25cf" : "\u25cb";
            var recordColor = _isRecording ? Color.red : Color.gray;
            var prevColor = GUI.contentColor;
            GUI.contentColor = recordColor;

            if (GUILayout.Button($"{recordIcon} Record", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                ToggleRecording();
            }

            GUI.contentColor = prevColor;

            EditorGUI.BeginDisabledGroup(!_isRecording);
            if (GUILayout.Button("Stop", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                StopRecording();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(45)))
            {
                ClearSamples();
            }

            GUILayout.Space(10);

            // Timeline toggle
            var timelineToggleStyle = _showTimeline ? EditorStyles.toolbarButton : EditorStyles.toolbarButton;
            var prevBg = GUI.backgroundColor;
            if (_showTimeline)
            {
                GUI.backgroundColor = new Color(0.5f, 0.7f, 1.0f);
            }
            if (GUILayout.Button("Timeline", timelineToggleStyle, GUILayout.Width(60)))
            {
                _showTimeline = !_showTimeline;
            }
            GUI.backgroundColor = prevBg;

            GUILayout.Space(5);

            // Sort dropdown
            GUILayout.Label("Sort:", GUILayout.Width(30));
            _currentSortMode = (SortMode)EditorGUILayout.Popup(
                (int)_currentSortMode, _sortModeLabels, EditorStyles.toolbarPopup, GUILayout.Width(100));

            GUILayout.Space(10);

            GUILayout.Label("Interval:", GUILayout.Width(50));
            _updateInterval = EditorGUILayout.Slider(_updateInterval, 0.05f, 1.0f, GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            GUILayout.Label("Filter:", GUILayout.Width(40));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            if (GUILayout.Button("Export JSON", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ExportToJson();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPlayModeMessage()
        {
            GUILayout.Space(50);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(450));
            EditorGUILayout.HelpBox(
                "SYSTEM EXECUTION PROFILER\n\n" +
                "Real-time monitoring of ECS system execution times:\n" +
                "\u2022 Hierarchical view grouped by UpdatePhase\n" +
                "\u2022 Configurable warning/critical thresholds\n" +
                "\u2022 Detailed metrics (avg, min, max, std dev)\n" +
                "\u2022 Sparkline mini-charts per system\n" +
                "\u2022 Frame timeline view\n" +
                "\u2022 Alert system for critical thresholds\n" +
                "\u2022 Summary statistics panel\n" +
                "\u2022 Multiple sort modes\n" +
                "\u2022 JSON export for analysis\n\n" +
                "Enter Play Mode to start profiling.",
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

        private void DrawThresholdSettings()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            GUILayout.Label("Thresholds:", EditorStyles.boldLabel, GUILayout.Width(80));

            var prevColor = GUI.backgroundColor;

            GUI.backgroundColor = _warningColor;
            GUILayout.Label("Warning:", GUILayout.Width(55));
            _warningThresholdMs = EditorGUILayout.FloatField(_warningThresholdMs, GUILayout.Width(50));
            GUILayout.Label("ms", GUILayout.Width(25));

            GUILayout.Space(20);

            GUI.backgroundColor = _criticalColor;
            GUILayout.Label("Critical:", GUILayout.Width(50));
            _criticalThresholdMs = EditorGUILayout.FloatField(_criticalThresholdMs, GUILayout.Width(50));
            GUILayout.Label("ms", GUILayout.Width(25));

            GUI.backgroundColor = prevColor;

            GUILayout.FlexibleSpace();

            DrawColorLegend();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawColorLegend()
        {
            GUILayout.Label("Legend:", GUILayout.Width(50));

            var rect = GUILayoutUtility.GetRect(15, 15);
            EditorGUI.DrawRect(rect, _normalColor);
            GUILayout.Label("Normal", GUILayout.Width(45));

            rect = GUILayoutUtility.GetRect(15, 15);
            EditorGUI.DrawRect(rect, _warningColor);
            GUILayout.Label("Warning", GUILayout.Width(50));

            rect = GUILayoutUtility.GetRect(15, 15);
            EditorGUI.DrawRect(rect, _criticalColor);
            GUILayout.Label("Critical", GUILayout.Width(45));
        }

        // =====================================================================
        // Feature 4: Summary Statistics Panel
        // =====================================================================

        private void DrawSummaryStatisticsPanel()
        {
            _showSummary = EditorGUILayout.Foldout(_showSummary, "Summary Statistics", true, EditorStyles.foldoutHeader);
            if (!_showSummary) return;

            var metricsByPhase = GetCachedMetricsByPhase();
            var allMetrics = metricsByPhase.Values.SelectMany(m => m).ToList();

            double totalFrameTime = allMetrics.Sum(m => m.LastExecutionMs);

            string slowestName = "N/A";
            double slowestTime = 0;
            if (allMetrics.Count > 0)
            {
                var slowest = allMetrics.OrderByDescending(m => m.LastExecutionMs).First();
                slowestName = slowest.SystemType.Name;
                slowestTime = slowest.LastExecutionMs;
            }

            int warningCount = allMetrics.Count(m =>
                ThresholdClassifier.Classify(m.LastExecutionMs, _warningThresholdMs, _criticalThresholdMs) == ThresholdLevel.Warning);
            int criticalCount = allMetrics.Count(m =>
                ThresholdClassifier.Classify(m.LastExecutionMs, _warningThresholdMs, _criticalThresholdMs) == ThresholdLevel.Critical);

            const double targetFrameMs = 16.67;
            double budgetUtilization = totalFrameTime / targetFrameMs * 100.0;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Total Frame Time:", EditorStyles.boldLabel, GUILayout.Width(130));
            var totalColor = GetThresholdColor(totalFrameTime);
            var prevContent = GUI.contentColor;
            GUI.contentColor = totalColor;
            GUILayout.Label($"{totalFrameTime:F3} ms", EditorStyles.boldLabel, GUILayout.Width(100));
            GUI.contentColor = prevContent;

            GUILayout.Space(20);
            GUILayout.Label("Budget (60fps):", EditorStyles.boldLabel, GUILayout.Width(100));
            var budgetColor = budgetUtilization > 100 ? _criticalColor :
                budgetUtilization > 75 ? _warningColor : _normalColor;
            prevContent = GUI.contentColor;
            GUI.contentColor = budgetColor;
            GUILayout.Label($"{budgetUtilization:F1}%", EditorStyles.boldLabel, GUILayout.Width(60));
            GUI.contentColor = prevContent;

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Slowest System:", EditorStyles.boldLabel, GUILayout.Width(130));
            GUILayout.Label($"{slowestName} ({slowestTime:F3} ms)", GUILayout.Width(250));

            GUILayout.Space(20);
            GUILayout.Label("Warnings:", EditorStyles.boldLabel, GUILayout.Width(70));
            prevContent = GUI.contentColor;
            GUI.contentColor = warningCount > 0 ? _warningColor : _normalColor;
            GUILayout.Label($"{warningCount}", GUILayout.Width(30));
            GUI.contentColor = prevContent;

            GUILayout.Label("Critical:", EditorStyles.boldLabel, GUILayout.Width(55));
            prevContent = GUI.contentColor;
            GUI.contentColor = criticalCount > 0 ? _criticalColor : _normalColor;
            GUILayout.Label($"{criticalCount}", GUILayout.Width(30));
            GUI.contentColor = prevContent;

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // =====================================================================
        // Feature 3: Alert System
        // =====================================================================

        private void CheckForAlerts(IEnumerable<SystemMetrics> allMetrics)
        {
            foreach (var metrics in allMetrics)
            {
                if (metrics.LastExecutionMs >= _criticalThresholdMs)
                {
                    bool alreadyAlerted = _activeAlerts.Any(a =>
                        a.SystemName == metrics.SystemType.Name && !a.IsExpired);
                    if (!alreadyAlerted)
                    {
                        _activeAlerts.Add(new AlertEntry
                        {
                            SystemName = metrics.SystemType.Name,
                            ExecutionTimeMs = metrics.LastExecutionMs,
                            CreatedTime = EditorApplication.timeSinceStartup
                        });
                    }
                }
            }
        }

        private void DrawAlertBanners()
        {
            // Remove expired alerts
            _activeAlerts.RemoveAll(a => a.IsExpired);

            if (_activeAlerts.Count == 0) return;

            foreach (var alert in _activeAlerts.ToList())
            {
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1.0f, 0.2f, 0.2f, 0.8f);

                EditorGUILayout.BeginHorizontal(_alertBannerStyle);

                var prevContent = GUI.contentColor;
                GUI.contentColor = Color.white;

                double remaining = AlertEntry.AutoDismissSeconds -
                    (EditorApplication.timeSinceStartup - alert.CreatedTime);
                GUILayout.Label(
                    $"CRITICAL: {alert.SystemName} took {alert.ExecutionTimeMs:F3} ms " +
                    $"(auto-dismiss in {remaining:F0}s)",
                    _alertBannerStyle);

                GUI.contentColor = prevContent;

                if (GUILayout.Button("Dismiss", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    _activeAlerts.Remove(alert);
                }

                EditorGUILayout.EndHorizontal();
                GUI.backgroundColor = prevBg;
            }
        }

        // =====================================================================
        // Feature 5: Sort Options
        // =====================================================================

        private IEnumerable<SystemMetrics> ApplySort(IEnumerable<SystemMetrics> metrics)
        {
            return _currentSortMode switch
            {
                SortMode.Name => metrics.OrderBy(m => m.SystemType.Name),
                SortMode.ExecutionTime => metrics.OrderByDescending(m => m.LastExecutionMs),
                SortMode.SampleCount => metrics.OrderByDescending(m => m.SampleCount),
                SortMode.Phase => metrics.OrderBy(m => m.Phase).ThenByDescending(m => m.LastExecutionMs),
                _ => metrics.OrderByDescending(m => m.LastExecutionMs)
            };
        }

        // =====================================================================
        // Systems-by-Phase view (with sparkline and sort)
        // =====================================================================

        private void DrawSystemsByPhase()
        {
            var metricsByPhase = GetCachedMetricsByPhase();

            foreach (UpdatePhase phase in Enum.GetValues(typeof(UpdatePhase)))
            {
                var phaseMetrics = metricsByPhase[phase];

                if (!string.IsNullOrEmpty(_searchFilter))
                {
                    phaseMetrics = phaseMetrics
                        .Where(m => m.SystemType.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }

                if (phaseMetrics.Count == 0 && string.IsNullOrEmpty(_searchFilter))
                {
                    DrawPhaseHeader(phase, 0, 0);
                    continue;
                }

                if (phaseMetrics.Count == 0) continue;

                double phaseTotal = phaseMetrics.Sum(m => m.LastExecutionMs);

                DrawPhaseHeader(phase, phaseMetrics.Count, phaseTotal);

                if (_phaseFoldouts[phase])
                {
                    foreach (var metrics in ApplySort(phaseMetrics))
                    {
                        DrawSystemRow(metrics);
                    }
                }
            }
        }

        private void DrawPhaseHeader(UpdatePhase phase, int systemCount, double totalMs)
        {
            EditorGUILayout.BeginHorizontal();

            var prevBgColor = GUI.backgroundColor;
            GUI.backgroundColor = _phaseHeaderColor;

            _phaseFoldouts[phase] = EditorGUILayout.Foldout(_phaseFoldouts[phase], $"{phase}", true, _phaseHeaderStyle);

            GUI.backgroundColor = prevBgColor;

            GUILayout.FlexibleSpace();

            GUILayout.Label($"{systemCount} systems", EditorStyles.miniLabel, GUILayout.Width(70));

            var totalColor = GetThresholdColor(totalMs);
            var prevContentColor = GUI.contentColor;
            GUI.contentColor = totalColor;
            GUILayout.Label($"Total: {totalMs:F3} ms", EditorStyles.boldLabel, GUILayout.Width(120));
            GUI.contentColor = prevContentColor;

            EditorGUILayout.EndHorizontal();

            var rect = GUILayoutUtility.GetRect(1, 1);
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        private void DrawSystemRow(SystemMetrics metrics)
        {
            var thresholdColor = GetThresholdColor(metrics.LastExecutionMs);
            var prevBgColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(thresholdColor.r, thresholdColor.g, thresholdColor.b, 0.3f);

            EditorGUILayout.BeginVertical(_systemRowStyle);

            EditorGUILayout.BeginHorizontal();

            if (!_systemDetailFoldouts.ContainsKey(metrics.SystemType))
            {
                _systemDetailFoldouts[metrics.SystemType] = false;
            }

            _systemDetailFoldouts[metrics.SystemType] = EditorGUILayout.Foldout(
                _systemDetailFoldouts[metrics.SystemType],
                metrics.SystemType.Name,
                true);

            GUILayout.FlexibleSpace();

            // Feature 1: Sparkline mini-chart
            DrawSparkline(metrics.SystemType);

            GUILayout.Space(5);

            var prevContentColor = GUI.contentColor;
            GUI.contentColor = thresholdColor;
            GUILayout.Label($"{metrics.LastExecutionMs:F3} ms", EditorStyles.boldLabel, GUILayout.Width(80));
            GUI.contentColor = prevContentColor;

            GUILayout.Label($"({metrics.SampleCount} samples)", EditorStyles.miniLabel, GUILayout.Width(80));

            EditorGUILayout.EndHorizontal();

            DrawTimingBar(metrics.LastExecutionMs);

            if (_systemDetailFoldouts[metrics.SystemType])
            {
                DrawDetailedMetrics(metrics);
            }

            EditorGUILayout.EndVertical();

            GUI.backgroundColor = prevBgColor;
        }

        // =====================================================================
        // Feature 1: Sparkline / Mini-Chart per System
        // =====================================================================

        private void DrawSparkline(Type systemType)
        {
            var rect = GUILayoutUtility.GetRect(SparklineWidth, SparklineHeight);

            // Background
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));

            var samples = GetCachedSamples(systemType);
            if (samples == null || samples.Count < 2) return;

            // Take the last N samples
            int startIndex = Mathf.Max(0, samples.Count - SparklineMaxSamples);
            int count = samples.Count - startIndex;

            if (count < 2) return;

            // Find min/max for normalization
            double minVal = double.MaxValue;
            double maxVal = double.MinValue;
            for (int i = startIndex; i < samples.Count; i++)
            {
                double v = samples[i].ExecutionTimeMs;
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }

            double range = maxVal - minVal;
            if (range < 0.0001) range = 0.0001;

            // Build polyline points
            var points = new Vector3[count];
            float padding = 1f;
            float drawWidth = rect.width - padding * 2;
            float drawHeight = rect.height - padding * 2;

            for (int i = 0; i < count; i++)
            {
                float x = rect.x + padding + (drawWidth * i / (count - 1));
                float normalized = (float)((samples[startIndex + i].ExecutionTimeMs - minVal) / range);
                float y = rect.y + rect.height - padding - (drawHeight * normalized);
                points[i] = new Vector3(x, y, 0);
            }

            // Draw threshold reference lines
            float warningNorm = Mathf.Clamp01((float)((_warningThresholdMs - minVal) / range));
            float criticalNorm = Mathf.Clamp01((float)((_criticalThresholdMs - minVal) / range));

            float warningY = rect.y + rect.height - padding - drawHeight * warningNorm;
            float criticalY = rect.y + rect.height - padding - drawHeight * criticalNorm;

            if (warningNorm > 0f && warningNorm < 1f)
            {
                EditorGUI.DrawRect(
                    new Rect(rect.x, warningY, rect.width, 1),
                    new Color(_warningColor.r, _warningColor.g, _warningColor.b, 0.3f));
            }

            if (criticalNorm > 0f && criticalNorm < 1f)
            {
                EditorGUI.DrawRect(
                    new Rect(rect.x, criticalY, rect.width, 1),
                    new Color(_criticalColor.r, _criticalColor.g, _criticalColor.b, 0.3f));
            }

            // Determine line color based on last sample value
            double lastVal = samples[samples.Count - 1].ExecutionTimeMs;
            Color lineColor = GetThresholdColor(lastVal);

            // Draw the polyline using Handles
            Handles.BeginGUI();
            var prevHandlesColor = Handles.color;
            Handles.color = lineColor;
            Handles.DrawAAPolyLine(2f, points);
            Handles.color = prevHandlesColor;
            Handles.EndGUI();
        }

        // =====================================================================
        // Feature 2: Frame Timeline View
        // =====================================================================

        private void DrawTimelineView()
        {
            var metricsByPhase = GetCachedMetricsByPhase();
            var allMetrics = metricsByPhase.Values.SelectMany(m => m).ToList();

            if (!string.IsNullOrEmpty(_searchFilter))
            {
                allMetrics = allMetrics
                    .Where(m => m.SystemType.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            allMetrics = ApplySort(allMetrics).ToList();

            if (allMetrics.Count == 0)
            {
                EditorGUILayout.HelpBox("No systems to display in timeline.", MessageType.Info);
                return;
            }

            // Gather all samples to determine the global time range
            long globalMinTimestamp = long.MaxValue;
            long globalMaxTimestamp = long.MinValue;

            var systemSamplesMap = new Dictionary<Type, IReadOnlyList<SystemTimingSample>>();
            foreach (var m in allMetrics)
            {
                var samples = GetCachedSamples(m.SystemType);
                systemSamplesMap[m.SystemType] = samples;

                if (samples.Count > 0)
                {
                    long first = samples[0].Timestamp;
                    long last = samples[samples.Count - 1].Timestamp;
                    if (first < globalMinTimestamp) globalMinTimestamp = first;
                    if (last > globalMaxTimestamp) globalMaxTimestamp = last;
                }
            }

            if (globalMinTimestamp >= globalMaxTimestamp)
            {
                EditorGUILayout.HelpBox("Not enough timing data for timeline view.", MessageType.Info);
                return;
            }

            long timeSpanMs = globalMaxTimestamp - globalMinTimestamp;
            float timelineWidth = Mathf.Max(400f, position.width - TimelineHeaderWidth - 30f);
            float totalHeight = allMetrics.Count * TimelineTrackHeight + 30f;

            // Timeline header with time labels
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(TimelineHeaderWidth);

            var headerRect = GUILayoutUtility.GetRect(timelineWidth, 20f);
            EditorGUI.DrawRect(headerRect, new Color(0.2f, 0.2f, 0.2f));

            // Draw time labels
            int labelCount = Mathf.Max(2, (int)(timelineWidth / 80f));
            for (int i = 0; i <= labelCount; i++)
            {
                float t = (float)i / labelCount;
                float x = headerRect.x + timelineWidth * t;
                long timeMs = globalMinTimestamp + (long)(timeSpanMs * t);
                float timeSec = timeMs / 1000f;

                var labelRect = new Rect(x - 20, headerRect.y, 50, 20);
                GUI.Label(labelRect, $"{timeSec:F1}s", EditorStyles.miniLabel);

                // Grid line indicator
                EditorGUI.DrawRect(new Rect(x, headerRect.y + 16, 1, 4), _timelineGridColor);
            }

            EditorGUILayout.EndHorizontal();

            // Draw each system track
            for (int i = 0; i < allMetrics.Count; i++)
            {
                var metrics = allMetrics[i];
                EditorGUILayout.BeginHorizontal();

                // System name label
                var nameRect = GUILayoutUtility.GetRect(TimelineHeaderWidth, TimelineTrackHeight);
                Color labelBg = (i % 2 == 0)
                    ? new Color(0.22f, 0.22f, 0.22f)
                    : new Color(0.18f, 0.18f, 0.18f);
                EditorGUI.DrawRect(nameRect, labelBg);
                GUI.Label(
                    new Rect(nameRect.x + 4, nameRect.y + 2, nameRect.width - 8, nameRect.height - 4),
                    metrics.SystemType.Name, EditorStyles.miniLabel);

                // Timeline track
                var trackRect = GUILayoutUtility.GetRect(timelineWidth, TimelineTrackHeight);
                EditorGUI.DrawRect(trackRect, _timelineBackgroundColor);

                // Alternating row tint
                if (i % 2 == 0)
                {
                    EditorGUI.DrawRect(trackRect, new Color(1, 1, 1, 0.02f));
                }

                // Draw grid lines on track
                for (int g = 0; g <= labelCount; g++)
                {
                    float t = (float)g / labelCount;
                    float gx = trackRect.x + timelineWidth * t;
                    EditorGUI.DrawRect(new Rect(gx, trackRect.y, 1, trackRect.height), _timelineGridColor);
                }

                // Draw sample bars
                if (systemSamplesMap.TryGetValue(metrics.SystemType, out var trackSamples))
                {
                    foreach (var sample in trackSamples)
                    {
                        float startNorm = (float)(sample.Timestamp - globalMinTimestamp) / timeSpanMs;
                        float barWidthNorm = (float)(sample.ExecutionTimeMs) / timeSpanMs;

                        float barX = trackRect.x + timelineWidth * startNorm;
                        float barW = Mathf.Max(2f, timelineWidth * barWidthNorm);
                        float barY = trackRect.y + 2;
                        float barH = trackRect.height - 4;

                        Color barColor;
                        if (sample.ExecutionTimeMs >= _criticalThresholdMs)
                            barColor = _timelineBarCriticalColor;
                        else if (sample.ExecutionTimeMs >= _warningThresholdMs)
                            barColor = _timelineBarWarningColor;
                        else
                            barColor = _timelineBarNormalColor;

                        EditorGUI.DrawRect(new Rect(barX, barY, barW, barH), barColor);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        // =====================================================================
        // Timing bar and detailed metrics (existing, unchanged)
        // =====================================================================

        private void DrawTimingBar(double executionTimeMs)
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(4));
            rect.x += 5;
            rect.width -= 10;

            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));

            float maxMs = _criticalThresholdMs * 2;
            float ratio = Mathf.Clamp01((float)(executionTimeMs / maxMs));

            var barRect = new Rect(rect.x, rect.y, rect.width * ratio, rect.height);
            EditorGUI.DrawRect(barRect, GetThresholdColor(executionTimeMs));

            float warningX = rect.x + rect.width * (_warningThresholdMs / maxMs);
            float criticalX = rect.x + rect.width * (_criticalThresholdMs / maxMs);

            EditorGUI.DrawRect(new Rect(warningX, rect.y, 1, rect.height), _warningColor);
            EditorGUI.DrawRect(new Rect(criticalX, rect.y, 1, rect.height), _criticalColor);
        }

        private void DrawDetailedMetrics(SystemMetrics metrics)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Average: {metrics.AverageMs:F3} ms", _metricsStyle, GUILayout.Width(140));
            GUILayout.Label($"Min: {metrics.MinMs:F3} ms", _metricsStyle, GUILayout.Width(120));
            GUILayout.Label($"Max: {metrics.MaxMs:F3} ms", _metricsStyle, GUILayout.Width(120));
            GUILayout.Label($"Std Dev: {metrics.StandardDeviation:F3} ms", _metricsStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Full Type: {metrics.SystemType.FullName}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // =====================================================================
        // Threshold color helpers
        // =====================================================================

        private Color GetThresholdColor(double executionTimeMs)
        {
            var level = ThresholdClassifier.Classify(executionTimeMs, _warningThresholdMs, _criticalThresholdMs);
            return GetColorForLevel(level);
        }

        private Color GetColorForLevel(ThresholdLevel level)
        {
            return level switch
            {
                ThresholdLevel.Critical => _criticalColor,
                ThresholdLevel.Warning => _warningColor,
                _ => _normalColor
            };
        }

        /// <summary>
        /// Gets the threshold level for a given execution time.
        /// Exposed for testing purposes.
        /// </summary>
        internal ThresholdLevel GetThresholdLevel(double executionTimeMs)
        {
            return ThresholdClassifier.Classify(executionTimeMs, _warningThresholdMs, _criticalThresholdMs);
        }

        /// <summary>
        /// Gets the current threshold configuration.
        /// </summary>
        internal ThresholdConfiguration GetThresholdConfiguration()
        {
            return new ThresholdConfiguration
            {
                WarningThresholdMs = _warningThresholdMs,
                CriticalThresholdMs = _criticalThresholdMs
            };
        }

        /// <summary>
        /// Sets the threshold configuration.
        /// </summary>
        internal void SetThresholdConfiguration(ThresholdConfiguration config)
        {
            _warningThresholdMs = (float)config.WarningThresholdMs;
            _criticalThresholdMs = (float)config.CriticalThresholdMs;
        }

        // =====================================================================
        // Recording / lifecycle
        // =====================================================================

        private void ToggleRecording()
        {
            if (_isRecording)
            {
                StopRecording();
            }
            else
            {
                StartRecording();
            }
        }

        private void StartRecording()
        {
            if (!Application.isPlaying) return;

            RefreshSystemList();
            _profiler.StartRecording();
            _isRecording = true;
            InvalidateProfilerCaches();
        }

        private void StopRecording()
        {
            _profiler.StopRecording();
            _isRecording = false;
        }

        private void ClearSamples()
        {
            _profiler.Clear();
            _activeAlerts.Clear();
            InvalidateProfilerCaches();
            Repaint();
        }

        private void RefreshSystemList()
        {
            if (World.Current == null) return;

            var scheduler = World.Current.SystemScheduler;
            if (scheduler == null) return;

            try
            {
                var schedulerType = scheduler.GetType();
                var systemsByPhaseField = schedulerType.GetField("_systemsByPhase",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (systemsByPhaseField != null)
                {
                    var systemsByPhase = systemsByPhaseField.GetValue(scheduler) as System.Collections.IList[];
                    if (systemsByPhase != null)
                    {
                        for (int phaseIndex = 0; phaseIndex < systemsByPhase.Length; phaseIndex++)
                        {
                            var systems = systemsByPhase[phaseIndex];
                            if (systems == null) continue;

                            var phase = ConvertToEditorPhase((ECS.World.UpdatePhase)phaseIndex);

                            foreach (var system in systems)
                            {
                                if (system != null)
                                {
                                    _profiler.RegisterSystem(system.GetType(), phase);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SystemProfiler] Failed to enumerate systems: {ex.Message}");
            }
        }

        private UpdatePhase ConvertToEditorPhase(ECS.World.UpdatePhase runtimePhase)
        {
            return runtimePhase switch
            {
                ECS.World.UpdatePhase.Initialization => UpdatePhase.PreUpdate,
                ECS.World.UpdatePhase.Update => UpdatePhase.Update,
                ECS.World.UpdatePhase.LateUpdate => UpdatePhase.LateUpdate,
                ECS.World.UpdatePhase.FixedUpdate => UpdatePhase.FixedUpdate,
                _ => UpdatePhase.Update
            };
        }

        private void CollectTimingSamples()
        {
            if (World.Current == null) return;

            var scheduler = World.Current.SystemScheduler;
            if (scheduler == null) return;

            try
            {
                var executionTimes = scheduler.LastExecutionTimes;
                foreach (var kvp in executionTimes)
                {
                    _profiler.RecordSample(kvp.Key, kvp.Value);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SystemProfiler] Failed to collect timing samples: {ex.Message}");
            }
        }

        private void ExportToJson()
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Profiling Data",
                "",
                $"profiling_data_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                "json");

            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var snapshot = _profiler.ExportSnapshot();
                var json = JsonUtility.ToJson(new ProfilingSnapshotWrapper(snapshot), true);
                File.WriteAllText(path, json);

                Debug.Log($"[SystemProfiler] Exported profiling data to: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SystemProfiler] Failed to export: {ex.Message}");
                EditorUtility.DisplayDialog("Export Failed", $"Failed to export profiling data:\n{ex.Message}", "OK");
            }
        }

        private void OnInspectorUpdate()
        {
            if (!Application.isPlaying || !_isRecording) return;

            if (EditorApplication.timeSinceStartup - _lastUpdateTime > _updateInterval)
            {
                CollectTimingSamples();
                _dataGeneration++;
                _lastUpdateTime = EditorApplication.timeSinceStartup;

                // Check for critical alerts after collecting samples
                var allMetrics = _profiler.GetAllMetrics();
                CheckForAlerts(allMetrics);

                Repaint();
            }
        }

        /// <summary>
        /// Returns the per-phase metrics for the current sample generation, computing them
        /// at most once per collection tick. OnGUI asks for these from two panels, and each
        /// call re-walks every sample of every system twice.
        /// </summary>
        private Dictionary<UpdatePhase, List<SystemMetrics>> GetCachedMetricsByPhase()
        {
            if (_metricsByPhaseCache == null || _metricsCacheGeneration != _dataGeneration)
            {
                _metricsByPhaseCache = _profiler.GetMetricsByPhase();
                _metricsCacheGeneration = _dataGeneration;
            }

            return _metricsByPhaseCache;
        }

        /// <summary>
        /// Returns a system's samples for the current sample generation. The underlying
        /// GetSamples copies the whole circular buffer, so calling it once per system row per
        /// repaint - repaints the mouse alone can generate - is not affordable.
        /// </summary>
        private IReadOnlyList<SystemTimingSample> GetCachedSamples(Type systemType)
        {
            if (_sampleCacheGeneration != _dataGeneration)
            {
                _sampleCache.Clear();
                _sampleCacheGeneration = _dataGeneration;
            }

            if (!_sampleCache.TryGetValue(systemType, out var samples))
            {
                samples = _profiler.GetSamples(systemType);
                _sampleCache[systemType] = samples;
            }

            return samples;
        }

        /// <summary>
        /// Discards the cached metrics and samples. Call wherever the profiler's sample data
        /// changes outside the collection tick.
        /// </summary>
        private void InvalidateProfilerCaches()
        {
            _dataGeneration++;
        }
    }

    /// <summary>
    /// Wrapper class for JSON serialization of ProfilingSnapshot.
    /// Unity's JsonUtility requires a wrapper for proper serialization.
    /// </summary>
    [Serializable]
    internal class ProfilingSnapshotWrapper
    {
        public string timestamp;
        public string sessionId;
        public List<SystemTimingSampleJson> samples = new List<SystemTimingSampleJson>();
        public List<SystemMetricsJson> metrics = new List<SystemMetricsJson>();
        public int bufferSize;
        public int systemCount;
        public int totalSamples;

        public ProfilingSnapshotWrapper(ProfilingSnapshot snapshot)
        {
            timestamp = snapshot.Timestamp.ToString("o");
            sessionId = snapshot.SessionId;

            foreach (var sample in snapshot.Samples)
            {
                samples.Add(new SystemTimingSampleJson
                {
                    systemTypeName = sample.SystemTypeName,
                    phase = sample.Phase,
                    executionTimeMs = sample.ExecutionTimeMs,
                    timestamp = sample.Timestamp
                });
            }

            foreach (var m in snapshot.Metrics)
            {
                metrics.Add(new SystemMetricsJson
                {
                    systemTypeName = m.SystemTypeName,
                    phase = m.Phase,
                    averageMs = m.AverageMs,
                    minMs = m.MinMs,
                    maxMs = m.MaxMs,
                    standardDeviation = m.StandardDeviation,
                    sampleCount = m.SampleCount
                });
            }

            if (snapshot.Metadata.TryGetValue("BufferSize", out var bs))
                bufferSize = (int)bs;
            if (snapshot.Metadata.TryGetValue("SystemCount", out var sc))
                systemCount = (int)sc;
            if (snapshot.Metadata.TryGetValue("TotalSamples", out var ts))
                totalSamples = (int)ts;
        }
    }

    [Serializable]
    internal class SystemTimingSampleJson
    {
        public string systemTypeName;
        public string phase;
        public double executionTimeMs;
        public long timestamp;
    }

    [Serializable]
    internal class SystemMetricsJson
    {
        public string systemTypeName;
        public string phase;
        public double averageMs;
        public double minMs;
        public double maxMs;
        public double standardDeviation;
        public int sampleCount;
    }
}

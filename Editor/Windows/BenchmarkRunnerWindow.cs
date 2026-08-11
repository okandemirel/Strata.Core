using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Strada.Core.Editor.Benchmarking;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Editor window for running and comparing performance benchmarks.
    /// Provides UI for executing benchmarks, viewing results, comparing with baselines,
    /// and detecting performance regressions.
    /// </summary>
    public class BenchmarkRunnerWindow : EditorWindow
    {
        private BenchmarkRunner _runner;
        private BenchmarkSession _currentSession;
        private BenchmarkSession _baselineSession;
        private List<BenchmarkComparison> _comparisons = new List<BenchmarkComparison>();

        private Vector2 _scrollPosition;
        private Vector2 _historyScrollPosition;
        private Dictionary<string, bool> _categoryFoldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> _benchmarkSelection = new Dictionary<string, bool>();
        private int _selectedTab;
        private string _searchFilter = "";
        private bool _isRunning;
        private string _currentBenchmark = "";
        private int _completedCount;
        private int _totalCount;

        private double _regressionThreshold = 10.0;
        private bool _showOnlyRegressions;

        // GetSavedSessions hits the filesystem and sorts. IMGUI runs Layout and Repaint as
        // separate passes, so calling it from OnGUI meant at least two directory
        // enumerations per input event for as long as the History tab was open.
        private List<string> _cachedSessions;

        private GUIStyle _headerStyle;
        private GUIStyle _categoryStyle;
        private GUIStyle _resultStyle;
        private bool _stylesInitialized;

        private readonly Color _passedColor = new Color(0.4f, 0.8f, 0.4f);
        private readonly Color _failedColor = new Color(0.9f, 0.4f, 0.4f);
        private readonly Color _regressionColor = new Color(1.0f, 0.6f, 0.2f);
        private readonly Color _improvementColor = new Color(0.4f, 0.7f, 1.0f);

        private readonly string[] _tabNames = { "Benchmarks", "Results", "History", "Settings" };

        public static void ShowWindow()
        {
            var window = GetWindow<BenchmarkRunnerWindow>("Benchmark Runner");
            window.minSize = new Vector2(600, 400);
        }

        private void OnEnable()
        {
            _runner = new BenchmarkRunner();
            _runner.OnBenchmarkStarted += OnBenchmarkStarted;
            _runner.OnBenchmarkCompleted += OnBenchmarkCompleted;

            foreach (var category in _runner.GetCategories())
            {
                _categoryFoldouts[category] = true;
            }

            foreach (var benchmark in _runner.Benchmarks)
            {
                _benchmarkSelection[benchmark.Name] = true;
            }

            _baselineSession = BenchmarkPersistence.LoadLatestSession();
        }

        private void OnDisable()
        {
            if (_runner != null)
            {
                _runner.OnBenchmarkStarted -= OnBenchmarkStarted;
                _runner.OnBenchmarkCompleted -= OnBenchmarkCompleted;
            }
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(5, 5, 10, 5)
            };

            _categoryStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };

            _resultStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 5, 5),
                margin = new RectOffset(20, 5, 2, 2)
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            DrawToolbar();

            _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames);

            EditorGUILayout.Space(5);

            switch (_selectedTab)
            {
                case 0:
                    DrawBenchmarksTab();
                    break;
                case 1:
                    DrawResultsTab();
                    break;
                case 2:
                    DrawHistoryTab();
                    break;
                case 3:
                    DrawSettingsTab();
                    break;
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginDisabledGroup(_isRunning);

            if (GUILayout.Button("Run Selected", EditorStyles.toolbarButton, GUILayout.Width(85)))
            {
                RunSelectedBenchmarks();
            }

            if (GUILayout.Button("Run All", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                RunAllBenchmarks();
            }

            EditorGUI.EndDisabledGroup();

            GUILayout.Space(10);

            if (_isRunning)
            {
                var progress = _totalCount > 0 ? (float)_completedCount / _totalCount : 0;
                var rect = GUILayoutUtility.GetRect(150, 18);
                EditorGUI.ProgressBar(rect, progress, $"Running: {_currentBenchmark}");
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label("Search:", GUILayout.Width(45));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            if (GUILayout.Button("Save Results", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                SaveCurrentSession();
            }

            if (GUILayout.Button("Load Baseline", EditorStyles.toolbarButton, GUILayout.Width(85)))
            {
                LoadBaseline();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawBenchmarksTab()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All", GUILayout.Width(80)))
                SetAllSelections(true);
            if (GUILayout.Button("Select None", GUILayout.Width(80)))
                SetAllSelections(false);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            foreach (var category in _runner.GetCategories())
            {
                var benchmarksInCategory = _runner.Benchmarks
                    .Where(b => b.Category == category)
                    .Where(b => string.IsNullOrEmpty(_searchFilter) ||
                                b.Name.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (benchmarksInCategory.Count == 0) continue;

                if (!_categoryFoldouts.ContainsKey(category))
                    _categoryFoldouts[category] = true;

                _categoryFoldouts[category] = EditorGUILayout.Foldout(_categoryFoldouts[category],
                    $"{category} ({benchmarksInCategory.Count})", true, _categoryStyle);

                if (_categoryFoldouts[category])
                {
                    EditorGUI.indentLevel++;

                    foreach (var benchmark in benchmarksInCategory)
                    {
                        DrawBenchmarkItem(benchmark);
                    }

                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBenchmarkItem(BenchmarkDefinition benchmark)
        {
            EditorGUILayout.BeginHorizontal(_resultStyle);

            _benchmarkSelection[benchmark.Name] = EditorGUILayout.Toggle(
                _benchmarkSelection[benchmark.Name], GUILayout.Width(20));

            EditorGUILayout.LabelField(benchmark.Name, EditorStyles.boldLabel, GUILayout.Width(200));

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField($"Iterations: {benchmark.DefaultIterations}", GUILayout.Width(120));

            var threshold = _runner.GetThreshold(benchmark.Name);
            if (threshold?.MinOpsPerSecond != null)
            {
                EditorGUILayout.LabelField($"Min: {threshold.MinOpsPerSecond:F0} ops/s", GUILayout.Width(120));
            }

            EditorGUI.BeginDisabledGroup(_isRunning);
            if (GUILayout.Button("Run", GUILayout.Width(50)))
            {
                RunSingleBenchmark(benchmark.Name);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(benchmark.Description))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField(benchmark.Description, EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawResultsTab()
        {
            if (_currentSession == null || _currentSession.Results.Count == 0)
            {
                EditorGUILayout.HelpBox("No benchmark results available. Run benchmarks to see results.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Session: {_currentSession.SessionId}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Time: {_currentSession.Timestamp:g}");
            EditorGUILayout.LabelField($"Results: {_currentSession.Results.Count}");

            int passed = _currentSession.Results.Count(r => r.Passed);
            int failed = _currentSession.Results.Count - passed;

            var prevColor = GUI.contentColor;
            GUI.contentColor = _passedColor;
            EditorGUILayout.LabelField($"Passed: {passed}", GUILayout.Width(80));
            GUI.contentColor = _failedColor;
            EditorGUILayout.LabelField($"Failed: {failed}", GUILayout.Width(80));
            GUI.contentColor = prevColor;

            EditorGUILayout.EndHorizontal();

            if (_baselineSession != null)
            {
                EditorGUILayout.BeginHorizontal();
                _showOnlyRegressions = EditorGUILayout.Toggle("Show Only Regressions", _showOnlyRegressions);
                EditorGUILayout.LabelField($"Baseline: {_baselineSession.SessionId} ({_baselineSession.Timestamp:g})", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            var resultsByCategory = _currentSession.Results.GroupBy(r => r.Category);

            foreach (var group in resultsByCategory)
            {
                EditorGUILayout.LabelField(group.Key, _headerStyle);

                foreach (var result in group)
                {
                    var comparison = _comparisons.FirstOrDefault(c => c.Current?.Name == result.Name);

                    if (_showOnlyRegressions && (comparison == null || !comparison.IsRegression))
                        continue;

                    DrawResultItem(result, comparison);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResultItem(BenchmarkResult result, BenchmarkComparison comparison)
        {
            var bgColor = result.Passed ? new Color(0.3f, 0.5f, 0.3f, 0.2f) : new Color(0.5f, 0.3f, 0.3f, 0.2f);

            if (comparison != null && comparison.IsRegression)
            {
                bgColor = new Color(0.6f, 0.4f, 0.2f, 0.3f);
            }

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;

            EditorGUILayout.BeginVertical(_resultStyle);

            EditorGUILayout.BeginHorizontal();

            var statusIcon = result.Passed ? "✓" : "✗";
            var statusColor = result.Passed ? _passedColor : _failedColor;
            var prevContent = GUI.contentColor;
            GUI.contentColor = statusColor;
            EditorGUILayout.LabelField(statusIcon, GUILayout.Width(20));
            GUI.contentColor = prevContent;

            EditorGUILayout.LabelField(result.Name, EditorStyles.boldLabel, GUILayout.Width(180));

            EditorGUILayout.LabelField($"{result.OperationsPerSecond:N0} ops/s", GUILayout.Width(120));
            EditorGUILayout.LabelField($"Avg: {result.AverageTimeMs:F4} ms", GUILayout.Width(120));
            EditorGUILayout.LabelField($"Memory: {FormatBytes(result.MemoryAllocatedBytes)}", GUILayout.Width(100));

            if (comparison != null)
            {
                DrawComparisonIndicator(comparison);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Min: {result.MinTimeMs:F4} ms", EditorStyles.miniLabel, GUILayout.Width(100));
            EditorGUILayout.LabelField($"Max: {result.MaxTimeMs:F4} ms", EditorStyles.miniLabel, GUILayout.Width(100));
            EditorGUILayout.LabelField($"StdDev: {result.StandardDeviation:F4} ms", EditorStyles.miniLabel, GUILayout.Width(120));
            EditorGUILayout.LabelField($"Iterations: {result.Iterations}", EditorStyles.miniLabel, GUILayout.Width(100));

            if (!result.Passed && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                GUI.contentColor = _failedColor;
                EditorGUILayout.LabelField($"Error: {result.ErrorMessage}", EditorStyles.miniLabel);
                GUI.contentColor = prevContent;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            GUI.backgroundColor = prevBg;
        }

        private void DrawComparisonIndicator(BenchmarkComparison comparison)
        {
            var changeText = comparison.PercentageChange >= 0
                ? $"+{comparison.PercentageChange:F1}%"
                : $"{comparison.PercentageChange:F1}%";

            var color = comparison.IsRegression ? _regressionColor :
                        comparison.PercentageChange < -5 ? _improvementColor :
                        Color.gray;

            var icon = comparison.IsRegression ? "▲" :
                       comparison.PercentageChange < -5 ? "▼" : "─";

            var prevColor = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField($"{icon} {changeText}", GUILayout.Width(80));
            GUI.contentColor = prevColor;
        }

        /// <summary>
        /// Returns the saved session paths, reading the results directory only when the
        /// cache has been invalidated. Never call BenchmarkPersistence.GetSavedSessions
        /// directly from OnGUI.
        /// </summary>
        private List<string> GetCachedSessions()
        {
            return _cachedSessions ??= BenchmarkPersistence.GetSavedSessions();
        }

        private void InvalidateSessionCache()
        {
            _cachedSessions = null;
        }

        private void OnFocus()
        {
            // Sessions can be written by another process or an earlier run, so re-read the
            // directory when the user comes back to the window.
            InvalidateSessionCache();
        }

        private void DrawHistoryTab()
        {
            var sessions = GetCachedSessions();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Saved Sessions ({sessions.Count})", _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
            {
                InvalidateSessionCache();
            }
            EditorGUILayout.EndHorizontal();

            if (sessions.Count == 0)
            {
                EditorGUILayout.HelpBox("No saved benchmark sessions found.", MessageType.Info);
                return;
            }

            _historyScrollPosition = EditorGUILayout.BeginScrollView(_historyScrollPosition);

            foreach (var path in sessions)
            {
                var filename = Path.GetFileName(path);

                EditorGUILayout.BeginHorizontal(_resultStyle);

                EditorGUILayout.LabelField(filename, GUILayout.Width(300));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Load", GUILayout.Width(50)))
                {
                    var session = BenchmarkPersistence.LoadSession(path);
                    if (session != null)
                    {
                        _currentSession = session;
                        UpdateComparisons();
                        _selectedTab = 1;
                    }
                }

                if (GUILayout.Button("Set Baseline", GUILayout.Width(80)))
                {
                    _baselineSession = BenchmarkPersistence.LoadSession(path);
                    UpdateComparisons();
                }

                if (GUILayout.Button("Delete", GUILayout.Width(50)))
                {
                    if (EditorUtility.DisplayDialog("Delete Session",
                        $"Delete benchmark session?\n{filename}", "Delete", "Cancel"))
                    {
                        BenchmarkPersistence.DeleteSession(path);
                        InvalidateSessionCache();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSettingsTab()
        {
            EditorGUILayout.LabelField("Regression Detection", _headerStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            _regressionThreshold = EditorGUILayout.DoubleField("Regression Threshold (%)", _regressionThreshold);
            EditorGUILayout.HelpBox(
                $"Performance regressions exceeding {_regressionThreshold:F1}% will be highlighted.",
                MessageType.Info);

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Benchmark Thresholds", _headerStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            foreach (var benchmark in _runner.Benchmarks)
            {
                var threshold = _runner.GetThreshold(benchmark.Name);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(benchmark.Name, GUILayout.Width(180));

                if (threshold != null)
                {
                    var minOps = threshold.MinOpsPerSecond ?? 0;
                    var newMinOps = EditorGUILayout.DoubleField("Min Ops/s", minOps, GUILayout.Width(200));

                    if (Math.Abs(newMinOps - minOps) > 0.001)
                    {
                        threshold.MinOpsPerSecond = newMinOps > 0 ? newMinOps : (double?)null;
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("No threshold configured", EditorStyles.miniLabel);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Storage", _headerStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Results Directory: {BenchmarkPersistence.GetDefaultDirectory()}");

            if (GUILayout.Button("Open Results Folder", GUILayout.Width(150)))
            {
                var dir = BenchmarkPersistence.GetDefaultDirectory();
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                EditorUtility.RevealInFinder(dir);
            }

            EditorGUILayout.EndVertical();
        }

        private void SetAllSelections(bool selected)
        {
            foreach (var key in _benchmarkSelection.Keys.ToList())
                _benchmarkSelection[key] = selected;
        }

        private void RunSelectedBenchmarks()
        {
            var selected = _runner.Benchmarks
                .Where(b => _benchmarkSelection.TryGetValue(b.Name, out var sel) && sel)
                .ToList();

            if (selected.Count == 0)
            {
                EditorUtility.DisplayDialog("No Benchmarks Selected",
                    "Please select at least one benchmark to run.", "OK");
                return;
            }

            StartBenchmarkRun(selected);
        }

        private void RunAllBenchmarks()
        {
            StartBenchmarkRun(_runner.Benchmarks.ToList());
        }

        private void RunSingleBenchmark(string name)
        {
            var benchmark = _runner.Benchmarks.FirstOrDefault(b => b.Name == name);
            if (benchmark != null)
            {
                StartBenchmarkRun(new List<BenchmarkDefinition> { benchmark });
            }
        }

        private void StartBenchmarkRun(List<BenchmarkDefinition> benchmarks)
        {
            _isRunning = true;
            _completedCount = 0;
            _totalCount = benchmarks.Count;
            _currentSession = BenchmarkSession.Create();

            EditorApplication.delayCall += () => RunBenchmarksSequentially(benchmarks, 0);
        }

        private void RunBenchmarksSequentially(List<BenchmarkDefinition> benchmarks, int index)
        {
            if (index >= benchmarks.Count)
            {
                FinishBenchmarkRun();
                return;
            }

            var benchmark = benchmarks[index];
            var result = _runner.RunBenchmark(benchmark, benchmark.DefaultIterations);
            _currentSession.Results.Add(result);

            _completedCount++;
            Repaint();

            EditorApplication.delayCall += () => RunBenchmarksSequentially(benchmarks, index + 1);
        }

        private void FinishBenchmarkRun()
        {
            _isRunning = false;
            _currentBenchmark = "";
            UpdateComparisons();
            _selectedTab = 1;
            Repaint();

            int passed = _currentSession.Results.Count(r => r.Passed);
            int failed = _currentSession.Results.Count - passed;
            int regressions = _comparisons.Count(c => c.IsRegression);

            var message = $"Completed {_currentSession.Results.Count} benchmarks.\n" +
                          $"Passed: {passed}, Failed: {failed}";

            if (regressions > 0)
            {
                message += $"\n\nWarning: {regressions} regression(s) detected!";
            }

            Debug.Log($"[BenchmarkRunner] {message}");
        }

        private void OnBenchmarkStarted(string name)
        {
            _currentBenchmark = name;
            Repaint();
        }

        private void OnBenchmarkCompleted(BenchmarkResult result)
        {
            Repaint();
        }

        private void SaveCurrentSession()
        {
            if (_currentSession == null || _currentSession.Results.Count == 0)
            {
                EditorUtility.DisplayDialog("No Results",
                    "No benchmark results to save. Run benchmarks first.", "OK");
                return;
            }

            var path = BenchmarkPersistence.SaveSession(_currentSession);
            InvalidateSessionCache();
            Debug.Log($"[BenchmarkRunner] Saved session to: {path}");
            EditorUtility.DisplayDialog("Session Saved",
                $"Benchmark results saved to:\n{path}", "OK");
        }

        private void LoadBaseline()
        {
            var path = EditorUtility.OpenFilePanel("Load Baseline Session",
                BenchmarkPersistence.GetDefaultDirectory(), "json");

            if (string.IsNullOrEmpty(path)) return;

            _baselineSession = BenchmarkPersistence.LoadSession(path);

            if (_baselineSession != null)
            {
                UpdateComparisons();
                Debug.Log($"[BenchmarkRunner] Loaded baseline: {_baselineSession.SessionId}");
            }
            else
            {
                EditorUtility.DisplayDialog("Load Failed",
                    "Failed to load baseline session.", "OK");
            }
        }

        private void UpdateComparisons()
        {
            _comparisons.Clear();

            if (_currentSession == null || _baselineSession == null) return;

            foreach (var current in _currentSession.Results)
            {
                var baseline = _baselineSession.Results.FirstOrDefault(r => r.Name == current.Name);
                if (baseline != null)
                {
                    _comparisons.Add(BenchmarkComparison.Compare(baseline, current, _regressionThreshold));
                }
            }
        }

        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}

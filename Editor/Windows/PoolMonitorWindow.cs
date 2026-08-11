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
    /// Editor window for monitoring object pool usage across the application.
    /// Discovers pool instances via reflection, displays utilization metrics,
    /// highlights pools nearing capacity, and provides force-return functionality.
    /// </summary>
    public class PoolMonitorWindow : EditorWindow
    {
        private const float WarningThreshold = 0.8f;
        private const float DefaultRefreshInterval = 1.0f;
        private const float MinRefreshInterval = 0.1f;
        private const float MaxRefreshInterval = 5.0f;

        private List<PoolInfo> _discoveredPools = new List<PoolInfo>();
        private Vector2 _scrollPosition;

        private bool _autoRefresh = true;
        private float _refreshInterval = DefaultRefreshInterval;
        private double _lastRefreshTime;

        private SortMode _sortMode = SortMode.Name;
        private bool _sortAscending = true;

        private string _searchFilter = "";

        private int _totalPools;
        private int _totalActiveObjects;
        private int _totalPooledObjects;

        private GUIStyle _headerStyle;
        private GUIStyle _poolRowStyle;
        private GUIStyle _warningRowStyle;
        private bool _stylesInitialized;

        private enum SortMode
        {
            Name,
            ActiveCount,
            Utilization
        }

        /// <summary>
        /// Opens the Pool Monitor window.
        /// </summary>
        [MenuItem("Strada/Tools/Pool Monitor", priority = 53)]
        public static void ShowWindow()
        {
            var window = GetWindow<PoolMonitorWindow>("Pool Monitor");
            window.minSize = new Vector2(500, 400);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            DiscoverPools();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                DiscoverPools();
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _discoveredPools.Clear();
                _totalPools = 0;
                _totalActiveObjects = 0;
                _totalPooledObjects = 0;
            }

            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (!_autoRefresh) return;

            if (EditorApplication.timeSinceStartup - _lastRefreshTime > _refreshInterval)
            {
                RefreshPoolData();
                _lastRefreshTime = EditorApplication.timeSinceStartup;
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

            _poolRowStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 5, 5),
                margin = new RectOffset(5, 5, 2, 2)
            };

            _warningRowStyle = new GUIStyle(_poolRowStyle);

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

            if (_discoveredPools.Count == 0)
            {
                DrawNoPoolsMessage();
                return;
            }

            DrawSummaryBar();
            StradaEditorStyles.DrawSeparator();
            DrawPoolList();
            DrawStatusBar();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(55)))
            {
                DiscoverPools();
                RefreshPoolData();
            }

            if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(55)))
            {
                _discoveredPools.Clear();
                DiscoverPools();
                RefreshPoolData();
            }

            GUILayout.Space(10);

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto Refresh", EditorStyles.toolbarButton, GUILayout.Width(85));

            if (_autoRefresh)
            {
                GUILayout.Label("Interval:", GUILayout.Width(50));
                _refreshInterval = EditorGUILayout.Slider(
                    _refreshInterval, MinRefreshInterval, MaxRefreshInterval, GUILayout.Width(100));
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label("Sort:", GUILayout.Width(30));
            var newSort = (SortMode)EditorGUILayout.EnumPopup(_sortMode, EditorStyles.toolbarPopup, GUILayout.Width(90));
            if (newSort != _sortMode)
            {
                _sortMode = newSort;
                SortPools();
            }

            if (GUILayout.Button(_sortAscending ? "Asc" : "Desc", EditorStyles.toolbarButton, GUILayout.Width(35)))
            {
                _sortAscending = !_sortAscending;
                SortPools();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Filter:", GUILayout.Width(40));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(200));

            if (!string.IsNullOrEmpty(_searchFilter))
            {
                if (GUILayout.Button("x", EditorStyles.toolbarButton, GUILayout.Width(20)))
                {
                    _searchFilter = "";
                }
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawNotPlayingMessage()
        {
            GUILayout.Space(50);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(450));
            EditorGUILayout.HelpBox(
                "POOL MONITOR\n\n" +
                "Monitor object pool usage in real-time:\n" +
                "  - View active, inactive, and total counts per pool\n" +
                "  - Visual utilization bars with warning highlights\n" +
                "  - Track peak usage over time\n" +
                "  - Sort by name, active count, or utilization\n" +
                "  - Force return objects to their pools\n\n" +
                "Enter Play Mode to begin monitoring.",
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

        private void DrawNoPoolsMessage()
        {
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(450));
            EditorGUILayout.HelpBox(
                "No object pools discovered.\n\n" +
                "The Pool Monitor searches for:\n" +
                "  - UnityEngine.Pool.ObjectPool<T> instances\n" +
                "  - Fields named _pool, _objectPool, Pool, etc.\n" +
                "  - Custom pool implementations with Count/CountInactive properties\n\n" +
                "To integrate your pools:\n" +
                "  1. Use UnityEngine.Pool.ObjectPool<T>\n" +
                "  2. Store pool references in static or instance fields on MonoBehaviours\n" +
                "  3. Click 'Rescan' after pools are created",
                MessageType.Info);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private void DrawSummaryBar()
        {
            EditorGUILayout.BeginHorizontal(StradaEditorStyles.StatsBoxStyle);

            DrawSummaryStat("Pools", _totalPools.ToString(), StradaEditorStyles.InfoColor);
            GUILayout.Space(20);
            DrawSummaryStat("Active Objects", _totalActiveObjects.ToString(), StradaEditorStyles.WarningColor);
            GUILayout.Space(20);
            DrawSummaryStat("Pooled Objects", _totalPooledObjects.ToString(), StradaEditorStyles.SuccessColor);
            GUILayout.Space(20);

            float overallUtilization = (_totalActiveObjects + _totalPooledObjects) > 0
                ? (float)_totalActiveObjects / (_totalActiveObjects + _totalPooledObjects)
                : 0f;
            DrawSummaryStat("Utilization", $"{overallUtilization:P0}",
                overallUtilization >= WarningThreshold ? StradaEditorStyles.ErrorColor : StradaEditorStyles.NormalColor);

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSummaryStat(string label, string value, Color valueColor)
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(label, StradaEditorStyles.MiniLabelStyle);

            var prevColor = GUI.contentColor;
            GUI.contentColor = valueColor;
            EditorGUILayout.LabelField(value, EditorStyles.boldLabel, GUILayout.Width(80));
            GUI.contentColor = prevColor;

            EditorGUILayout.EndVertical();
        }

        private void DrawPoolList()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawPoolListHeader();

            var filteredPools = GetFilteredPools();
            foreach (var pool in filteredPools)
            {
                DrawPoolRow(pool);
            }

            if (filteredPools.Count == 0 && !string.IsNullOrEmpty(_searchFilter))
            {
                EditorGUILayout.HelpBox("No pools match the current filter.", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPoolListHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Pool Type", EditorStyles.miniLabel, GUILayout.Width(200));
            GUILayout.Label("Active", EditorStyles.miniLabel, GUILayout.Width(55));
            GUILayout.Label("Inactive", EditorStyles.miniLabel, GUILayout.Width(55));
            GUILayout.Label("Total", EditorStyles.miniLabel, GUILayout.Width(55));
            GUILayout.Label("Peak", EditorStyles.miniLabel, GUILayout.Width(55));
            GUILayout.Label("Utilization", EditorStyles.miniLabel, GUILayout.MinWidth(100));
            GUILayout.Label("", GUILayout.Width(80));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPoolRow(PoolInfo pool)
        {
            bool isWarning = pool.Utilization >= WarningThreshold;

            var prevBg = GUI.backgroundColor;
            if (isWarning)
            {
                GUI.backgroundColor = new Color(
                    StradaEditorStyles.WarningColor.r,
                    StradaEditorStyles.WarningColor.g,
                    StradaEditorStyles.WarningColor.b,
                    0.2f);
            }

            EditorGUILayout.BeginHorizontal(_poolRowStyle);
            GUI.backgroundColor = prevBg;

            EditorGUILayout.LabelField(pool.TypeName, GUILayout.Width(200));

            var prevContentColor = GUI.contentColor;
            GUI.contentColor = isWarning ? StradaEditorStyles.WarningColor : Color.white;
            EditorGUILayout.LabelField(pool.ActiveCount.ToString(), GUILayout.Width(55));
            GUI.contentColor = prevContentColor;

            EditorGUILayout.LabelField(pool.InactiveCount.ToString(), GUILayout.Width(55));
            EditorGUILayout.LabelField(pool.TotalCount.ToString(), GUILayout.Width(55));

            prevContentColor = GUI.contentColor;
            GUI.contentColor = pool.PeakUsage >= pool.TotalCount && pool.TotalCount > 0
                ? StradaEditorStyles.ErrorColor
                : Color.white;
            EditorGUILayout.LabelField(pool.PeakUsage.ToString(), GUILayout.Width(55));
            GUI.contentColor = prevContentColor;

            DrawUtilizationBar(pool.Utilization);

            EditorGUI.BeginDisabledGroup(pool.ActiveCount == 0);
            if (GUILayout.Button("Return All", EditorStyles.miniButton, GUILayout.Width(75)))
            {
                if (EditorUtility.DisplayDialog(
                    "Force Return All",
                    $"Return all {pool.ActiveCount} active objects to pool '{pool.TypeName}'?\n\n" +
                    "This may cause errors if objects are still in use.",
                    "Return All",
                    "Cancel"))
                {
                    ForceReturnAll(pool);
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawUtilizationBar(float utilization)
        {
            var barRect = GUILayoutUtility.GetRect(100, 18, GUILayout.MinWidth(100));
            barRect.y += 2;
            barRect.height -= 4;

            Color fillColor;
            if (utilization >= WarningThreshold)
                fillColor = StradaEditorStyles.ErrorColor;
            else if (utilization >= 0.5f)
                fillColor = StradaEditorStyles.WarningColor;
            else
                fillColor = StradaEditorStyles.SuccessColor;

            StradaEditorStyles.DrawProgressBar(
                barRect,
                utilization,
                new Color(0.15f, 0.15f, 0.15f),
                fillColor);

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            GUI.Label(barRect, $"{utilization:P0}", labelStyle);
        }

        private void DrawStatusBar()
        {
            StradaEditorStyles.DrawSeparator();

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (_autoRefresh)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = StradaEditorStyles.SuccessColor;
                GUILayout.Label("AUTO", EditorStyles.toolbarButton, GUILayout.Width(40));
                GUI.contentColor = prevColor;
            }

            GUILayout.Label($"Pools: {_totalPools}", GUILayout.Width(70));

            int warningCount = _discoveredPools.Count(p => p.Utilization >= WarningThreshold);
            if (warningCount > 0)
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = StradaEditorStyles.WarningColor;
                GUILayout.Label($"Warnings: {warningCount}", GUILayout.Width(90));
                GUI.contentColor = prevColor;
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label($"Last refresh: {DateTime.Now:HH:mm:ss}",
                StradaEditorStyles.MiniLabelStyle, GUILayout.Width(130));

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Discovers all object pool instances by scanning MonoBehaviours and static fields.
        /// Looks for common pool types and naming patterns.
        /// </summary>
        private void DiscoverPools()
        {
            if (!Application.isPlaying) return;

            _discoveredPools.Clear();

            DiscoverPoolsFromMonoBehaviours();
            DiscoverPoolsFromStaticFields();

            RefreshPoolData();
        }

        /// <summary>
        /// Scans all active MonoBehaviours for fields that look like pool instances.
        /// </summary>
        private void DiscoverPoolsFromMonoBehaviours()
        {
            var monoBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);

            foreach (var mb in monoBehaviours)
            {
                if (mb == null) continue;

                try
                {
                    var mbType = mb.GetType();
                    var fields = mbType.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    foreach (var field in fields)
                    {
                        if (IsPoolField(field))
                        {
                            var poolObj = field.GetValue(mb);
                            if (poolObj != null)
                            {
                                RegisterPool(poolObj, field.FieldType, mb.GetType().Name, field.Name, mb);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip objects that cannot be reflected
                }
            }
        }

        /// <summary>
        /// Scans loaded types for static fields that look like pool instances.
        /// </summary>
        private void DiscoverPoolsFromStaticFields()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic) continue;

                string assemblyName = assembly.GetName().Name;
                if (assemblyName.StartsWith("Unity") && !assemblyName.StartsWith("UnityEngine."))
                    continue;
                if (assemblyName.StartsWith("System") || assemblyName.StartsWith("mscorlib")
                    || assemblyName.StartsWith("netstandard") || assemblyName.StartsWith("Mono"))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        ScanStaticFieldsForPools(type);
                    }
                }
                catch
                {
                    // Skip assemblies that fail
                }
            }
        }

        private void ScanStaticFieldsForPools(Type type)
        {
            if (!IsSafeToReadStatics(type)) return;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var field in fields)
            {
                // The name heuristic in IsPoolField is fine for the fields of a single
                // MonoBehaviour the user is looking at, but far too loose for a scan of every
                // type in the project: it matches _poolPrefab, s_poolRoot, PoolManagerInstance
                // and so on, and reading any of them runs that type's initializer.
                if (!IsPoolType(field.FieldType)) continue;

                try
                {
                    var poolObj = field.GetValue(null);
                    if (poolObj != null)
                    {
                        bool alreadyTracked = _discoveredPools.Any(p =>
                            ReferenceEquals(p.PoolInstance, poolObj));

                        if (!alreadyTracked)
                        {
                            RegisterPool(poolObj, field.FieldType, type.Name, field.Name, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Swallowing this silently hides genuine type-initializer failures that
                    // the scan itself triggered, which are otherwise impossible to diagnose.
                    Debug.LogWarning(
                        $"[PoolMonitor] Failed to read static field {type.FullName}.{field.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Determines whether this window may read a type's static fields.
        /// Reading a static field forces the declaring type's initializer to run, so types
        /// that declare an explicit static constructor are skipped: running arbitrary user
        /// and third-party initialization as a side effect of a monitor refresh is not
        /// something an editor window should do.
        /// </summary>
        private static bool IsSafeToReadStatics(Type type)
        {
            if (type == null) return false;

            // Fields of an open generic type cannot be read at all.
            if (type.ContainsGenericParameters) return false;

            // BeforeFieldInit is set by the compiler exactly when there is no explicit static
            // constructor, i.e. when initialization is nothing but field initializers.
            return type.TypeInitializer == null ||
                   (type.Attributes & TypeAttributes.BeforeFieldInit) != 0;
        }

        /// <summary>
        /// Determines whether a field is likely a pool based on its type or name.
        /// </summary>
        private static bool IsPoolField(FieldInfo field)
        {
            var fieldType = field.FieldType;

            if (IsPoolType(fieldType))
                return true;

            string fieldName = field.Name.ToLowerInvariant();
            return fieldName.Contains("pool") &&
                   !fieldName.Contains("poolsize") &&
                   !fieldName.Contains("poolcount");
        }

        /// <summary>
        /// Determines whether a type is a known pool implementation.
        /// </summary>
        private static bool IsPoolType(Type type)
        {
            if (type == null) return false;

            string fullName = type.FullName ?? "";

            if (fullName.Contains("ObjectPool") ||
                fullName.Contains("Pool`1") ||
                fullName.Contains("GenericPool") ||
                fullName.Contains("CollectionPool") ||
                fullName.Contains("ListPool") ||
                fullName.Contains("DictionaryPool") ||
                fullName.Contains("HashSetPool"))
                return true;

            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                string genericName = genericDef.FullName ?? genericDef.Name;
                if (genericName.Contains("Pool"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Registers a discovered pool instance for monitoring.
        /// </summary>
        private void RegisterPool(object poolInstance, Type poolType, string ownerName, string fieldName, UnityEngine.Object ownerObject)
        {
            bool alreadyTracked = _discoveredPools.Any(p => ReferenceEquals(p.PoolInstance, poolInstance));
            if (alreadyTracked) return;

            string elementTypeName = "Unknown";
            if (poolType.IsGenericType)
            {
                var genericArgs = poolType.GetGenericArguments();
                if (genericArgs.Length > 0)
                {
                    elementTypeName = genericArgs[0].Name;
                }
            }

            var info = new PoolInfo
            {
                PoolInstance = poolInstance,
                PoolType = poolType,
                TypeName = $"{elementTypeName} ({ownerName}.{fieldName})",
                ElementTypeName = elementTypeName,
                OwnerName = ownerName,
                FieldName = fieldName,
                OwnerObject = ownerObject,
                ActiveCount = 0,
                InactiveCount = 0,
                TotalCount = 0,
                PeakUsage = 0,
                Utilization = 0f
            };

            ResolvePoolAccessors(info);
            _discoveredPools.Add(info);
        }

        /// <summary>
        /// Resolves reflection accessors for reading pool metrics (count, inactive count, etc.).
        /// </summary>
        private static void ResolvePoolAccessors(PoolInfo info)
        {
            var poolType = info.PoolInstance.GetType();

            info.CountActiveProperty = FindProperty(poolType, "CountActive", "ActiveCount", "Count");
            info.CountInactiveProperty = FindProperty(poolType, "CountInactive", "InactiveCount", "AvailableCount");
            info.CountAllProperty = FindProperty(poolType, "CountAll", "TotalCount", "Capacity", "Size");
            info.ClearMethod = FindMethod(poolType, "Clear", "ReleaseAll", "ReturnAll", "Reset");
        }

        private static PropertyInfo FindProperty(Type type, params string[] candidateNames)
        {
            foreach (var name in candidateNames)
            {
                var prop = type.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(int))
                    return prop;
            }

            return null;
        }

        private static MethodInfo FindMethod(Type type, params string[] candidateNames)
        {
            foreach (var name in candidateNames)
            {
                var method = type.GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (method != null)
                    return method;
            }

            return null;
        }

        /// <summary>
        /// Refreshes pool metrics for all discovered pools.
        /// </summary>
        private void RefreshPoolData()
        {
            _totalActiveObjects = 0;
            _totalPooledObjects = 0;

            for (int i = _discoveredPools.Count - 1; i >= 0; i--)
            {
                var pool = _discoveredPools[i];

                try
                {
                    if (pool.PoolInstance == null ||
                        (pool.PoolInstance is UnityEngine.Object obj && obj == null))
                    {
                        _discoveredPools.RemoveAt(i);
                        continue;
                    }

                    int active = ReadPropertyValue(pool.PoolInstance, pool.CountActiveProperty);
                    int inactive = ReadPropertyValue(pool.PoolInstance, pool.CountInactiveProperty);
                    int total = ReadPropertyValue(pool.PoolInstance, pool.CountAllProperty);

                    if (total <= 0 && (active > 0 || inactive > 0))
                    {
                        total = active + inactive;
                    }

                    if (active <= 0 && total > 0 && inactive >= 0)
                    {
                        active = total - inactive;
                    }

                    if (inactive <= 0 && total > 0 && active >= 0)
                    {
                        inactive = total - active;
                    }

                    pool.ActiveCount = Mathf.Max(0, active);
                    pool.InactiveCount = Mathf.Max(0, inactive);
                    pool.TotalCount = Mathf.Max(0, total);
                    pool.PeakUsage = Mathf.Max(pool.PeakUsage, pool.ActiveCount);
                    pool.Utilization = pool.TotalCount > 0
                        ? (float)pool.ActiveCount / pool.TotalCount
                        : 0f;

                    _totalActiveObjects += pool.ActiveCount;
                    _totalPooledObjects += pool.InactiveCount;
                }
                catch
                {
                    _discoveredPools.RemoveAt(i);
                }
            }

            _totalPools = _discoveredPools.Count;
            SortPools();
        }

        private static int ReadPropertyValue(object instance, PropertyInfo property)
        {
            if (property == null || instance == null) return 0;

            try
            {
                var value = property.GetValue(instance);
                return value is int intVal ? intVal : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Forces all active objects in a pool to be returned/cleared.
        /// </summary>
        private void ForceReturnAll(PoolInfo pool)
        {
            if (pool.ClearMethod == null)
            {
                Debug.LogWarning($"[PoolMonitor] No clear/return method found for pool '{pool.TypeName}'.");
                return;
            }

            try
            {
                pool.ClearMethod.Invoke(pool.PoolInstance, null);
                Debug.Log($"[PoolMonitor] Forced return of all objects in pool '{pool.TypeName}'.");
                RefreshPoolData();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolMonitor] Failed to return objects in pool '{pool.TypeName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Sorts the discovered pools according to the current sort mode and direction.
        /// </summary>
        private void SortPools()
        {
            switch (_sortMode)
            {
                case SortMode.Name:
                    _discoveredPools.Sort((a, b) =>
                        _sortAscending
                            ? string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase)
                            : string.Compare(b.TypeName, a.TypeName, StringComparison.OrdinalIgnoreCase));
                    break;

                case SortMode.ActiveCount:
                    _discoveredPools.Sort((a, b) =>
                        _sortAscending
                            ? a.ActiveCount.CompareTo(b.ActiveCount)
                            : b.ActiveCount.CompareTo(a.ActiveCount));
                    break;

                case SortMode.Utilization:
                    _discoveredPools.Sort((a, b) =>
                        _sortAscending
                            ? a.Utilization.CompareTo(b.Utilization)
                            : b.Utilization.CompareTo(a.Utilization));
                    break;
            }
        }

        /// <summary>
        /// Returns the list of pools filtered by the current search string.
        /// </summary>
        private List<PoolInfo> GetFilteredPools()
        {
            if (string.IsNullOrEmpty(_searchFilter))
                return _discoveredPools;

            return _discoveredPools
                .Where(p => p.TypeName.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            p.ElementTypeName.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            p.OwnerName.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        /// <summary>
        /// Stores runtime information about a discovered pool instance.
        /// </summary>
        private class PoolInfo
        {
            public object PoolInstance;
            public Type PoolType;
            public string TypeName;
            public string ElementTypeName;
            public string OwnerName;
            public string FieldName;
            public UnityEngine.Object OwnerObject;

            public int ActiveCount;
            public int InactiveCount;
            public int TotalCount;
            public int PeakUsage;
            public float Utilization;

            public PropertyInfo CountActiveProperty;
            public PropertyInfo CountInactiveProperty;
            public PropertyInfo CountAllProperty;
            public MethodInfo ClearMethod;
        }
    }
}

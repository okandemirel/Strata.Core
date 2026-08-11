using System;
using System.Collections.Generic;
using System.Reflection;
using Strada.Core.ECS;
using Strada.Core.ECS.Core;
using Strada.Core.ECS.Storage;
using Strada.Core.ECS.World;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Windows
{
    /// <summary>
    /// Diff information between two consecutive snapshots.
    /// </summary>
    public struct SnapshotDiff
    {
        public int[] CreatedEntities;
        public int[] DestroyedEntities;
        public Dictionary<int, List<ComponentChange>> ComponentChanges;
    }

    /// <summary>
    /// Describes a single component field change on an entity.
    /// </summary>
    public struct ComponentChange
    {
        public Type ComponentType;
        public string FieldName;
        public string OldValue;
        public string NewValue;
    }

    /// <summary>
    /// Time Machine window for recording and replaying ECS World states.
    /// Allows stepping back in time to debug logic.
    /// </summary>
    public class TimeMachineWindow : EditorWindow
    {
        private bool _isRecording;
        private int _maxSnapshots = 600;
        private List<WorldSnapshot> _snapshots = new List<WorldSnapshot>();
        private int _playbackFrame = -1;
        private bool _isReplaying;
        private bool _isPlayingRecording;

        private WorldSnapshot _liveSnapshot;
        private float _playbackSpeed = 1.0f;
        private double _lastPlaybackTime;

        // WorldSnapshot.Capture boxes every component of every entity, so it must not run at
        // the raw editor tick rate. Snapshots are sampled on this interval instead.
        private const float DefaultCaptureInterval = 1f / 60f;
        private float _captureInterval = DefaultCaptureInterval;
        private double _lastCaptureTime;

        // A snapshot count alone does not bound memory: each snapshot retains one dictionary
        // per entity plus one boxed value per component, so 600 snapshots of a large world is
        // gigabytes. Evict on the boxed-component total as well as on the count.
        private const int MaxRetainedBoxedComponents = 1_000_000;
        private long _retainedBoxedComponents;

        // Scratch buffers for the timeline bar. These are sized to the snapshot count and
        // reused; allocating them inside OnGUI meant a fresh int[] and Vector3[] per repaint.
        private int[] _entityCountBuffer = Array.Empty<int>();
        private Vector3[] _sparklineBuffer = Array.Empty<Vector3>();

        // Annotations / bookmarks per frame index
        private Dictionary<int, string> _annotations = new Dictionary<int, string>();

        // Cached diff for the current playback frame
        private SnapshotDiff? _cachedDiff;
        private int _cachedDiffFrame = -1;

        // Diff panel scroll position
        private Vector2 _diffScrollPos;
        private bool _showDiffPanel = true;

        // Playback speed presets
        private static readonly float[] SpeedPresets = { 0.25f, 0.5f, 1.0f, 2.0f, 4.0f };
        private static readonly string[] SpeedLabels = { "0.25x", "0.5x", "1x", "2x", "4x" };

        // Timeline visual constants
        private const float TimelineHeight = 60f;
        private const float LifecycleMarkerHeight = 10f;

        // Double-click tracking for annotations
        private double _lastClickTime;
        private int _lastClickFrame = -1;

        // Annotation editing state
        private bool _isEditingAnnotation;
        private int _editingAnnotationFrame = -1;
        private string _editingAnnotationText = "";

        public static void ShowWindow()
        {
            var window = GetWindow<TimeMachineWindow>("Time Machine");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            _snapshots.Clear();
            _retainedBoxedComponents = 0;
            _liveSnapshot = null;
            _annotations.Clear();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _isRecording = false;
                _isReplaying = false;
                _isPlayingRecording = false;
                _snapshots.Clear();
                _retainedBoxedComponents = 0;
                _liveSnapshot = null;
                _playbackFrame = -1;
                _annotations.Clear();
                _cachedDiff = null;
                _cachedDiffFrame = -1;
                _isEditingAnnotation = false;
            }
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            if (_isRecording && !_isReplaying)
            {
                double captureTime = EditorApplication.timeSinceStartup;
                if (captureTime - _lastCaptureTime >= _captureInterval)
                {
                    _lastCaptureTime = captureTime;
                    RecordSnapshot();
                }

                Repaint();
            }

            if (_isPlayingRecording && _isReplaying)
            {
                double currentTime = EditorApplication.timeSinceStartup;
                if (currentTime - _lastPlaybackTime > (0.016f / _playbackSpeed))
                {
                    _lastPlaybackTime = currentTime;
                    Step(1);

                    if (_playbackFrame >= _snapshots.Count - 1)
                    {
                        _isPlayingRecording = false;
                    }
                }

                Repaint();
            }

            // An idle window has nothing changing behind it, so repainting here would only
            // burn editor frames redrawing the same timeline.
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to use Time Machine.", MessageType.Info);
                return;
            }

            DrawToolbar();
            DrawTimeline();
            DrawAnnotationEditor();
            DrawSnapshotDetails();
            DrawDiffPanel();
        }

        // ---------------------------------------------------------------
        // Toolbar
        // ---------------------------------------------------------------

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Record button
            if (GUILayout.Button(_isRecording ? "Stop Recording" : "Start Recording", EditorStyles.toolbarButton))
            {
                _isRecording = !_isRecording;
                if (_isRecording)
                {
                    if (_isReplaying)
                    {
                        RestoreLiveState();
                    }
                    _isReplaying = false;
                    _isPlayingRecording = false;
                }
            }

            // Clear button
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton))
            {
                _snapshots.Clear();
                _retainedBoxedComponents = 0;
                _playbackFrame = -1;
                _isReplaying = false;
                _isPlayingRecording = false;
                _liveSnapshot = null;
                _annotations.Clear();
                _cachedDiff = null;
                _cachedDiffFrame = -1;
            }

            GUILayout.Space(8);

            // Playback speed selector
            GUILayout.Label("Speed:", EditorStyles.miniLabel, GUILayout.Width(38));
            int currentSpeedIndex = Array.IndexOf(SpeedPresets, _playbackSpeed);
            if (currentSpeedIndex < 0) currentSpeedIndex = 2; // default 1x
            int newSpeedIndex = EditorGUILayout.Popup(currentSpeedIndex, SpeedLabels,
                EditorStyles.toolbarPopup, GUILayout.Width(55));
            if (newSpeedIndex != currentSpeedIndex)
            {
                _playbackSpeed = SpeedPresets[newSpeedIndex];
            }

            GUILayout.Space(8);

            // Max snapshots control
            GUILayout.Label("Max:", EditorStyles.miniLabel, GUILayout.Width(28));
            _maxSnapshots = EditorGUILayout.IntField(_maxSnapshots, EditorStyles.toolbarTextField, GUILayout.Width(50));
            _maxSnapshots = Mathf.Max(10, _maxSnapshots);

            GUILayout.Space(8);

            // Capture rate control (seconds between snapshots)
            GUILayout.Label("Every:", EditorStyles.miniLabel, GUILayout.Width(38));
            _captureInterval = EditorGUILayout.FloatField(_captureInterval, EditorStyles.toolbarTextField, GUILayout.Width(50));
            _captureInterval = Mathf.Clamp(_captureInterval, DefaultCaptureInterval, 5f);
            GUILayout.Label("s", EditorStyles.miniLabel, GUILayout.Width(10));

            GUILayout.FlexibleSpace();
            GUILayout.Label($"Snapshots: {_snapshots.Count}/{_maxSnapshots}");

            EditorGUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------
        // Visual Timeline
        // ---------------------------------------------------------------

        private void DrawTimeline()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Timeline", EditorStyles.boldLabel);

            if (_snapshots.Count > 0)
            {
                int maxFrame = _snapshots.Count - 1;
                int currentFrame = _playbackFrame == -1 ? maxFrame : _playbackFrame;

                // --- Visual timeline bar ---
                Rect timelineRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                    GUILayout.ExpandWidth(true), GUILayout.Height(TimelineHeight));

                if (timelineRect.width > 1)
                {
                    DrawTimelineBar(timelineRect, currentFrame, maxFrame);
                }

                // --- Entity lifecycle markers row ---
                Rect lifecycleRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                    GUILayout.ExpandWidth(true), GUILayout.Height(LifecycleMarkerHeight));

                if (lifecycleRect.width > 1)
                {
                    DrawEntityLifecycleMarkers(lifecycleRect, maxFrame);
                }

                // --- Annotation markers row ---
                Rect annotationRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                    GUILayout.ExpandWidth(true), GUILayout.Height(12f));

                if (annotationRect.width > 1)
                {
                    DrawAnnotationMarkers(annotationRect, maxFrame);
                }

                GUILayout.Space(4);

                // --- Transport controls ---
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("<<", GUILayout.Width(30)))
                {
                    StartReplayIfNeeded();
                    Step(-10);
                }

                if (GUILayout.Button("<", GUILayout.Width(30)))
                {
                    StartReplayIfNeeded();
                    Step(-1);
                }

                if (GUILayout.Button(_isPlayingRecording ? "||" : "\u25B6", GUILayout.Width(30)))
                {
                    StartReplayIfNeeded();
                    _isPlayingRecording = !_isPlayingRecording;
                    _lastPlaybackTime = EditorApplication.timeSinceStartup;
                }

                if (GUILayout.Button(">", GUILayout.Width(30)))
                {
                    StartReplayIfNeeded();
                    Step(1);
                }

                if (GUILayout.Button(">>", GUILayout.Width(30)))
                {
                    StartReplayIfNeeded();
                    Step(10);
                }

                GUILayout.FlexibleSpace();

                GUILayout.Label($"Frame: {currentFrame} / {maxFrame}");

                GUILayout.Space(8);

                if (GUILayout.Button(_isReplaying ? "Return to Live" : "Live", GUILayout.Width(100)))
                {
                    if (_isReplaying)
                    {
                        RestoreLiveState();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("No snapshots recorded.");
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the main timeline bar with color-coded segments, sparkline, and playhead.
        /// </summary>
        private void DrawTimelineBar(Rect rect, int currentFrame, int maxFrame)
        {
            // Background
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f, 1f));

            int count = _snapshots.Count;
            if (count == 0) return;

            // Compute entity counts into the reusable buffer
            if (_entityCountBuffer.Length < count)
            {
                _entityCountBuffer = new int[count];
            }
            int[] entityCounts = _entityCountBuffer;

            int minCount = int.MaxValue;
            int maxCount = 0;
            for (int i = 0; i < count; i++)
            {
                entityCounts[i] = _snapshots[i].EntityCount;
                if (entityCounts[i] < minCount) minCount = entityCounts[i];
                if (entityCounts[i] > maxCount) maxCount = entityCounts[i];
            }

            int range = Mathf.Max(1, maxCount - minCount);

            // Draw color-coded segments
            float segWidth = rect.width / Mathf.Max(1, count);
            for (int i = 0; i < count; i++)
            {
                Color segColor;
                if (i == 0)
                {
                    segColor = new Color(0.2f, 0.6f, 0.2f, 0.5f); // green stable
                }
                else
                {
                    int delta = entityCounts[i] - entityCounts[i - 1];
                    if (delta > 0)
                        segColor = new Color(0.9f, 0.8f, 0.1f, 0.5f); // yellow increase
                    else if (delta < 0)
                        segColor = new Color(0.9f, 0.2f, 0.2f, 0.5f); // red decrease
                    else
                        segColor = new Color(0.2f, 0.6f, 0.2f, 0.5f); // green stable
                }

                Rect seg = new Rect(rect.x + i * segWidth, rect.y, Mathf.Max(segWidth, 1f), rect.height);
                EditorGUI.DrawRect(seg, segColor);
            }

            // Draw sparkline overlay
            if (count > 1)
            {
                if (_sparklineBuffer.Length < count)
                {
                    _sparklineBuffer = new Vector3[count];
                }
                Vector3[] points = _sparklineBuffer;

                float padding = 4f;
                float usableHeight = rect.height - padding * 2;

                for (int i = 0; i < count; i++)
                {
                    float x = rect.x + ((float)i / (count - 1)) * rect.width;
                    float normalised = (float)(entityCounts[i] - minCount) / range;
                    float y = rect.yMax - padding - normalised * usableHeight;
                    points[i] = new Vector3(x, y, 0f);
                }

                Handles.color = new Color(1f, 1f, 1f, 0.9f);
                // The buffer can be longer than the snapshot count, so the point count has to
                // be passed explicitly or stale trailing points would be drawn.
                Handles.DrawAAPolyLine(2f, count, points);
            }

            // Draw playhead
            float playheadX = rect.x + ((float)currentFrame / Mathf.Max(1, maxFrame)) * rect.width;
            Rect playheadRect = new Rect(playheadX - 1f, rect.y, 2f, rect.height);
            EditorGUI.DrawRect(playheadRect, Color.cyan);

            // Draw small triangle at top of playhead
            Vector3 triTop = new Vector3(playheadX, rect.y, 0f);
            Vector3 triLeft = new Vector3(playheadX - 4f, rect.y + 6f, 0f);
            Vector3 triRight = new Vector3(playheadX + 4f, rect.y + 6f, 0f);
            Handles.color = Color.cyan;
            Handles.DrawAAConvexPolygon(triTop, triLeft, triRight);

            // Click-to-seek and double-click for annotations
            Event e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                float clickNorm = (e.mousePosition.x - rect.x) / rect.width;
                int seekFrame = Mathf.Clamp(Mathf.RoundToInt(clickNorm * maxFrame), 0, maxFrame);

                // Detect double-click for annotation
                double now = EditorApplication.timeSinceStartup;
                if (_lastClickFrame == seekFrame && (now - _lastClickTime) < 0.3)
                {
                    // Double-click: open annotation editor
                    _isEditingAnnotation = true;
                    _editingAnnotationFrame = seekFrame;
                    _editingAnnotationText = _annotations.ContainsKey(seekFrame) ? _annotations[seekFrame] : "";
                    _lastClickFrame = -1;
                }
                else
                {
                    _lastClickFrame = seekFrame;
                    _lastClickTime = now;
                }

                StartReplayIfNeeded();
                _playbackFrame = seekFrame;
                _isPlayingRecording = false;
                RestoreSnapshot(_snapshots[_playbackFrame]);
                InvalidateDiffCache();
                e.Use();
            }

            // Drag to scrub
            if (e.type == EventType.MouseDrag && rect.Contains(e.mousePosition))
            {
                float dragNorm = (e.mousePosition.x - rect.x) / rect.width;
                int seekFrame = Mathf.Clamp(Mathf.RoundToInt(dragNorm * maxFrame), 0, maxFrame);
                StartReplayIfNeeded();
                _playbackFrame = seekFrame;
                _isPlayingRecording = false;
                RestoreSnapshot(_snapshots[_playbackFrame]);
                InvalidateDiffCache();
                e.Use();
            }
        }

        // ---------------------------------------------------------------
        // Entity Lifecycle Markers
        // ---------------------------------------------------------------

        private void DrawEntityLifecycleMarkers(Rect rect, int maxFrame)
        {
            EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f, 1f));

            int count = _snapshots.Count;
            if (count < 2) return;

            for (int i = 1; i < count; i++)
            {
                // The flags are computed once against the preceding snapshot at capture time.
                // Recomputing them here built two HashSets per snapshot pair per repaint.
                bool hasCreated = _snapshots[i].HasEntityCreations;
                bool hasDestroyed = _snapshots[i].HasEntityDestructions;

                if (!hasCreated && !hasDestroyed) continue;

                float x = rect.x + ((float)i / Mathf.Max(1, maxFrame)) * rect.width;

                // Green up-arrow for creation
                if (hasCreated)
                {
                    Vector3 top = new Vector3(x, rect.y + 1f, 0f);
                    Vector3 left = new Vector3(x - 3f, rect.y + rect.height * 0.5f, 0f);
                    Vector3 right = new Vector3(x + 3f, rect.y + rect.height * 0.5f, 0f);
                    Handles.color = new Color(0.2f, 0.9f, 0.2f, 0.9f);
                    Handles.DrawAAConvexPolygon(top, left, right);
                }

                // Red down-arrow for destruction
                if (hasDestroyed)
                {
                    Vector3 bottom = new Vector3(x, rect.yMax - 1f, 0f);
                    Vector3 left = new Vector3(x - 3f, rect.y + rect.height * 0.5f, 0f);
                    Vector3 right = new Vector3(x + 3f, rect.y + rect.height * 0.5f, 0f);
                    Handles.color = new Color(0.9f, 0.2f, 0.2f, 0.9f);
                    Handles.DrawAAConvexPolygon(bottom, left, right);
                }
            }
        }

        // ---------------------------------------------------------------
        // Annotation Markers on Timeline
        // ---------------------------------------------------------------

        private void DrawAnnotationMarkers(Rect rect, int maxFrame)
        {
            if (_annotations.Count == 0) return;

            foreach (var kvp in _annotations)
            {
                int frame = kvp.Key;
                if (frame < 0 || frame > maxFrame) continue;

                float x = rect.x + ((float)frame / Mathf.Max(1, maxFrame)) * rect.width;

                // Draw a small diamond marker
                Vector3 top = new Vector3(x, rect.y, 0f);
                Vector3 right = new Vector3(x + 4f, rect.y + rect.height * 0.5f, 0f);
                Vector3 bottom = new Vector3(x, rect.yMax, 0f);
                Vector3 left = new Vector3(x - 4f, rect.y + rect.height * 0.5f, 0f);

                Handles.color = new Color(0.3f, 0.6f, 1f, 0.9f);
                Handles.DrawAAConvexPolygon(top, right, bottom, left);

                // Tooltip on hover
                Rect hitRect = new Rect(x - 6f, rect.y, 12f, rect.height);
                if (hitRect.Contains(Event.current.mousePosition))
                {
                    EditorGUI.LabelField(new Rect(x + 8f, rect.y - 4f, 200f, 20f),
                        kvp.Value, EditorStyles.helpBox);
                }
            }
        }

        // ---------------------------------------------------------------
        // Annotation Editor
        // ---------------------------------------------------------------

        private void DrawAnnotationEditor()
        {
            if (!_isEditingAnnotation) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label($"Annotation for Frame {_editingAnnotationFrame}", EditorStyles.boldLabel);

            _editingAnnotationText = EditorGUILayout.TextField("Note:", _editingAnnotationText);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Save", GUILayout.Width(60)))
            {
                if (string.IsNullOrEmpty(_editingAnnotationText))
                {
                    _annotations.Remove(_editingAnnotationFrame);
                }
                else
                {
                    _annotations[_editingAnnotationFrame] = _editingAnnotationText;
                }
                _isEditingAnnotation = false;
            }

            if (GUILayout.Button("Delete", GUILayout.Width(60)))
            {
                _annotations.Remove(_editingAnnotationFrame);
                _isEditingAnnotation = false;
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(60)))
            {
                _isEditingAnnotation = false;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------
        // Snapshot Details
        // ---------------------------------------------------------------

        private void DrawSnapshotDetails()
        {
            if (_playbackFrame == -1 || _playbackFrame >= _snapshots.Count) return;

            var snapshot = _snapshots[_playbackFrame];

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label($"Snapshot Frame: {_playbackFrame}", EditorStyles.boldLabel);
            GUILayout.Label($"Timestamp: {snapshot.Timestamp:F3}");
            GUILayout.Label($"Entities: {snapshot.EntityCount}");

            if (_annotations.ContainsKey(_playbackFrame))
            {
                GUILayout.Label($"Annotation: {_annotations[_playbackFrame]}", EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------
        // Diff Panel
        // ---------------------------------------------------------------

        private void DrawDiffPanel()
        {
            if (_playbackFrame <= 0 || _playbackFrame >= _snapshots.Count) return;

            _showDiffPanel = EditorGUILayout.Foldout(_showDiffPanel, "Snapshot Diff (vs Previous Frame)", true);
            if (!_showDiffPanel) return;

            var diff = GetOrComputeDiff(_playbackFrame);
            if (!diff.HasValue) return;

            var d = diff.Value;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _diffScrollPos = EditorGUILayout.BeginScrollView(_diffScrollPos, GUILayout.MaxHeight(200f));

            // Created entities
            if (d.CreatedEntities != null && d.CreatedEntities.Length > 0)
            {
                GUILayout.Label($"Entities Created ({d.CreatedEntities.Length}):", EditorStyles.boldLabel);
                foreach (int id in d.CreatedEntities)
                {
                    EditorGUILayout.LabelField($"  + Entity {id}");
                }
            }

            // Destroyed entities
            if (d.DestroyedEntities != null && d.DestroyedEntities.Length > 0)
            {
                GUILayout.Label($"Entities Destroyed ({d.DestroyedEntities.Length}):", EditorStyles.boldLabel);
                foreach (int id in d.DestroyedEntities)
                {
                    EditorGUILayout.LabelField($"  - Entity {id}");
                }
            }

            // Component changes
            if (d.ComponentChanges != null && d.ComponentChanges.Count > 0)
            {
                GUILayout.Label($"Component Changes ({d.ComponentChanges.Count} entities):", EditorStyles.boldLabel);
                foreach (var entityKvp in d.ComponentChanges)
                {
                    EditorGUILayout.LabelField($"  Entity {entityKvp.Key}:");
                    foreach (var change in entityKvp.Value)
                    {
                        EditorGUILayout.LabelField(
                            $"    {change.ComponentType.Name}.{change.FieldName}: {change.OldValue} -> {change.NewValue}");
                    }
                }
            }

            if ((d.CreatedEntities == null || d.CreatedEntities.Length == 0) &&
                (d.DestroyedEntities == null || d.DestroyedEntities.Length == 0) &&
                (d.ComponentChanges == null || d.ComponentChanges.Count == 0))
            {
                GUILayout.Label("No changes detected.");
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------
        // Diff Computation
        // ---------------------------------------------------------------

        private SnapshotDiff? GetOrComputeDiff(int frame)
        {
            if (frame <= 0 || frame >= _snapshots.Count) return null;
            if (_cachedDiffFrame == frame && _cachedDiff.HasValue) return _cachedDiff;

            _cachedDiff = ComputeDiff(_snapshots[frame - 1], _snapshots[frame]);
            _cachedDiffFrame = frame;
            return _cachedDiff;
        }

        private void InvalidateDiffCache()
        {
            _cachedDiffFrame = -1;
            _cachedDiff = null;
        }

        private SnapshotDiff ComputeDiff(WorldSnapshot previous, WorldSnapshot current)
        {
            var diff = new SnapshotDiff();

            var prevSet = previous.ActiveIndices != null ? new HashSet<int>(previous.ActiveIndices) : new HashSet<int>();
            var currSet = current.ActiveIndices != null ? new HashSet<int>(current.ActiveIndices) : new HashSet<int>();

            // Created: in current but not in previous
            var created = new List<int>();
            foreach (int id in currSet)
            {
                if (!prevSet.Contains(id)) created.Add(id);
            }
            diff.CreatedEntities = created.ToArray();

            // Destroyed: in previous but not in current
            var destroyed = new List<int>();
            foreach (int id in prevSet)
            {
                if (!currSet.Contains(id)) destroyed.Add(id);
            }
            diff.DestroyedEntities = destroyed.ToArray();

            // Component changes on entities present in both snapshots
            diff.ComponentChanges = new Dictionary<int, List<ComponentChange>>();
            var prevData = previous.EntityData;
            var currData = current.EntityData;

            if (prevData != null && currData != null)
            {
                foreach (int id in currSet)
                {
                    if (!prevSet.Contains(id)) continue; // skip newly created

                    Dictionary<Type, object> prevComps = null;
                    Dictionary<Type, object> currComps = null;
                    prevData.TryGetValue(id, out prevComps);
                    currData.TryGetValue(id, out currComps);

                    if (prevComps == null && currComps == null) continue;

                    var changes = new List<ComponentChange>();

                    // Gather all component types from both
                    var allTypes = new HashSet<Type>();
                    if (prevComps != null) foreach (var t in prevComps.Keys) allTypes.Add(t);
                    if (currComps != null) foreach (var t in currComps.Keys) allTypes.Add(t);

                    foreach (var type in allTypes)
                    {
                        object prevVal = null;
                        object currVal = null;
                        if (prevComps != null) prevComps.TryGetValue(type, out prevVal);
                        if (currComps != null) currComps.TryGetValue(type, out currVal);

                        if (prevVal == null && currVal != null)
                        {
                            changes.Add(new ComponentChange
                            {
                                ComponentType = type,
                                FieldName = "(added)",
                                OldValue = "null",
                                NewValue = currVal.ToString()
                            });
                        }
                        else if (prevVal != null && currVal == null)
                        {
                            changes.Add(new ComponentChange
                            {
                                ComponentType = type,
                                FieldName = "(removed)",
                                OldValue = prevVal.ToString(),
                                NewValue = "null"
                            });
                        }
                        else if (prevVal != null && currVal != null)
                        {
                            // Compare fields via reflection
                            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            foreach (var field in fields)
                            {
                                var oldFieldVal = field.GetValue(prevVal);
                                var newFieldVal = field.GetValue(currVal);

                                string oldStr = oldFieldVal != null ? oldFieldVal.ToString() : "null";
                                string newStr = newFieldVal != null ? newFieldVal.ToString() : "null";

                                if (oldStr != newStr)
                                {
                                    changes.Add(new ComponentChange
                                    {
                                        ComponentType = type,
                                        FieldName = field.Name,
                                        OldValue = oldStr,
                                        NewValue = newStr
                                    });
                                }
                            }
                        }
                    }

                    if (changes.Count > 0)
                    {
                        diff.ComponentChanges[id] = changes;
                    }
                }
            }

            return diff;
        }

        // ---------------------------------------------------------------
        // Core Recording / Replay
        // ---------------------------------------------------------------

        private void StartReplayIfNeeded()
        {
            if (!_isReplaying)
            {
                _isReplaying = true;
                _isRecording = false;

                if (World.Current != null)
                {
                    _liveSnapshot = new WorldSnapshot();
                    _liveSnapshot.Capture(World.Current);
                }

                if (_playbackFrame == -1)
                {
                    _playbackFrame = _snapshots.Count - 1;
                }
            }
        }

        private void RestoreLiveState()
        {
            _isReplaying = false;
            _isPlayingRecording = false;
            _playbackFrame = -1;
            InvalidateDiffCache();

            if (_liveSnapshot != null && World.Current != null)
            {
                _liveSnapshot.Restore(World.Current);
                _liveSnapshot = null;
            }
        }

        private void Step(int frames)
        {
            if (_snapshots.Count == 0) return;

            int current = _playbackFrame == -1 ? _snapshots.Count - 1 : _playbackFrame;
            int target = Mathf.Clamp(current + frames, 0, _snapshots.Count - 1);

            _playbackFrame = target;
            RestoreSnapshot(_snapshots[_playbackFrame]);
            InvalidateDiffCache();
        }

        private void RecordSnapshot()
        {
            if (World.Current == null) return;

            var snapshot = new WorldSnapshot();
            snapshot.Capture(World.Current);

            // Entity creation/destruction relative to the previous frame is fixed the moment
            // the snapshot is taken, so the timeline marker row can just read the result.
            if (_snapshots.Count > 0)
            {
                snapshot.ComputeLifecycleFlags(_snapshots[_snapshots.Count - 1]);
            }

            _snapshots.Add(snapshot);
            _retainedBoxedComponents += snapshot.BoxedComponentCount;

            // Always keep the newest snapshot, however large it is - dropping it would leave
            // the timeline with nothing to show.
            while (_snapshots.Count > _maxSnapshots ||
                   (_snapshots.Count > 1 && _retainedBoxedComponents > MaxRetainedBoxedComponents))
            {
                EvictOldestSnapshot();
            }
        }

        private void EvictOldestSnapshot()
        {
            // Shift annotation keys down by 1 when removing oldest snapshot
            var shifted = new Dictionary<int, string>();
            foreach (var kvp in _annotations)
            {
                int newKey = kvp.Key - 1;
                if (newKey >= 0)
                {
                    shifted[newKey] = kvp.Value;
                }
            }
            _annotations = shifted;

            _retainedBoxedComponents -= _snapshots[0].BoxedComponentCount;
            if (_retainedBoxedComponents < 0) _retainedBoxedComponents = 0;

            _snapshots.RemoveAt(0);
            if (_playbackFrame > 0) _playbackFrame--;

            // The diff cache is keyed by frame index, and every index just shifted down by
            // one, so keeping it would show the previous frame's diff.
            InvalidateDiffCache();
        }

        private void RestoreSnapshot(WorldSnapshot snapshot)
        {
            if (World.Current == null) return;
            snapshot.Restore(World.Current);
        }
    }

    public class WorldSnapshot
    {
        public double Timestamp;
        public int EntityCount;

        public int NextEntityIndex;
        public int[] ActiveIndices;
        public int[] Versions;

        /// <summary>
        /// True when this snapshot contains entities that did not exist in the preceding one.
        /// Set by <see cref="ComputeLifecycleFlags"/> at capture time.
        /// </summary>
        public bool HasEntityCreations;

        /// <summary>
        /// True when the preceding snapshot contained entities that are gone from this one.
        /// Set by <see cref="ComputeLifecycleFlags"/> at capture time.
        /// </summary>
        public bool HasEntityDestructions;

        /// <summary>
        /// Number of boxed component values this snapshot holds. Used by the window to bound
        /// total retained memory, which the snapshot count on its own does not.
        /// </summary>
        public int BoxedComponentCount;

        private Dictionary<int, Dictionary<Type, object>> _entityData = new Dictionary<int, Dictionary<Type, object>>();

        /// <summary>
        /// Provides read access to entity data for diff computation.
        /// </summary>
        internal Dictionary<int, Dictionary<Type, object>> EntityData => _entityData;

        public void Capture(World world)
        {
            Timestamp = EditorApplication.timeSinceStartup;
            var entityManager = world.EntityManager;
            EntityCount = entityManager.EntityCount;

            entityManager.CaptureState(out NextEntityIndex, out ActiveIndices, out Versions);

            BoxedComponentCount = 0;

            foreach (var entityId in ActiveIndices)
            {
                var components = new Dictionary<Type, object>();
                foreach (var type in entityManager.Store.GetComponentTypes())
                {
                    if (entityManager.Store.HasComponent(entityId, type))
                    {
                        var value = entityManager.Store.GetComponentBoxed(entityId, type);
                        if (value != null)
                        {
                            components[type] = value;
                            BoxedComponentCount++;
                        }
                    }
                }
                _entityData[entityId] = components;
            }
        }

        /// <summary>
        /// Records whether entities were created or destroyed between <paramref name="previous"/>
        /// and this snapshot. Done once per capture so the timeline does not have to diff
        /// entity index arrays on every repaint.
        /// </summary>
        internal void ComputeLifecycleFlags(WorldSnapshot previous)
        {
            HasEntityCreations = false;
            HasEntityDestructions = false;

            var prevIndices = previous?.ActiveIndices;
            var currIndices = ActiveIndices;

            if (prevIndices == null || currIndices == null) return;

            var prevSet = new HashSet<int>(prevIndices);
            foreach (int id in currIndices)
            {
                if (!prevSet.Contains(id)) { HasEntityCreations = true; break; }
            }

            var currSet = new HashSet<int>(currIndices);
            foreach (int id in prevIndices)
            {
                if (!currSet.Contains(id)) { HasEntityDestructions = true; break; }
            }
        }

        public void Restore(World world)
        {
            var entityManager = world.EntityManager;

            entityManager.RestoreState(NextEntityIndex, ActiveIndices, Versions);

            foreach (var kvp in _entityData)
            {
                int entityId = kvp.Key;
                var components = kvp.Value;

                foreach (var compKvp in components)
                {
                    var type = compKvp.Key;
                    var value = compKvp.Value;
                    entityManager.Store.SetComponentBoxed(entityId, type, value);
                }
            }
        }
    }
}

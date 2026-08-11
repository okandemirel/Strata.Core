using System;
using System.Collections.Generic;
using System.Reflection;
using Strada.Core.Sync;
using Strada.Core.Patterns;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for EntityMediator components that displays:
    /// - All active ComponentBindings
    /// - Binding state (synced/error)
    /// - Force Sync and Force Push buttons
    /// Requirements: 10.1, 10.3, 10.4, 10.5
    /// </summary>
    [CustomEditor(typeof(View), true)]
    public class EntityMediatorInspector : UnityEditor.Editor
    {
        private static readonly Color SyncedColor = new Color(0.2f, 0.8f, 0.3f);
        private static readonly Color ErrorColor = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color NotSyncedColor = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color EntityDestroyedColor = new Color(0.8f, 0.5f, 0.2f);

        private bool _bindingsFoldout = true;
        private object _mediator;
        private IReadOnlyList<IComponentBinding> _bindings;
        private MethodInfo _syncMethod;
        private MethodInfo _pushMethod;
        private PropertyInfo _isBoundProperty;
        private PropertyInfo _bindingsProperty;

        // The reflection results depend only on the type, and this inspector repaints every
        // editor frame in Play Mode, so resolving them per repaint was pure waste.
        private Type _resolvedMediatorType;
        private static readonly Dictionary<Type, FieldInfo> MediatorFieldByViewType =
            new Dictionary<Type, FieldInfo>();

        private static GUIStyle _miniLabelStyle;

        private double _lastRepaintTime;
        private const double RepaintIntervalSeconds = 0.05;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (!Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("EntityMediator bindings are only available during Play Mode.", MessageType.Info);
                return;
            }

            RefreshMediatorReference();

            if (_mediator == null)
            {
                return;
            }

            EditorGUILayout.Space();
            DrawMediatorSection();
        }

        private void RefreshMediatorReference()
        {
            var view = target as View;
            if (view == null) return;

            _mediator = FindMediatorForView(view);

            if (_mediator == null)
            {
                _bindings = null;
                return;
            }

            var mediatorType = _mediator.GetType();
            if (mediatorType != _resolvedMediatorType)
            {
                _resolvedMediatorType = mediatorType;
                _bindingsProperty = mediatorType.GetProperty("Bindings",
                    BindingFlags.Public | BindingFlags.Instance);
                _isBoundProperty = mediatorType.GetProperty("IsBound",
                    BindingFlags.Public | BindingFlags.Instance);
                _syncMethod = mediatorType.GetMethod("SyncBindings",
                    BindingFlags.Public | BindingFlags.Instance);
                _pushMethod = mediatorType.GetMethod("PushBindings",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            _bindings = _bindingsProperty?.GetValue(_mediator) as IReadOnlyList<IComponentBinding>;
        }

        private object FindMediatorForView(View view)
        {
            // There is deliberately no MediatorRegistry lookup here. MediatorRegistry exposes no
            // static Instance and no GetMediatorForView, so the registry branch that used to sit
            // in front of this was unreachable and only cost a Type.GetType plus two member
            // lookups per repaint. Restore it only alongside a real registry API.
            var viewType = view.GetType();

            if (!MediatorFieldByViewType.TryGetValue(viewType, out var mediatorField))
            {
                var fields = viewType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                foreach (var field in fields)
                {
                    // An assignability test, not a Name.Contains("Mediator") substring match:
                    // the latter binds to unrelated fields such as _mediatorPrefab.
                    if (IsEntityMediatorType(field.FieldType))
                    {
                        mediatorField = field;
                        break;
                    }
                }

                MediatorFieldByViewType[viewType] = mediatorField;
            }

            return mediatorField?.GetValue(view);
        }

        private static bool IsEntityMediatorType(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(EntityMediator<>))
                    return true;
            }

            return false;
        }

        private void DrawMediatorSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("EntityMediator", EditorStyles.boldLabel);

            bool isBound = _isBoundProperty != null && (bool)_isBoundProperty.GetValue(_mediator);

            DrawColoredMiniLabel(isBound ? "Bound" : "Not Bound", isBound ? SyncedColor : NotSyncedColor, GUILayout.Width(60));

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = isBound;
            if (GUILayout.Button(new GUIContent("Force Sync", "Manually trigger SyncBindings to update view from ECS"), 
                GUILayout.Height(24)))
            {
                _syncMethod?.Invoke(_mediator, null);
                Repaint();
            }

            if (GUILayout.Button(new GUIContent("Force Push", "Manually trigger PushBindings to update ECS from view"), 
                GUILayout.Height(24)))
            {
                _pushMethod?.Invoke(_mediator, null);
                Repaint();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            if (_bindings != null && _bindings.Count > 0)
            {
                EditorGUILayout.Space();
                _bindingsFoldout = EditorGUILayout.Foldout(_bindingsFoldout, 
                    $"Component Bindings ({_bindings.Count})", true);

                if (_bindingsFoldout)
                {
                    EditorGUI.indentLevel++;
                    foreach (var binding in _bindings)
                    {
                        DrawBindingEntry(binding);
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else if (isBound)
            {
                EditorGUILayout.HelpBox("No component bindings registered.", MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawBindingEntry(IComponentBinding binding)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            var statusRect = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(12));
            statusRect.y += 4;
            var statusColor = GetStatusColor(binding.SyncState);
            EditorGUI.DrawRect(statusRect, statusColor);

            var componentName = binding.ComponentType?.Name ?? "Unknown";
            EditorGUILayout.LabelField(componentName, GUILayout.MinWidth(100));

            DrawColoredMiniLabel(binding.SyncState.ToString(), statusColor, GUILayout.Width(80));

            if (binding.IsDirty)
            {
                DrawColoredMiniLabel("*", new Color(0.9f, 0.7f, 0.2f), GUILayout.Width(10));
            }

            EditorGUILayout.EndHorizontal();

            if (binding.SyncState == BindingSyncState.Error && !string.IsNullOrEmpty(binding.LastError))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(binding.LastError, MessageType.Error);
                EditorGUI.indentLevel--;
            }
        }

        private static void DrawColoredMiniLabel(string text, Color color, params GUILayoutOption[] options)
        {
            // Reused rather than allocated per call: this runs at least twice per binding per
            // repaint, and the inspector repaints continuously in Play Mode. Created lazily
            // because EditorStyles is not available during static initialisation.
            if (_miniLabelStyle == null)
                _miniLabelStyle = new GUIStyle(EditorStyles.miniLabel);

            _miniLabelStyle.normal.textColor = color;
            EditorGUILayout.LabelField(text, _miniLabelStyle, options);
        }

        private Color GetStatusColor(BindingSyncState state)
        {
            return state switch
            {
                BindingSyncState.Synced => SyncedColor,
                BindingSyncState.Error => ErrorColor,
                BindingSyncState.EntityDestroyed => EntityDestroyedColor,
                _ => NotSyncedColor
            };
        }

        public override bool RequiresConstantRepaint()
        {
            if (!Application.isPlaying || _mediator == null)
                return false;

            // Gated to ~20 Hz. Every repaint walks the binding list and re-reads the mediator
            // through reflection, and a debug read-out does not need one frame of latency.
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastRepaintTime < RepaintIntervalSeconds)
                return false;

            _lastRepaintTime = now;
            return true;
        }
    }
}

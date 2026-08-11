using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Strada.Core.Modules;
using Strada.Core.ECS.World;

namespace Strada.Core.Editor.PropertyDrawers
{
    /// <summary>
    /// Property drawer for SystemEntry that provides a compact, informative Inspector view.
    /// </summary>
    [CustomPropertyDrawer(typeof(SystemEntry))]
    public class SystemEntryDrawer : PropertyDrawer
    {
        private const float EnabledToggleWidth = 18f;
        private const float PhaseWidth = 85f;
        private const float OrderWidth = 40f;
        private const float Spacing = 4f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var descriptionProp = property.FindPropertyRelative("_description");
            bool hasDescription = !string.IsNullOrEmpty(descriptionProp.stringValue);

            return EditorGUIUtility.singleLineHeight + (hasDescription ? EditorGUIUtility.singleLineHeight : 0);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabledProp = property.FindPropertyRelative("_enabled");
            var systemTypeProp = property.FindPropertyRelative("_systemType");
            var phaseProp = property.FindPropertyRelative("_phase");
            var orderProp = property.FindPropertyRelative("_order");
            var descriptionProp = property.FindPropertyRelative("_description");

            var lineRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            var toggleRect = new Rect(lineRect.x, lineRect.y, EnabledToggleWidth, lineRect.height);
            enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);

            float remainingWidth = lineRect.width - EnabledToggleWidth - PhaseWidth - OrderWidth - Spacing * 3;
            var typeRect = new Rect(toggleRect.xMax + Spacing, lineRect.y, remainingWidth, lineRect.height);

            var assemblyQualifiedNameProp = systemTypeProp.FindPropertyRelative("_assemblyQualifiedName");
            var typeName = "(None)";
            if (!string.IsNullOrEmpty(assemblyQualifiedNameProp.stringValue))
            {
                var type = System.Type.GetType(assemblyQualifiedNameProp.stringValue);
                typeName = type?.Name ?? "(Invalid Type)";
            }

            var previousColor = GUI.color;
            if (!enabledProp.boolValue)
            {
                GUI.color = new Color(1, 1, 1, 0.5f);
            }

            if (EditorGUI.DropdownButton(typeRect, new GUIContent(typeName, GetTooltip(property)), FocusType.Keyboard))
            {
                ShowSystemTypeMenu(typeRect, assemblyQualifiedNameProp);
            }

            var phaseRect = new Rect(typeRect.xMax + Spacing, lineRect.y, PhaseWidth, lineRect.height);
            EditorGUI.PropertyField(phaseRect, phaseProp, GUIContent.none);

            var orderRect = new Rect(phaseRect.xMax + Spacing, lineRect.y, OrderWidth, lineRect.height);
            EditorGUI.PropertyField(orderRect, orderProp, GUIContent.none);

            GUI.color = previousColor;

            if (!string.IsNullOrEmpty(descriptionProp.stringValue))
            {
                var descRect = new Rect(
                    position.x + EnabledToggleWidth + Spacing,
                    position.y + EditorGUIUtility.singleLineHeight,
                    position.width - EnabledToggleWidth - Spacing,
                    EditorGUIUtility.singleLineHeight);

                var oldColor = GUI.color;
                GUI.color = new Color(0.7f, 0.7f, 0.7f, 1f);
                EditorGUI.LabelField(descRect, descriptionProp.stringValue, EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            EditorGUI.EndProperty();
        }

        private string GetTooltip(SerializedProperty property)
        {
            var descriptionProp = property.FindPropertyRelative("_description");
            var categoryProp = property.FindPropertyRelative("_category");

            var tooltip = "";
            if (!string.IsNullOrEmpty(categoryProp.stringValue))
            {
                tooltip += $"Category: {categoryProp.stringValue}\n";
            }
            if (!string.IsNullOrEmpty(descriptionProp.stringValue))
            {
                tooltip += descriptionProp.stringValue;
            }

            return string.IsNullOrEmpty(tooltip) ? null : tooltip;
        }

        private void ShowSystemTypeMenu(Rect position, SerializedProperty assemblyQualifiedNameProp)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("(None)"), string.IsNullOrEmpty(assemblyQualifiedNameProp.stringValue), () =>
            {
                assemblyQualifiedNameProp.stringValue = "";
                assemblyQualifiedNameProp.serializedObject.ApplyModifiedProperties();
            });

            menu.AddSeparator("");

            var systems = RuntimeSystemDiscovery.DiscoverSystems();
            var groupedSystems = new Dictionary<string, List<Modules.SystemInfo>>();

            foreach (var system in systems)
            {
                var key = string.IsNullOrEmpty(system.Category) ?
                    (string.IsNullOrEmpty(system.Module) ? "Other" : system.Module) :
                    system.Category;

                if (!groupedSystems.TryGetValue(key, out var list))
                {
                    list = new List<Modules.SystemInfo>();
                    groupedSystems[key] = list;
                }
                list.Add(system);
            }

            foreach (var group in groupedSystems)
            {
                foreach (var system in group.Value)
                {
                    var menuPath = $"{group.Key}/{system.Type.Name}";
                    var isSelected = assemblyQualifiedNameProp.stringValue == system.Type.AssemblyQualifiedName;
                    var tooltip = system.Description;

                    menu.AddItem(new GUIContent(menuPath, tooltip), isSelected, () =>
                    {
                        assemblyQualifiedNameProp.stringValue = system.Type.AssemblyQualifiedName;
                        assemblyQualifiedNameProp.serializedObject.ApplyModifiedProperties();
                    });
                }
            }

            menu.DropDown(position);
        }
    }

    /// <summary>
    /// Property drawer for ServiceEntry.
    /// </summary>
    [CustomPropertyDrawer(typeof(ServiceEntry))]
    public class ServiceEntryDrawer : PropertyDrawer
    {
        private const float EnabledToggleWidth = 18f;
        private const float LifetimeWidth = 80f;
        private const float Spacing = 4f;
        private const float ArrowWidth = 20f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabledProp = property.FindPropertyRelative("_enabled");
            var interfaceTypeProp = property.FindPropertyRelative("_interfaceType");
            var implementationTypeProp = property.FindPropertyRelative("_implementationType");
            var lifetimeProp = property.FindPropertyRelative("_lifetime");

            var toggleRect = new Rect(position.x, position.y, EnabledToggleWidth, position.height);
            enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);

            float typeWidth = (position.width - EnabledToggleWidth - ArrowWidth - LifetimeWidth - Spacing * 4) / 2;

            var interfaceRect = new Rect(toggleRect.xMax + Spacing, position.y, typeWidth, position.height);
            DrawTypeField(interfaceRect, interfaceTypeProp, enabledProp.boolValue, interfacesOnly: true);

            var arrowRect = new Rect(interfaceRect.xMax, position.y, ArrowWidth, position.height);
            EditorGUI.LabelField(arrowRect, "→", EditorStyles.centeredGreyMiniLabel);

            // The implementation slot only ever accepts something that implements the selected
            // interface, so constrain the menu to that instead of offering every type in the
            // project.
            var selectedInterface = ResolveType(interfaceTypeProp);

            var implRect = new Rect(arrowRect.xMax, position.y, typeWidth, position.height);
            DrawTypeField(implRect, implementationTypeProp, enabledProp.boolValue,
                interfacesOnly: false, requiredBase: selectedInterface);

            var lifetimeRect = new Rect(implRect.xMax + Spacing, position.y, LifetimeWidth, position.height);
            EditorGUI.PropertyField(lifetimeRect, lifetimeProp, GUIContent.none);

            EditorGUI.EndProperty();
        }

        private static System.Type ResolveType(SerializedProperty typeProp)
        {
            var nameProp = typeProp?.FindPropertyRelative("_assemblyQualifiedName");
            if (nameProp == null || string.IsNullOrEmpty(nameProp.stringValue))
                return null;

            return System.Type.GetType(nameProp.stringValue);
        }

        private void DrawTypeField(Rect rect, SerializedProperty typeProp, bool enabled,
            bool interfacesOnly, System.Type requiredBase = null)
        {
            var assemblyQualifiedNameProp = typeProp.FindPropertyRelative("_assemblyQualifiedName");
            var typeName = "(None)";
            if (!string.IsNullOrEmpty(assemblyQualifiedNameProp.stringValue))
            {
                var type = System.Type.GetType(assemblyQualifiedNameProp.stringValue);
                typeName = type?.Name ?? "(Invalid)";
            }

            var previousColor = GUI.color;
            if (!enabled)
            {
                GUI.color = new Color(1, 1, 1, 0.5f);
            }

            if (EditorGUI.DropdownButton(rect, new GUIContent(typeName), FocusType.Keyboard))
            {
                ShowTypeMenu(rect, assemblyQualifiedNameProp, interfacesOnly, requiredBase);
            }

            GUI.color = previousColor;
        }

        private static System.Collections.Generic.List<System.Type> _cachedTypes;

        /// <summary>
        /// All user-assembly interfaces and concrete classes, scanned once.
        /// </summary>
        /// <remarks>
        /// This used to run a full AppDomain scan - GetTypes() on every user assembly - on every
        /// dropdown click. The set can only change on a domain reload, which clears this static.
        /// </remarks>
        private static System.Collections.Generic.List<System.Type> GetCandidateTypes()
        {
            if (_cachedTypes != null)
                return _cachedTypes;

            var types = new System.Collections.Generic.List<System.Type>();

            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                var assemblyName = assembly.GetName().Name;
                if (assemblyName.StartsWith("System", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Microsoft", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Unity.", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("UnityEngine", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("UnityEditor", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("mscorlib", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("netstandard", System.StringComparison.Ordinal) ||
                    assemblyName.StartsWith("Mono.", System.StringComparison.Ordinal))
                    continue;

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        AddCandidate(types, type);
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                {
                    foreach (var type in ex.Types)
                    {
                        AddCandidate(types, type);
                    }
                }
                catch { }
            }

            _cachedTypes = types;
            return _cachedTypes;
        }

        private static void AddCandidate(System.Collections.Generic.List<System.Type> types, System.Type type)
        {
            if (type == null) return;

            if (type.IsInterface)
            {
                if (type.Namespace == null || !type.Namespace.StartsWith("System", System.StringComparison.Ordinal))
                    types.Add(type);
                return;
            }

            if (type.IsClass && !type.IsAbstract)
                types.Add(type);
        }

        private void ShowTypeMenu(Rect position, SerializedProperty property,
            bool interfacesOnly, System.Type requiredBase)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("(None)"), string.IsNullOrEmpty(property.stringValue), () =>
            {
                property.stringValue = "";
                property.serializedObject.ApplyModifiedProperties();
            });
            menu.AddSeparator("");

            var grouped = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Type>>();
            foreach (var type in GetCandidateTypes())
            {
                if (interfacesOnly != type.IsInterface)
                    continue;

                if (requiredBase != null && !requiredBase.IsAssignableFrom(type))
                    continue;

                var ns = type.Namespace ?? "Global";
                if (!grouped.TryGetValue(ns, out var list))
                {
                    list = new System.Collections.Generic.List<System.Type>();
                    grouped[ns] = list;
                }
                list.Add(type);
            }

            menu.AddDisabledItem(new GUIContent(interfacesOnly ? "--- Interfaces ---" : "--- Classes ---"));

            foreach (var kvp in grouped)
            {
                foreach (var type in kvp.Value)
                {
                    var menuPath = string.IsNullOrEmpty(kvp.Key) ? type.Name : $"{kvp.Key}/{type.Name}";
                    var isSelected = property.stringValue == type.AssemblyQualifiedName;
                    menu.AddItem(new GUIContent(menuPath), isSelected, () =>
                    {
                        property.stringValue = type.AssemblyQualifiedName;
                        property.serializedObject.ApplyModifiedProperties();
                    });
                }
            }

            menu.DropDown(position);
        }
    }

    /// <summary>
    /// Property drawer for ModuleEntry.
    /// </summary>
    [CustomPropertyDrawer(typeof(ModuleEntry))]
    public class ModuleEntryDrawer : PropertyDrawer
    {
        private const float EnabledToggleWidth = 18f;
        private const float PriorityWidth = 50f;
        private const float Spacing = 4f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var enabledProp = property.FindPropertyRelative("_enabled");
            var configProp = property.FindPropertyRelative("_config");

            var toggleRect = new Rect(position.x, position.y, EnabledToggleWidth, position.height);
            enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);

            var previousColor = GUI.color;
            if (!enabledProp.boolValue)
            {
                GUI.color = new Color(1, 1, 1, 0.5f);
            }

            float configWidth = position.width - EnabledToggleWidth - PriorityWidth - Spacing * 2;
            var configRect = new Rect(toggleRect.xMax + Spacing, position.y, configWidth, position.height);
            EditorGUI.PropertyField(configRect, configProp, GUIContent.none);

            var priorityRect = new Rect(configRect.xMax + Spacing, position.y, PriorityWidth, position.height);
            if (configProp.objectReferenceValue is ModuleConfig moduleConfig)
            {
                EditorGUI.LabelField(priorityRect, $"P: {moduleConfig.Priority}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.LabelField(priorityRect, "P: -", EditorStyles.miniLabel);
            }

            GUI.color = previousColor;

            EditorGUI.EndProperty();
        }
    }
}

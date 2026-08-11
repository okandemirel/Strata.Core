using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Strada.Core.Editor.PropertyDrawers
{
    /// <summary>
    /// Enhanced property drawer for ReactiveProperty fields.
    /// Features:
    /// - Live indicator showing subscription status (green = has subscribers, gray = no subscribers)
    /// - Subscriber count display during Play Mode
    /// - "Notify" button to force notification to all subscribers
    /// Requirements: 7.1, 7.3, 7.5
    /// </summary>
    [CustomPropertyDrawer(typeof(Sync.ReactiveProperty<>), true)]
    public class ReactivePropertyDrawer : PropertyDrawer
    {
        private const float LiveIndicatorWidth = 12f;
        private const float SubscriberCountWidth = 30f;
        private const float ButtonWidth = 50f;
        private const float Spacing = 4f;

        private static readonly Color ActiveSubscribersColor = new Color(0.2f, 0.8f, 0.3f);
        private static readonly Color NoSubscribersColor = new Color(0.8f, 0.6f, 0.2f);
        private static readonly Color InactiveColor = new Color(0.5f, 0.5f, 0.5f);

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var valueProperty = property.FindPropertyRelative("_value");
            if (valueProperty == null)
            {
                EditorGUI.LabelField(position, label.text, "Cannot find _value field");
                EditorGUI.EndProperty();
                return;
            }

            bool isPlaying = Application.isPlaying;
            int subscriberCount = 0;
            bool hasSubscribers = false;

            if (isPlaying)
            {
                subscriberCount = GetSubscriberCount(property);
                hasSubscribers = subscriberCount > 0;
            }

            float currentX = position.x;

            var labelRect = new Rect(currentX, position.y, EditorGUIUtility.labelWidth, position.height);
            currentX += EditorGUIUtility.labelWidth + Spacing;

            var indicatorRect = new Rect(currentX, position.y + (position.height - LiveIndicatorWidth) / 2, LiveIndicatorWidth, LiveIndicatorWidth);
            currentX += LiveIndicatorWidth + Spacing;

            Rect subscriberCountRect = default;
            if (isPlaying)
            {
                subscriberCountRect = new Rect(currentX, position.y, SubscriberCountWidth, position.height);
                currentX += SubscriberCountWidth + Spacing;
            }

            var buttonRect = new Rect(position.xMax - ButtonWidth, position.y, ButtonWidth, position.height);

            var valueRect = new Rect(currentX, position.y, buttonRect.x - currentX - Spacing, position.height);

            EditorGUI.LabelField(labelRect, label);

            DrawLiveIndicator(indicatorRect, isPlaying, hasSubscribers);

            if (isPlaying)
            {
                var countStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fontStyle = hasSubscribers ? FontStyle.Bold : FontStyle.Normal
                };
                countStyle.normal.textColor = hasSubscribers ? ActiveSubscribersColor : NoSubscribersColor;
                EditorGUI.LabelField(subscriberCountRect, subscriberCount.ToString(), countStyle);
            }

            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(valueRect, valueProperty, GUIContent.none);
            bool changed = EditorGUI.EndChangeCheck();

            if (changed && isPlaying)
            {
                property.serializedObject.ApplyModifiedProperties();
                TryNotifyProperty(property);
            }

            GUI.enabled = isPlaying;
            if (GUI.Button(buttonRect, new GUIContent("Notify", "Force notification to all subscribers with current value")))
            {
                TryNotifyProperty(property);
            }
            GUI.enabled = true;

            EditorGUI.EndProperty();
        }

        private void DrawLiveIndicator(Rect rect, bool isPlaying, bool hasSubscribers)
        {
            Color indicatorColor;
            string tooltip;

            if (!isPlaying)
            {
                indicatorColor = InactiveColor;
                tooltip = "Not in Play Mode";
            }
            else if (hasSubscribers)
            {
                indicatorColor = ActiveSubscribersColor;
                tooltip = "Has active subscribers";
            }
            else
            {
                indicatorColor = NoSubscribersColor;
                tooltip = "No subscribers";
            }

            var oldColor = GUI.color;
            GUI.color = indicatorColor;

            EditorGUI.DrawRect(rect, indicatorColor);

            var borderColor = new Color(indicatorColor.r * 0.7f, indicatorColor.g * 0.7f, indicatorColor.b * 0.7f);
            DrawRectBorder(rect, borderColor, 1);

            GUI.color = oldColor;

            EditorGUI.LabelField(rect, new GUIContent("", tooltip));
        }

        private void DrawRectBorder(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private object GetReactivePropertyInstance(SerializedProperty property)
        {
            var targetObject = property.serializedObject.targetObject;
            return ResolveByPath(targetObject, property.propertyPath);
        }

        private int GetSubscriberCount(SerializedProperty property)
        {
            var reactiveProperty = GetReactivePropertyInstance(property);
            if (reactiveProperty == null) return 0;

            var subscriberCountProperty = reactiveProperty.GetType().GetProperty("SubscriberCount", BindingFlags.Public | BindingFlags.Instance);
            if (subscriberCountProperty == null) return 0;

            return (int)subscriberCountProperty.GetValue(reactiveProperty);
        }

        private void TryNotifyProperty(SerializedProperty property)
        {
            var reactiveProperty = GetReactivePropertyInstance(property);
            if (reactiveProperty == null) return;

            var notifyMethod = reactiveProperty.GetType().GetMethod("Notify", BindingFlags.Public | BindingFlags.Instance);
            notifyMethod?.Invoke(reactiveProperty, null);
        }

        /// <summary>
        /// Walks a SerializedProperty path over the live object graph and returns the
        /// ReactiveProperty instance it addresses, or null.
        /// </summary>
        /// <remarks>
        /// This resolves the instance rather than a FieldInfo because a FieldInfo cannot express
        /// a collection element. Unity spells an element as "_props.Array.data[3]", and the old
        /// code skipped the "Array" and "data[3]" segments without ever unwrapping the element
        /// type - so it then looked for the next segment on List&lt;ReactiveProperty&lt;T&gt;&gt;,
        /// found nothing, and returned null for every ReactiveProperty stored in an array or list.
        /// </remarks>
        private static object ResolveByPath(object root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
                return null;

            object current = root;
            var parts = path.Split('.');

            for (int i = 0; i < parts.Length; i++)
            {
                if (current == null)
                    return null;

                if (IsReactivePropertyType(current.GetType()))
                    return current;

                var part = parts[i];

                if (part == "Array" && i + 1 < parts.Length && parts[i + 1].StartsWith("data[", StringComparison.Ordinal))
                {
                    if (!TryParseElementIndex(parts[i + 1], out var index))
                        return null;

                    current = GetElementAt(current, index);
                    i++;
                    continue;
                }

                var field = FindField(current.GetType(), part);
                if (field == null)
                    return null;

                current = field.GetValue(current);
            }

            return current != null && IsReactivePropertyType(current.GetType()) ? current : null;
        }

        private static bool TryParseElementIndex(string segment, out int index)
        {
            index = 0;
            var open = segment.IndexOf('[');
            var close = segment.IndexOf(']');
            if (open < 0 || close <= open + 1)
                return false;

            return int.TryParse(segment.Substring(open + 1, close - open - 1), out index);
        }

        private static object GetElementAt(object collection, int index)
        {
            // Both T[] and List<T> implement IList, which is all the indexing this needs.
            if (collection is System.Collections.IList list)
                return index >= 0 && index < list.Count ? list[index] : null;

            return null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            // Private fields are not visible through a derived type, so walk the base chain.
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            return null;
        }

        private static bool IsReactivePropertyType(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Sync.ReactiveProperty<>))
                    return true;
            }

            return false;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var valueProperty = property.FindPropertyRelative("_value");
            if (valueProperty == null) return EditorGUIUtility.singleLineHeight;
            return EditorGUI.GetPropertyHeight(valueProperty);
        }
    }

    /// <summary>
    /// Enhanced property drawer for ReactiveCollection fields.
    /// Features:
    /// - Element count badge in header
    /// - Add/remove element buttons
    /// - Expandable element list with drag reordering support
    /// - Clear all button
    /// Requirements: 7.4
    /// </summary>
    [CustomPropertyDrawer(typeof(Sync.ReactiveCollection<>), true)]
    public class ReactiveCollectionDrawer : PropertyDrawer
    {
        private bool _foldout = true;
        private const float ButtonWidth = 20f;
        private const float HeaderButtonWidth = 60f;
        private const float Spacing = 2f;

        private static readonly Color BadgeBackgroundColor = new Color(0.3f, 0.3f, 0.3f);
        private static readonly Color BadgeTextColor = new Color(0.9f, 0.9f, 0.9f);
        private static readonly Color EmptyBadgeColor = new Color(0.6f, 0.6f, 0.6f);

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var itemsProperty = property.FindPropertyRelative("_items");
            if (itemsProperty == null)
            {
                EditorGUI.LabelField(position, label.text, "Cannot find _items field");
                EditorGUI.EndProperty();
                return;
            }

            int count = itemsProperty.arraySize;
            bool isEmpty = count == 0;

            var headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            var foldoutRect = new Rect(headerRect.x, headerRect.y, EditorGUIUtility.labelWidth - 40, headerRect.height);
            _foldout = EditorGUI.Foldout(foldoutRect, _foldout, label, true);

            var badgeContent = new GUIContent(count.ToString());
            var badgeWidth = Mathf.Max(24, EditorStyles.miniLabel.CalcSize(badgeContent).x + 8);
            var badgeRect = new Rect(foldoutRect.xMax + 4, headerRect.y + 2, badgeWidth, headerRect.height - 4);
            DrawCountBadge(badgeRect, count, isEmpty);

            var clearButtonRect = new Rect(headerRect.xMax - HeaderButtonWidth, headerRect.y, HeaderButtonWidth, headerRect.height);
            var addButtonRect = new Rect(clearButtonRect.x - HeaderButtonWidth - Spacing, headerRect.y, HeaderButtonWidth, headerRect.height);

            GUI.enabled = !isEmpty;
            if (GUI.Button(clearButtonRect, new GUIContent("Clear", "Remove all elements")))
            {
                itemsProperty.ClearArray();
                property.serializedObject.ApplyModifiedProperties();
            }
            GUI.enabled = true;

            if (GUI.Button(addButtonRect, new GUIContent("+ Add", "Add new element")))
            {
                itemsProperty.InsertArrayElementAtIndex(count);
                property.serializedObject.ApplyModifiedProperties();
            }

            if (_foldout)
            {
                EditorGUI.indentLevel++;
                var yOffset = EditorGUIUtility.singleLineHeight + Spacing;

                if (isEmpty)
                {
                    var emptyRect = new Rect(position.x + 16, position.y + yOffset, position.width - 16, EditorGUIUtility.singleLineHeight);
                    var emptyStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel);
                    EditorGUI.LabelField(emptyRect, "Collection is empty", emptyStyle);
                    yOffset += EditorGUIUtility.singleLineHeight + Spacing;
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        var element = itemsProperty.GetArrayElementAtIndex(i);
                        var elementHeight = EditorGUI.GetPropertyHeight(element);

                        var rowRect = new Rect(position.x, position.y + yOffset, position.width, elementHeight);

                        var indexRect = new Rect(rowRect.x + 16, rowRect.y, 30, EditorGUIUtility.singleLineHeight);
                        var indexStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
                        indexStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
                        EditorGUI.LabelField(indexRect, $"[{i}]", indexStyle);

                        var elementRect = new Rect(indexRect.xMax + 4, rowRect.y, rowRect.width - indexRect.width - ButtonWidth * 2 - 24, elementHeight);
                        EditorGUI.PropertyField(elementRect, element, GUIContent.none);

                        var moveUpRect = new Rect(elementRect.xMax + Spacing, rowRect.y, ButtonWidth, EditorGUIUtility.singleLineHeight);
                        GUI.enabled = i > 0;
                        if (GUI.Button(moveUpRect, new GUIContent("▲", "Move up")))
                        {
                            itemsProperty.MoveArrayElement(i, i - 1);
                            property.serializedObject.ApplyModifiedProperties();
                        }
                        GUI.enabled = true;

                        var moveDownRect = new Rect(moveUpRect.xMax + 1, rowRect.y, ButtonWidth, EditorGUIUtility.singleLineHeight);
                        GUI.enabled = i < count - 1;
                        if (GUI.Button(moveDownRect, new GUIContent("▼", "Move down")))
                        {
                            itemsProperty.MoveArrayElement(i, i + 1);
                            property.serializedObject.ApplyModifiedProperties();
                        }
                        GUI.enabled = true;

                        var removeRect = new Rect(moveDownRect.xMax + Spacing, rowRect.y, ButtonWidth, EditorGUIUtility.singleLineHeight);
                        if (GUI.Button(removeRect, new GUIContent("×", "Remove element")))
                        {
                            itemsProperty.DeleteArrayElementAtIndex(i);
                            property.serializedObject.ApplyModifiedProperties();
                            break;
                        }

                        yOffset += elementHeight + Spacing;
                    }
                }

                var bottomAddRect = new Rect(position.x + 16, position.y + yOffset, position.width - 16, EditorGUIUtility.singleLineHeight);
                if (GUI.Button(bottomAddRect, new GUIContent("+ Add Element", "Add new element to collection")))
                {
                    itemsProperty.InsertArrayElementAtIndex(count);
                    property.serializedObject.ApplyModifiedProperties();
                }

                EditorGUI.indentLevel--;
            }

            EditorGUI.EndProperty();
        }

        private void DrawCountBadge(Rect rect, int count, bool isEmpty)
        {
            var backgroundColor = isEmpty ? EmptyBadgeColor : BadgeBackgroundColor;
            EditorGUI.DrawRect(rect, backgroundColor);

            var textStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            textStyle.normal.textColor = BadgeTextColor;

            EditorGUI.LabelField(rect, count.ToString(), textStyle);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!_foldout) return EditorGUIUtility.singleLineHeight;

            var itemsProperty = property.FindPropertyRelative("_items");
            if (itemsProperty == null) return EditorGUIUtility.singleLineHeight;

            float height = EditorGUIUtility.singleLineHeight + Spacing;

            if (itemsProperty.arraySize == 0)
            {
                height += EditorGUIUtility.singleLineHeight + Spacing;
            }
            else
            {
                for (int i = 0; i < itemsProperty.arraySize; i++)
                {
                    height += EditorGUI.GetPropertyHeight(itemsProperty.GetArrayElementAtIndex(i)) + Spacing;
                }
            }

            height += EditorGUIUtility.singleLineHeight + Spacing;
            return height;
        }
    }
}

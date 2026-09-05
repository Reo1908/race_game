using UnityEditor;
using UnityEngine;

namespace RevAudio.EditorTools
{
    /// <summary>Two-handle range slider with editable numeric ends, for every Vector2 marked [MinMaxRange].</summary>
    [CustomPropertyDrawer(typeof(MinMaxRangeAttribute))]
    public class RevMinMaxRangeDrawer : PropertyDrawer
    {
        const float FieldWidth = 52f;
        const float Gap = 4f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            MinMaxRangeAttribute range = (MinMaxRangeAttribute)attribute;

            if (property.propertyType != SerializedPropertyType.Vector2)
            {
                EditorGUI.LabelField(position, label.text, "[MinMaxRange] only works on a Vector2.");
                return;
            }

            label = EditorGUI.BeginProperty(position, label, property);
            Rect line = EditorGUI.PrefixLabel(position, label);

            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            Vector2 value = property.vector2Value;
            float min = value.x;
            float max = value.y;

            Rect minRect = new Rect(line.x, line.y, FieldWidth, line.height);
            Rect sliderRect = new Rect(line.x + FieldWidth + Gap, line.y,
                                       Mathf.Max(20f, line.width - 2f * (FieldWidth + Gap)), line.height);
            Rect maxRect = new Rect(line.xMax - FieldWidth, line.y, FieldWidth, line.height);

            EditorGUI.BeginChangeCheck();
            min = EditorGUI.DelayedFloatField(minRect, min);
            EditorGUI.MinMaxSlider(sliderRect, ref min, ref max, range.Min, range.Max);
            max = EditorGUI.DelayedFloatField(maxRect, max);

            if (EditorGUI.EndChangeCheck())
            {
                min = Mathf.Clamp(min, range.Min, range.Max);
                max = Mathf.Clamp(max, range.Min, range.Max);
                if (max < min) max = min;
                if (range.Integer) { min = Mathf.Round(min); max = Mathf.Round(max); }
                property.vector2Value = new Vector2(min, max);
            }

            if (!string.IsNullOrEmpty(range.Unit) && Event.current.type == EventType.Repaint)
            {
                GUI.Label(sliderRect, range.Unit, RevEditorStyles.CenteredMini);
            }

            EditorGUI.indentLevel = indent;
            EditorGUI.EndProperty();
        }
    }

    public static class RevEditorStyles
    {
        static GUIStyle centeredMini;
        public static GUIStyle CenteredMini
        {
            get
            {
                if (centeredMini == null)
                {
                    centeredMini = new GUIStyle(EditorStyles.miniLabel);
                    centeredMini.alignment = TextAnchor.MiddleCenter;
                    Color c = centeredMini.normal.textColor;
                    c.a = 0.45f;
                    centeredMini.normal.textColor = c;
                }
                return centeredMini;
            }
        }

        static GUIStyle sectionHeader;
        public static GUIStyle SectionHeader
        {
            get
            {
                if (sectionHeader == null)
                {
                    sectionHeader = new GUIStyle(EditorStyles.foldoutHeader);
                    sectionHeader.fontStyle = FontStyle.Bold;
                }
                return sectionHeader;
            }
        }
    }
}

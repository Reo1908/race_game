using UnityEngine;
#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
#endif

/// <summary>
/// Changes the label a field shows in the inspector without renaming the C# field,
/// so code and saved values keep working. Optional min/max draws the field as a slider
/// (use this instead of [Range], the two can't be combined on one field).
/// Works on fields inside [Serializable] classes too.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
public class InspectorLabelAttribute : PropertyAttribute
{
    public readonly string label;
    public readonly bool hasRange;
    public readonly float min;
    public readonly float max;

    public InspectorLabelAttribute(string label)
    {
        this.label = label;
    }

    public InspectorLabelAttribute(string label, float min, float max)
    {
        this.label = label;
        hasRange = true;
        this.min = min;
        this.max = max;
    }
}

#if UNITY_EDITOR
[CustomPropertyDrawer(typeof(InspectorLabelAttribute))]
public class InspectorLabelDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        InspectorLabelAttribute attr = (InspectorLabelAttribute)attribute;

        // Keep any [Tooltip] the field has
        string tooltip = label.tooltip;
        if (string.IsNullOrEmpty(tooltip) && fieldInfo != null)
        {
            TooltipAttribute tip = fieldInfo.GetCustomAttribute<TooltipAttribute>();
            if (tip != null) tooltip = tip.tooltip;
        }

        GUIContent content = new GUIContent(attr.label, tooltip);

        if (attr.hasRange && property.propertyType == SerializedPropertyType.Float)
            EditorGUI.Slider(position, property, attr.min, attr.max, content);
        else
            EditorGUI.PropertyField(position, property, content, true);
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUI.GetPropertyHeight(property, label, true);
    }
}
#endif

/// <summary>
/// Draws a float as a slider with a button underneath that calls a method on the
/// component (by name). Used for the "Bake Settings" button. Replaces [Range] on that field.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
public class InspectorSliderButtonAttribute : PropertyAttribute
{
    public readonly string label;
    public readonly float min;
    public readonly float max;
    public readonly string buttonLabel;
    public readonly string methodName;

    public InspectorSliderButtonAttribute(string label, float min, float max, string buttonLabel, string methodName)
    {
        this.label = label;
        this.min = min;
        this.max = max;
        this.buttonLabel = buttonLabel;
        this.methodName = methodName;
    }
}

#if UNITY_EDITOR
[CustomPropertyDrawer(typeof(InspectorSliderButtonAttribute))]
public class InspectorSliderButtonDrawer : PropertyDrawer
{
    private const float Gap = 2f;

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        InspectorSliderButtonAttribute attr = (InspectorSliderButtonAttribute)attribute;
        float line = EditorGUIUtility.singleLineHeight;

        string tooltip = label.tooltip;
        if (string.IsNullOrEmpty(tooltip) && fieldInfo != null)
        {
            TooltipAttribute tip = fieldInfo.GetCustomAttribute<TooltipAttribute>();
            if (tip != null) tooltip = tip.tooltip;
        }

        Rect sliderRect = new Rect(position.x, position.y, position.width, line);
        EditorGUI.Slider(sliderRect, property, attr.min, attr.max, new GUIContent(attr.label, tooltip));

        // Button sits under the slider, lined up with the slider's value area
        float indent = EditorGUIUtility.labelWidth;
        Rect buttonRect = new Rect(position.x + indent, position.y + line + Gap, position.width - indent, line);

        if (GUI.Button(buttonRect, attr.buttonLabel))
        {
            foreach (Object target in property.serializedObject.targetObjects)
            {
                Undo.RecordObject(target, attr.buttonLabel);
                MethodInfo method = target.GetType().GetMethod(
                    attr.methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (method != null) method.Invoke(target, null);
                else Debug.LogWarning($"InspectorSliderButton: method '{attr.methodName}' not found on {target.GetType().Name}");

                EditorUtility.SetDirty(target);
            }
            property.serializedObject.Update();
        }
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUIUtility.singleLineHeight * 2f + Gap;
    }
}
#endif
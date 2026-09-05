using UnityEngine;

namespace RevAudio
{
    /// <summary>
    /// Draws a Vector2 as a two-handle min/max range slider (x = min, y = max).
    /// Used for every "range" style control in the engine (grain size, grain rate,
    /// overlap, playhead range, ...) so they all look and behave the same.
    /// </summary>
    public class MinMaxRangeAttribute : PropertyAttribute
    {
        public readonly float Min;
        public readonly float Max;
        public readonly string Unit;
        public readonly bool Integer;

        public MinMaxRangeAttribute(float min, float max, string unit = "", bool integer = false)
        {
            Min = min;
            Max = max;
            Unit = unit;
            Integer = integer;
        }
    }
}

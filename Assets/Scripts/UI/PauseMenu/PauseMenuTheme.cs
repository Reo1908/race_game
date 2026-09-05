using UnityEngine;

/// <summary>
/// Every colour, size and timing the pause menu draws with. Kept as one
/// serializable block so the whole look can be retuned from the inspector
/// without touching the builder code.
/// </summary>
[System.Serializable]
public class PauseMenuTheme
{
    [Header("Palette")]
    [Tooltip("Main phosphor colour — headline, selection bar, highlights.")]
    public Color primary = new Color(0.435f, 0.949f, 1f, 1f);

    [Tooltip("Dimmed phosphor for rules, ticks and idle chrome.")]
    public Color primaryDim = new Color(0.165f, 0.482f, 0.573f, 1f);

    [Tooltip("Warm accent — index numbers, redline, the 'live' bits.")]
    public Color accent = new Color(1f, 0.690f, 0.227f, 1f);

    [Tooltip("Used by the quit entry when it is selected.")]
    public Color danger = new Color(1f, 0.302f, 0.369f, 1f);

    public Color textBright = new Color(0.902f, 0.976f, 1f, 1f);
    public Color textDim = new Color(0.486f, 0.576f, 0.659f, 1f);

    [Tooltip("Fill behind the panels, alpha included.")]
    public Color panelFill = new Color(0.024f, 0.055f, 0.086f, 0.82f);

    [Header("Backdrop")]
    public Color screenTint = new Color(0.031f, 0.059f, 0.098f, 1f);
    [Range(0f, 1f)] public float tintAmount = 0.78f;
    [Range(0f, 1f)] public float desaturate = 0.55f;
    [Range(0f, 2f)] public float brightness = 0.55f;
    [Range(0f, 1f)] public float scanlineAmount = 0.22f;
    [Range(0f, 1f)] public float grain = 0.07f;

    [Header("Type")]
    [Tooltip("Leave empty to use Unity's built-in font. A condensed or mono face suits this layout best.")]
    public Font font;
    public int headlineSize = 92;
    public int itemSize = 34;
    public int labelSize = 17;
    public int microSize = 13;

    [Header("Geometry")]
    [Tooltip("Horizontal offset (px) between the bottom and top edge of every slanted bar. Single knob for how hard the whole UI leans.")]
    public float slant = 14f;

    [Tooltip("Height of one menu row in reference pixels.")]
    public float rowHeight = 62f;

    [Header("Timing (unscaled seconds)")]
    public float openDuration = 0.45f;
    public float closeDuration = 0.22f;

    [Tooltip("Delay between one menu row arriving and the next.")]
    public float rowStagger = 0.045f;

    /// <summary>Assigned font, or Unity's built-in one when nothing is set.</summary>
    public Font ResolveFont()
    {
        if (font != null) return font;

        // Unity 2022.2+ renamed the built-in Arial resource; keep the old name as a fallback.
        Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (builtin == null) builtin = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return builtin;
    }

    public static Color Fade(Color c, float a)
    {
        c.a *= a;
        return c;
    }
}

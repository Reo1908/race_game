using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A slanted (parallelogram) bar drawn straight into the UI mesh — no sprite,
/// no texture, no import step. Optionally splits itself into evenly spaced
/// segments, which is what the rev bar and the tick strips are built from.
///
/// The slant is a fixed pixel offset rather than an angle, so every bar in the
/// menu leans by exactly the same amount no matter how tall it is.
/// </summary>
[AddComponentMenu("UI/Retro Pause/Slanted Graphic")]
public class SlantedGraphic : MaskableGraphic
{
    [Tooltip("Horizontal offset between the bottom and top edge, in local px. Negative leans the other way.")]
    [SerializeField] private float slant = 14f;

    [Tooltip("1 = one solid bar. Higher values split the width into that many lit segments.")]
    [SerializeField] private int segments = 1;

    [Tooltip("Gap between segments, in local px. Ignored when segments = 1.")]
    [SerializeField] private float segmentGap = 4f;

    [Tooltip("Colour at the left edge (multiplied by the graphic colour).")]
    [SerializeField] private Color leftColor = Color.white;

    [Tooltip("Colour at the right edge (multiplied by the graphic colour).")]
    [SerializeField] private Color rightColor = Color.white;

    [Tooltip("0..1 fill. A single bar shrinks its width; a segmented bar lights that fraction of its segments.")]
    [Range(0f, 1f)] [SerializeField] private float fill = 1f;

    [Tooltip("Colour for segments past the fill point. Alpha 0 hides them entirely.")]
    [SerializeField] private Color unlitColor = new Color(1f, 1f, 1f, 0.12f);

    [Tooltip("Normalised position where the warn colour takes over (1 = never). Drives the redline on the rev bar.")]
    [Range(0f, 1f)] [SerializeField] private float warnFrom = 1f;

    [SerializeField] private Color warnColor = new Color(1f, 0.302f, 0.369f, 1f);

    public float Slant
    {
        get { return slant; }
        set { if (!Mathf.Approximately(slant, value)) { slant = value; SetVerticesDirty(); } }
    }

    public float Fill
    {
        get { return fill; }
        set
        {
            value = Mathf.Clamp01(value);
            if (!Mathf.Approximately(fill, value)) { fill = value; SetVerticesDirty(); }
        }
    }

    public Color LeftColor
    {
        get { return leftColor; }
        set { leftColor = value; SetVerticesDirty(); }
    }

    public Color RightColor
    {
        get { return rightColor; }
        set { rightColor = value; SetVerticesDirty(); }
    }

    public Color UnlitColor
    {
        get { return unlitColor; }
        set { unlitColor = value; SetVerticesDirty(); }
    }

    public Color WarnColor
    {
        get { return warnColor; }
        set { warnColor = value; SetVerticesDirty(); }
    }

    public float WarnFrom
    {
        get { return warnFrom; }
        set { warnFrom = Mathf.Clamp01(value); SetVerticesDirty(); }
    }

    /// <summary>One-call setup used by the runtime builder.</summary>
    public void Configure(float barSlant, Color left, Color right, int segmentCount = 1, float gap = 4f)
    {
        slant = barSlant;
        leftColor = left;
        rightColor = right;
        segments = Mathf.Max(1, segmentCount);
        segmentGap = gap;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect r = GetPixelAdjustedRect();
        if (r.width <= 0f || r.height <= 0f) return;

        int count = Mathf.Max(1, segments);

        if (count == 1)
        {
            float width = r.width * Mathf.Clamp01(fill);
            if (width <= 0f) return;
            AddSlantedQuad(vh, r.xMin, r.xMin + width, r.yMin, r.yMax, r, true, -1f);
            return;
        }

        float totalGap = segmentGap * (count - 1);
        float segWidth = (r.width - totalGap) / count;
        if (segWidth <= 0f) return;

        for (int i = 0; i < count; i++)
        {
            float x0 = r.xMin + i * (segWidth + segmentGap);
            float t = (i + 0.5f) / count;
            bool lit = t <= fill;
            if (!lit && unlitColor.a <= 0f) continue;
            AddSlantedQuad(vh, x0, x0 + segWidth, r.yMin, r.yMax, r, lit, t);
        }
    }

    private void AddSlantedQuad(VertexHelper vh, float x0, float x1, float y0, float y1, Rect full, bool lit, float segT)
    {
        // Applied symmetrically so the bar stays visually centred on its
        // RectTransform instead of drifting to one side as it gets taller.
        float half = slant * 0.5f;

        Color32 left = ResolveColor(x0, full, lit, segT);
        Color32 right = ResolveColor(x1, full, lit, segT);

        UIVertex v = UIVertex.simpleVert;

        v.color = left;
        v.position = new Vector3(x0 - half, y0);
        v.uv0 = new Vector2(0f, 0f);
        vh.AddVert(v);

        v.position = new Vector3(x0 + half, y1);
        v.uv0 = new Vector2(0f, 1f);
        vh.AddVert(v);

        v.color = right;
        v.position = new Vector3(x1 + half, y1);
        v.uv0 = new Vector2(1f, 1f);
        vh.AddVert(v);

        v.position = new Vector3(x1 - half, y0);
        v.uv0 = new Vector2(1f, 0f);
        vh.AddVert(v);

        int b = vh.currentVertCount - 4;
        vh.AddTriangle(b, b + 1, b + 2);
        vh.AddTriangle(b + 2, b + 3, b);
    }

    private Color ResolveColor(float x, Rect full, bool lit, float segT)
    {
        if (!lit) return unlitColor * color;

        float t = full.width > 0f ? Mathf.Clamp01((x - full.xMin) / full.width) : 0f;
        Color gradient = Color.Lerp(leftColor, rightColor, t);

        if (segT >= 0f && warnFrom < 1f && segT >= warnFrom)
        {
            float over = Mathf.Clamp01((segT - warnFrom) / Mathf.Max(0.001f, 1f - warnFrom));
            gradient = Color.Lerp(gradient, warnColor, Mathf.Clamp01(0.4f + over * 0.6f));
        }

        return gradient * color;
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        segments = Mathf.Max(1, segments);
        SetVerticesDirty();
    }
#endif
}

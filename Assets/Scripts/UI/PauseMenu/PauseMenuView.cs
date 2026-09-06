using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One selectable row in the menu.</summary>
public struct PauseMenuEntryInfo
{
    public string label;
    public string caption;
    public bool danger;

    public PauseMenuEntryInfo(string label, string caption, bool danger = false)
    {
        this.label = label;
        this.caption = caption;
        this.danger = danger;
    }
}

/// <summary>One line on the CONTROLS page.</summary>
[System.Serializable]
public struct PauseMenuBinding
{
    [Tooltip("What the input does, e.g. THROTTLE.")]
    public string action;

    [Tooltip("Keyboard side of the binding, e.g. 'W / UP'.")]
    public string keyboard;

    [Tooltip("Gamepad side of the binding, e.g. 'RT'. Leave empty for keyboard-only actions.")]
    public string gamepad;

    public PauseMenuBinding(string action, string keyboard, string gamepad)
    {
        this.action = action;
        this.keyboard = keyboard;
        this.gamepad = gamepad;
    }
}

/// <summary>
/// Builds and animates the whole pause menu at runtime — canvas, backdrop,
/// letterbox, menu rows, telemetry card, controls page. Nothing here needs a
/// prefab or a scene edit; <see cref="PauseMenu"/> just news one of these up.
///
/// All animation runs on unscaled time, since the game itself is frozen at
/// Time.timeScale = 0 while any of this is on screen.
/// </summary>
public class PauseMenuView
{
    public enum Page { Root, Controls }

    private const float RefWidth = 1920f;
    private const float RefHeight = 1080f;
    private const float MarginX = 150f;
    private const float BarHeight = 84f;
    private const float RowWidth = 660f;
    private const float RowGap = 6f;
    private const float FirstRowY = -352f;
    private const float PanelWidth = 560f;

    private static readonly Vector2 TopLeft = new Vector2(0f, 1f);
    private static readonly Vector2 TopRight = new Vector2(1f, 1f);
    private static readonly Vector2 Centre = new Vector2(0.5f, 0.5f);

    private class Row
    {
        public RectTransform rect;
        public CanvasGroup group;
        public SlantedGraphic highlight;
        public SlantedGraphic edge;
        public Text index;
        public Text label;
        public Text caption;
        public Color hotColor;
        public bool danger;
        public float selection;   // smoothed 0..1
        public bool selected;
    }

    private readonly PauseMenuTheme theme;
    private readonly Font font;

    private GameObject rootObject;
    private RectTransform canvasRect;

    private RawImage backdrop;
    private Material backdropMaterial;

    private CanvasGroup topBarGroup;
    private CanvasGroup bottomBarGroup;
    private RectTransform topBar;
    private RectTransform bottomBar;
    private Image pauseGlyphA;
    private Image pauseGlyphB;
    private Text clockText;

    private CanvasGroup headerGroup;
    private RectTransform headerBlock;

    private CanvasGroup rowsGroup;
    private RectTransform rowsBlock;
    private readonly List<Row> rows = new List<Row>();

    private CanvasGroup telemetryGroup;
    private RectTransform telemetryPanel;
    private Text speedValue;
    private Text gearValue;
    private Text rpmValue;
    private SlantedGraphic revBar;
    private Text throttleValue;
    private Text ratioValue;
    private Text ignitionValue;

    private CanvasGroup controlsGroup;
    private RectTransform controlsPanel;

    private Text ghostWordmark;
    private readonly List<RectTransform> speedLines = new List<RectTransform>();
    private readonly List<float> speedLineSeeds = new List<float>();

    private Page page = Page.Root;
    private float pageDim = 1f;
    private float openProgress;
    private float revTarget;
    private float revShown;

    public bool IsBuilt { get { return rootObject != null; } }
    public Page CurrentPage { get { return page; } }
    public int RowCount { get { return rows.Count; } }

    public PauseMenuView(PauseMenuTheme theme)
    {
        this.theme = theme;
        font = theme.ResolveFont();
    }

    // ------------------------------------------------------------------ build

    public void Build(Transform owner, IList<PauseMenuEntryInfo> entries, IList<PauseMenuBinding> bindings,
                      string trackName, int sortingOrder)
    {
        rootObject = new GameObject("PauseMenu_Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        rootObject.layer = 5;
        rootObject.transform.SetParent(owner, false);

        Canvas canvas = rootObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvasRect = (RectTransform)rootObject.transform;

        CanvasScaler scaler = rootObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(RefWidth, RefHeight);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        BuildBackdrop();
        BuildGhostWordmark();
        BuildSpeedLines();
        BuildLetterbox(trackName);
        BuildHeader(trackName);
        BuildRows(entries);
        BuildTelemetry();
        BuildControls(bindings);

        SetOpenProgress(0f);
        SetPage(Page.Root, false);
    }

    private void BuildBackdrop()
    {
        RectTransform rt = MakeRect("Backdrop", canvasRect, Vector2.zero, Vector2.zero, Centre, Centre);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        backdrop = rt.gameObject.AddComponent<RawImage>();
        backdrop.raycastTarget = false;
        backdrop.color = Color.white;

        Shader shader = Shader.Find("UI/RetroPause/Backdrop");
        if (shader != null)
        {
            backdropMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            backdropMaterial.SetColor("_ScreenTint", theme.screenTint);
            backdropMaterial.SetColor("_Phosphor", theme.primary);
            backdropMaterial.SetFloat("_TintAmount", theme.tintAmount);
            backdropMaterial.SetFloat("_Desaturate", theme.desaturate);
            backdropMaterial.SetFloat("_Brightness", theme.brightness);
            backdropMaterial.SetFloat("_ScanAmount", theme.scanlineAmount);
            backdropMaterial.SetFloat("_GrainAmount", theme.grain);
            backdrop.material = backdropMaterial;
        }
        else
        {
            // Shader missing (deleted, or stripped from a build) — fall back to a
            // plain dark wash so the menu is still perfectly usable.
            Debug.LogWarning("[PauseMenu] Shader 'UI/RetroPause/Backdrop' not found — using a flat backdrop.");
            backdrop.color = new Color(theme.screenTint.r, theme.screenTint.g, theme.screenTint.b, 0.92f);
        }
    }

    private void BuildGhostWordmark()
    {
        ghostWordmark = MakeText("GhostWordmark", canvasRect, Spaced("PAUSE"), 280,
            PauseMenuTheme.Fade(theme.primary, 0.05f), TextAnchor.MiddleCenter,
            new Vector2(90f, -20f), new Vector2(1800f, 420f), Centre, Centre);
        ghostWordmark.fontStyle = FontStyle.BoldAndItalic;
    }

    private void BuildSpeedLines()
    {
        RectTransform holder = MakeRect("SpeedLines", canvasRect, Vector2.zero, Vector2.zero, Centre, Centre);
        holder.anchorMin = Vector2.zero;
        holder.anchorMax = Vector2.one;
        holder.offsetMin = Vector2.zero;
        holder.offsetMax = Vector2.zero;

        // Thin slanted streaks that keep drifting while everything else is
        // frozen — the only thing on screen still suggesting motion.
        float[] ys = { -228f, -470f, -612f, -742f, -880f, -960f };
        float[] widths = { 320f, 180f, 460f, 240f, 150f, 380f };
        float[] alphas = { 0.20f, 0.10f, 0.14f, 0.08f, 0.16f, 0.07f };

        for (int i = 0; i < ys.Length; i++)
        {
            SlantedGraphic bar = MakeBar("Streak" + i, holder,
                new Vector2(-40f, ys[i]), new Vector2(widths[i], i % 2 == 0 ? 3f : 2f), TopLeft, TopLeft);
            bar.Configure(theme.slant * 0.5f,
                PauseMenuTheme.Fade(theme.primary, 0f),
                PauseMenuTheme.Fade(theme.primary, alphas[i]));
            speedLines.Add((RectTransform)bar.transform);
            speedLineSeeds.Add(Random.Range(0f, 10f));
        }
    }

    private void BuildLetterbox(string trackName)
    {
        // ---- top ----
        topBar = MakeRect("TopBar", canvasRect, Vector2.zero, new Vector2(0f, BarHeight), TopLeft, TopLeft);
        topBar.anchorMin = new Vector2(0f, 1f);
        topBar.anchorMax = new Vector2(1f, 1f);
        topBar.sizeDelta = new Vector2(0f, BarHeight);
        topBarGroup = topBar.gameObject.AddComponent<CanvasGroup>();

        MakeStretchedImage("Fill", topBar, new Color(theme.screenTint.r, theme.screenTint.g, theme.screenTint.b, 0.94f));
        MakeImage("Edge", topBar, PauseMenuTheme.Fade(theme.primary, 0.55f),
            new Vector2(0f, -BarHeight + 2f), new Vector2(0f, 2f), TopLeft, TopLeft, true);

        pauseGlyphA = MakeImage("GlyphA", topBar, theme.accent, new Vector2(MarginX, -32f), new Vector2(7f, 24f), TopLeft, TopLeft);
        pauseGlyphB = MakeImage("GlyphB", topBar, theme.accent, new Vector2(MarginX + 13f, -32f), new Vector2(7f, 24f), TopLeft, TopLeft);

        MakeText("Status", topBar, Spaced("SYSTEM STANDBY"), theme.labelSize, theme.primary,
            TextAnchor.MiddleLeft, new Vector2(MarginX + 38f, -30f), new Vector2(500f, 28f), TopLeft, TopLeft);

        clockText = MakeText("Clock", topBar, "", theme.labelSize, theme.textDim,
            TextAnchor.MiddleRight, new Vector2(-MarginX, -30f), new Vector2(560f, 28f), TopRight, TopRight);

        // ---- bottom ----
        bottomBar = MakeRect("BottomBar", canvasRect, Vector2.zero, new Vector2(0f, BarHeight), new Vector2(0f, 0f), new Vector2(0f, 0f));
        bottomBar.anchorMin = new Vector2(0f, 0f);
        bottomBar.anchorMax = new Vector2(1f, 0f);
        bottomBar.sizeDelta = new Vector2(0f, BarHeight);
        bottomBarGroup = bottomBar.gameObject.AddComponent<CanvasGroup>();

        MakeStretchedImage("Fill", bottomBar, new Color(theme.screenTint.r, theme.screenTint.g, theme.screenTint.b, 0.94f));
        MakeImage("Edge", bottomBar, PauseMenuTheme.Fade(theme.primary, 0.55f),
            new Vector2(0f, BarHeight - 2f), new Vector2(0f, 2f), new Vector2(0f, 0f), new Vector2(0f, 0f), true);

        MakeText("Hints", bottomBar, BuildHintLine(), theme.microSize, theme.textDim,
            TextAnchor.MiddleLeft, new Vector2(MarginX, 30f), new Vector2(1100f, 26f), new Vector2(0f, 0f), new Vector2(0f, 0f));

        MakeText("Track", bottomBar, Spaced(trackName.ToUpperInvariant()), theme.microSize,
            PauseMenuTheme.Fade(theme.primary, 0.75f), TextAnchor.MiddleRight,
            new Vector2(-MarginX, 30f), new Vector2(600f, 26f), new Vector2(1f, 0f), new Vector2(1f, 0f));
    }

    private static string BuildHintLine()
    {
        return "<b>W / S</b>  NAVIGATE      <b>ENTER</b>  SELECT      <b>ESC</b>  RESUME      <b>GAMEPAD</b>  D-PAD / A / B";
    }

    private void BuildHeader(string trackName)
    {
        headerBlock = MakeRect("Header", canvasRect, Vector2.zero, Vector2.zero, TopLeft, TopLeft);
        headerBlock.anchorMin = Vector2.zero;
        headerBlock.anchorMax = Vector2.one;
        headerBlock.offsetMin = Vector2.zero;
        headerBlock.offsetMax = Vector2.zero;
        headerGroup = headerBlock.gameObject.AddComponent<CanvasGroup>();

        SlantedGraphic tab = MakeBar("Tab", headerBlock, new Vector2(MarginX, -150f), new Vector2(74f, 9f), TopLeft, TopLeft);
        tab.Configure(theme.slant * 0.6f, theme.accent, PauseMenuTheme.Fade(theme.accent, 0.15f));

        Text headline = MakeText("Headline", headerBlock, Spaced("PAUSED"), theme.headlineSize, theme.textBright,
            TextAnchor.UpperLeft, new Vector2(MarginX - 4f, -172f), new Vector2(1000f, 120f), TopLeft, TopLeft);
        headline.fontStyle = FontStyle.BoldAndItalic;

        Outline glow = headline.gameObject.AddComponent<Outline>();
        glow.effectColor = PauseMenuTheme.Fade(theme.primary, 0.45f);
        glow.effectDistance = new Vector2(3f, -3f);
        glow.useGraphicAlpha = true;

        MakeText("Subline", headerBlock, Spaced("SESSION HALTED  //  " + trackName.ToUpperInvariant()),
            theme.labelSize, theme.textDim, TextAnchor.UpperLeft,
            new Vector2(MarginX, -286f), new Vector2(900f, 26f), TopLeft, TopLeft);

        SlantedGraphic rule = MakeBar("Rule", headerBlock, new Vector2(MarginX, -324f), new Vector2(RowWidth, 2f), TopLeft, TopLeft);
        rule.Configure(theme.slant * 0.3f, PauseMenuTheme.Fade(theme.primary, 0.7f), PauseMenuTheme.Fade(theme.primary, 0f));
    }

    private void BuildRows(IList<PauseMenuEntryInfo> entries)
    {
        rowsBlock = MakeRect("Rows", canvasRect, Vector2.zero, Vector2.zero, TopLeft, TopLeft);
        rowsBlock.anchorMin = Vector2.zero;
        rowsBlock.anchorMax = Vector2.one;
        rowsBlock.offsetMin = Vector2.zero;
        rowsBlock.offsetMax = Vector2.zero;
        rowsGroup = rowsBlock.gameObject.AddComponent<CanvasGroup>();

        float step = theme.rowHeight + RowGap;

        for (int i = 0; i < entries.Count; i++)
        {
            PauseMenuEntryInfo info = entries[i];
            Row row = new Row();
            row.danger = info.danger;
            row.hotColor = info.danger ? theme.danger : theme.primary;

            row.rect = MakeRect("Row_" + info.label, rowsBlock,
                new Vector2(MarginX, FirstRowY - i * step), new Vector2(RowWidth, theme.rowHeight), TopLeft, TopLeft);
            row.group = row.rect.gameObject.AddComponent<CanvasGroup>();

            row.highlight = MakeBar("Highlight", row.rect, Vector2.zero, Vector2.zero, TopLeft, TopLeft);
            Stretch((RectTransform)row.highlight.transform);
            row.highlight.Configure(theme.slant, PauseMenuTheme.Fade(row.hotColor, 0.30f), PauseMenuTheme.Fade(row.hotColor, 0f));
            row.highlight.color = new Color(1f, 1f, 1f, 0f);

            row.edge = MakeBar("Edge", row.rect, new Vector2(0f, 0f), new Vector2(7f, theme.rowHeight), TopLeft, TopLeft);
            row.edge.Configure(theme.slant, row.hotColor, row.hotColor);
            row.edge.color = new Color(1f, 1f, 1f, 0.25f);

            row.index = MakeText("Index", row.rect, (i + 1).ToString("00"), theme.microSize,
                PauseMenuTheme.Fade(theme.accent, 0.6f), TextAnchor.MiddleLeft,
                new Vector2(28f, -theme.rowHeight * 0.5f), new Vector2(60f, 24f), TopLeft, new Vector2(0f, 0.5f));

            row.label = MakeText("Label", row.rect, Spaced(info.label), theme.itemSize, theme.textDim,
                TextAnchor.MiddleLeft, new Vector2(76f, -theme.rowHeight * 0.5f), new Vector2(420f, 44f),
                TopLeft, new Vector2(0f, 0.5f));
            row.label.fontStyle = FontStyle.Italic;

            row.caption = MakeText("Caption", row.rect, Spaced(info.caption), theme.microSize,
                PauseMenuTheme.Fade(theme.textDim, 0.7f), TextAnchor.MiddleRight,
                new Vector2(-18f, -theme.rowHeight * 0.5f), new Vector2(300f, 24f), TopRight, new Vector2(1f, 0.5f));

            rows.Add(row);
        }
    }

    private void BuildTelemetry()
    {
        telemetryPanel = MakeRect("Telemetry", canvasRect, new Vector2(-MarginX, -176f),
            new Vector2(PanelWidth, 470f), TopRight, TopRight);
        telemetryGroup = telemetryPanel.gameObject.AddComponent<CanvasGroup>();

        BuildPanelChrome(telemetryPanel, "TELEMETRY // FROZEN", theme.primary);

        MakeText("SpeedLabel", telemetryPanel, Spaced("ROAD SPEED"), theme.microSize, theme.textDim,
            TextAnchor.UpperLeft, new Vector2(26f, -52f), new Vector2(300f, 20f), TopLeft, TopLeft);

        speedValue = MakeText("SpeedValue", telemetryPanel, "0", 76, theme.textBright,
            TextAnchor.UpperLeft, new Vector2(22f, -74f), new Vector2(300f, 92f), TopLeft, TopLeft);
        speedValue.fontStyle = FontStyle.BoldAndItalic;

        MakeText("SpeedUnit", telemetryPanel, Spaced("KM/H"), theme.microSize,
            PauseMenuTheme.Fade(theme.primary, 0.8f), TextAnchor.UpperLeft,
            new Vector2(26f, -162f), new Vector2(200f, 20f), TopLeft, TopLeft);

        MakeText("GearLabel", telemetryPanel, Spaced("GEAR"), theme.microSize, theme.textDim,
            TextAnchor.UpperRight, new Vector2(-26f, -52f), new Vector2(200f, 20f), TopRight, TopRight);

        gearValue = MakeText("GearValue", telemetryPanel, "-", 64, theme.accent,
            TextAnchor.UpperRight, new Vector2(-26f, -74f), new Vector2(200f, 80f), TopRight, TopRight);
        gearValue.fontStyle = FontStyle.BoldAndItalic;

        MakeText("RpmLabel", telemetryPanel, Spaced("ENGINE RPM"), theme.microSize, theme.textDim,
            TextAnchor.UpperLeft, new Vector2(26f, -196f), new Vector2(300f, 20f), TopLeft, TopLeft);

        rpmValue = MakeText("RpmValue", telemetryPanel, "----", theme.labelSize, theme.textBright,
            TextAnchor.UpperRight, new Vector2(-26f, -196f), new Vector2(300f, 22f), TopRight, TopRight);

        revBar = MakeBar("RevBar", telemetryPanel, new Vector2(26f, -224f),
            new Vector2(PanelWidth - 52f, 24f), TopLeft, TopLeft);
        revBar.Configure(theme.slant * 0.55f, theme.primaryDim, theme.primary, 26, 4f);
        revBar.WarnFrom = 0.78f;
        revBar.WarnColor = theme.danger;
        revBar.UnlitColor = PauseMenuTheme.Fade(theme.primary, 0.10f);
        revBar.Fill = 0f;

        MakeImage("Divider", telemetryPanel, PauseMenuTheme.Fade(theme.primary, 0.22f),
            new Vector2(26f, -272f), new Vector2(PanelWidth - 52f, 1f), TopLeft, TopLeft);

        throttleValue = MakeStatRow(telemetryPanel, -292f, "THROTTLE MAP");
        ratioValue = MakeStatRow(telemetryPanel, -330f, "GEAR RATIO");
        ignitionValue = MakeStatRow(telemetryPanel, -368f, "IGNITION");

        MakeText("Footer", telemetryPanel, Spaced("SIM HALTED  //  TIMESCALE 0.00"), theme.microSize,
            PauseMenuTheme.Fade(theme.accent, 0.75f), TextAnchor.LowerLeft,
            new Vector2(26f, 18f), new Vector2(420f, 20f), new Vector2(0f, 0f), new Vector2(0f, 0f));
    }

    private Text MakeStatRow(RectTransform panel, float y, string label)
    {
        MakeText(label + "_L", panel, Spaced(label), theme.microSize, theme.textDim,
            TextAnchor.MiddleLeft, new Vector2(26f, y), new Vector2(300f, 22f), TopLeft, TopLeft);

        return MakeText(label + "_V", panel, "--", theme.labelSize, theme.textBright,
            TextAnchor.MiddleRight, new Vector2(-26f, y), new Vector2(300f, 22f), TopRight, TopRight);
    }

    private void BuildControls(IList<PauseMenuBinding> bindings)
    {
        float height = Mathf.Min(660f, 150f + bindings.Count * 44f);
        controlsPanel = MakeRect("Controls", canvasRect, new Vector2(-MarginX, -176f),
            new Vector2(PanelWidth, height), TopRight, TopRight);
        controlsGroup = controlsPanel.gameObject.AddComponent<CanvasGroup>();

        BuildPanelChrome(controlsPanel, "INPUT MAP", theme.accent);

        MakeText("Hint", controlsPanel, Spaced("KEYBOARD"), theme.microSize, theme.textDim,
            TextAnchor.UpperLeft, new Vector2(26f, -52f), new Vector2(200f, 20f), TopLeft, TopLeft);
        MakeText("Hint2", controlsPanel, Spaced("GAMEPAD"), theme.microSize, theme.textDim,
            TextAnchor.UpperRight, new Vector2(-26f, -52f), new Vector2(200f, 20f), TopRight, TopRight);

        float y = -84f;
        for (int i = 0; i < bindings.Count; i++)
        {
            PauseMenuBinding b = bindings[i];

            if (i % 2 == 0)
            {
                MakeImage("Stripe" + i, controlsPanel, PauseMenuTheme.Fade(theme.primary, 0.045f),
                    new Vector2(18f, y - 4f), new Vector2(PanelWidth - 36f, 36f), TopLeft, TopLeft);
            }

            MakeText("Action" + i, controlsPanel, Spaced(b.action), theme.microSize,
                PauseMenuTheme.Fade(theme.primary, 0.9f), TextAnchor.MiddleLeft,
                new Vector2(26f, y - 22f), new Vector2(230f, 24f), TopLeft, new Vector2(0f, 0.5f));

            MakeText("Keys" + i, controlsPanel, b.keyboard, theme.labelSize, theme.textBright,
                TextAnchor.MiddleRight, new Vector2(-150f, y - 22f), new Vector2(220f, 24f), TopRight, new Vector2(1f, 0.5f));

            MakeText("Pad" + i, controlsPanel, string.IsNullOrEmpty(b.gamepad) ? "—" : b.gamepad,
                theme.labelSize, string.IsNullOrEmpty(b.gamepad) ? PauseMenuTheme.Fade(theme.textDim, 0.5f) : theme.accent,
                TextAnchor.MiddleRight, new Vector2(-26f, y - 22f), new Vector2(110f, 24f), TopRight, new Vector2(1f, 0.5f));

            y -= 44f;
        }

        MakeText("Back", controlsPanel, Spaced("ESC / B   BACK"), theme.microSize,
            PauseMenuTheme.Fade(theme.accent, 0.8f), TextAnchor.LowerRight,
            new Vector2(-26f, 18f), new Vector2(400f, 20f), new Vector2(1f, 0f), new Vector2(1f, 0f));
    }

    private void BuildPanelChrome(RectTransform panel, string title, Color titleColor)
    {
        MakeStretchedImage("Fill", panel, theme.panelFill);

        SlantedGraphic strip = MakeBar("TitleStrip", panel, Vector2.zero, new Vector2(0f, 30f), TopLeft, TopLeft);
        RectTransform stripRect = (RectTransform)strip.transform;
        stripRect.anchorMin = new Vector2(0f, 1f);
        stripRect.anchorMax = new Vector2(1f, 1f);
        stripRect.sizeDelta = new Vector2(0f, 30f);
        strip.Configure(theme.slant * 0.5f, titleColor, PauseMenuTheme.Fade(titleColor, 0.25f));

        MakeText("Title", panel, Spaced(title), theme.microSize, theme.screenTint,
            TextAnchor.MiddleLeft, new Vector2(26f, -15f), new Vector2(420f, 22f), TopLeft, new Vector2(0f, 0.5f));

        // L-brackets, top-left and bottom-right.
        Color bracket = PauseMenuTheme.Fade(theme.primary, 0.7f);
        MakeImage("BracketTL_H", panel, bracket, new Vector2(-6f, 6f), new Vector2(28f, 2f), TopLeft, TopLeft);
        MakeImage("BracketTL_V", panel, bracket, new Vector2(-6f, 6f), new Vector2(2f, 28f), TopLeft, TopLeft);
        MakeImage("BracketBR_H", panel, bracket, new Vector2(6f, -6f), new Vector2(28f, 2f), new Vector2(1f, 0f), new Vector2(1f, 0f));
        MakeImage("BracketBR_V", panel, bracket, new Vector2(6f, -6f), new Vector2(2f, 28f), new Vector2(1f, 0f), new Vector2(1f, 0f));
    }

    // -------------------------------------------------------------- animation

    /// <summary>0 = fully hidden, 1 = fully open. Drives every entrance offset.</summary>
    public void SetOpenProgress(float t)
    {
        openProgress = Mathf.Clamp01(t);
        if (rootObject == null) return;

        float eased = EaseOutCubic(openProgress);

        if (backdropMaterial != null) backdropMaterial.SetFloat("_Reveal", eased);
        else if (backdrop != null) backdrop.color = new Color(backdrop.color.r, backdrop.color.g, backdrop.color.b, 0.92f * eased);

        // Letterbox bars slide in from off-screen.
        topBarGroup.alpha = eased;
        topBar.anchoredPosition = new Vector2(0f, (1f - eased) * BarHeight);
        bottomBarGroup.alpha = eased;
        bottomBar.anchoredPosition = new Vector2(0f, -(1f - eased) * BarHeight);

        float headerT = Mathf.Clamp01(Mathf.InverseLerp(0.08f, 0.75f, openProgress));
        headerGroup.alpha = EaseOutCubic(headerT);
        headerBlock.anchoredPosition = new Vector2((1f - EaseOutBack(headerT)) * -80f, 0f);

        rowsGroup.alpha = openProgress > 0.001f ? 1f : 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            float from = 0.14f + i * theme.rowStagger / Mathf.Max(0.01f, theme.openDuration);
            float rowT = Mathf.Clamp01(Mathf.InverseLerp(from, from + 0.5f, openProgress));
            rows[i].group.alpha = rowT * pageDim;
            rows[i].rect.anchoredPosition = new Vector2(
                MarginX + (1f - EaseOutBack(rowT)) * -70f,
                rows[i].rect.anchoredPosition.y);
        }

        float panelT = Mathf.Clamp01(Mathf.InverseLerp(0.25f, 1f, openProgress));
        float panelEase = EaseOutCubic(panelT);
        float panelOffset = (1f - panelEase) * 90f;

        telemetryGroup.alpha = page == Page.Root ? panelEase : 0f;
        telemetryPanel.anchoredPosition = new Vector2(-MarginX + panelOffset, -176f);

        controlsGroup.alpha = page == Page.Controls ? panelEase : 0f;
        controlsPanel.anchoredPosition = new Vector2(-MarginX + panelOffset, -176f);

        ghostWordmark.color = PauseMenuTheme.Fade(theme.primary, 0.05f * eased);
    }

    /// <summary>Per-frame idle motion. dt is unscaled.</summary>
    public void Tick(float dt, float unscaledTime, float sessionSeconds)
    {
        if (rootObject == null) return;

        if (backdropMaterial != null) backdropMaterial.SetFloat("_UnscaledTime", unscaledTime);

        // Pause glyph breathes.
        float blink = 0.55f + 0.45f * Mathf.Sin(unscaledTime * 3.4f);
        Color glyph = PauseMenuTheme.Fade(theme.accent, Mathf.Lerp(0.45f, 1f, blink) * openProgress);
        pauseGlyphA.color = glyph;
        pauseGlyphB.color = glyph;

        int minutes = Mathf.FloorToInt(sessionSeconds / 60f);
        int seconds = Mathf.FloorToInt(sessionSeconds % 60f);
        int hundredths = Mathf.FloorToInt((sessionSeconds * 100f) % 100f);
        clockText.text = string.Format("SESSION  {0:00}:{1:00}.{2:00}", minutes, seconds, hundredths);

        for (int i = 0; i < speedLines.Count; i++)
        {
            float seed = speedLineSeeds[i];
            float drift = Mathf.Sin(unscaledTime * (0.35f + i * 0.09f) + seed) * 42f;
            RectTransform line = speedLines[i];
            line.anchoredPosition = new Vector2(-40f + drift, line.anchoredPosition.y);
        }

        // Selection states ease toward their target.
        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            float target = row.selected ? 1f : 0f;
            row.selection = Mathf.MoveTowards(row.selection, target, dt * 7f);

            float pulse = row.selected ? 0.85f + 0.15f * Mathf.Sin(unscaledTime * 6f) : 1f;
            float s = row.selection;

            row.highlight.color = new Color(1f, 1f, 1f, s * pulse);
            row.edge.color = new Color(1f, 1f, 1f, Mathf.Lerp(0.22f, 1f, s) * pulse);
            row.label.color = Color.Lerp(theme.textDim, row.danger ? theme.danger : theme.textBright, s);
            row.index.color = PauseMenuTheme.Fade(theme.accent, Mathf.Lerp(0.45f, 1f, s));
            row.caption.color = PauseMenuTheme.Fade(theme.textDim, Mathf.Lerp(0.5f, 1f, s));

            RectTransform label = (RectTransform)row.label.transform;
            label.anchoredPosition = new Vector2(Mathf.Lerp(76f, 96f, s), label.anchoredPosition.y);
        }

        // Rev bar sweeps up to the frozen value instead of snapping.
        revShown = Mathf.MoveTowards(revShown, revTarget, dt * 1.6f);
        revBar.Fill = revShown;
    }

    public void SetSelection(int index)
    {
        for (int i = 0; i < rows.Count; i++) rows[i].selected = (i == index);
    }

    public void SetPage(Page value, bool animate)
    {
        page = value;

        // Rows stay readable but step back while a sub page is up.
        pageDim = page == Page.Root ? 1f : 0.35f;
        rowsGroup.alpha = 1f;

        // Re-applying the current progress refreshes every alpha and offset in
        // one place, so page state and entrance animation stay consistent.
        SetOpenProgress(openProgress);
    }

    // ------------------------------------------------------------------ data

    public void SetFrozenFrame(Texture texture, bool flipY)
    {
        if (backdrop == null) return;
        backdrop.texture = texture;
        if (backdropMaterial != null) backdropMaterial.SetFloat("_FlipY", flipY ? 1f : 0f);
    }

    public void SetTelemetry(bool available, float speedKmh, float rpm, float redline,
                             float throttle, float gearRatio, bool ignitionOn, string gearText)
    {
        if (rootObject == null) return;

        if (!available)
        {
            speedValue.text = "--";
            gearValue.text = "-";
            rpmValue.text = "NO SIGNAL";
            throttleValue.text = "--";
            ratioValue.text = "--";
            ignitionValue.text = "--";
            revTarget = 0f;
            return;
        }

        speedValue.text = Mathf.RoundToInt(speedKmh).ToString();
        gearValue.text = gearText;
        rpmValue.text = string.Format("{0:0} / {1:0}", rpm, redline);
        throttleValue.text = Mathf.RoundToInt(Mathf.Clamp01(throttle) * 100f) + "%";
        ratioValue.text = gearRatio.ToString("0.00");
        ignitionValue.text = ignitionOn ? "ON" : "CUT";
        ignitionValue.color = ignitionOn ? theme.textBright : theme.danger;
        revTarget = redline > 0.01f ? Mathf.Clamp01(rpm / redline) : 0f;
    }

    /// <summary>Row under the given screen point, or -1. Used for mouse hover/click.</summary>
    public int HitTest(Vector2 screenPoint)
    {
        if (rootObject == null || page != Page.Root) return -1;

        for (int i = 0; i < rows.Count; i++)
        {
            if (RectTransformUtility.RectangleContainsScreenPoint(rows[i].rect, screenPoint, null))
                return i;
        }
        return -1;
    }

    public void SetVisible(bool visible)
    {
        if (rootObject != null && rootObject.activeSelf != visible) rootObject.SetActive(visible);
        if (visible) revShown = 0f;
    }

    public void Destroy()
    {
        if (rootObject != null) Object.Destroy(rootObject);
        if (backdropMaterial != null) Object.Destroy(backdropMaterial);
        rootObject = null;
        backdropMaterial = null;
        rows.Clear();
        speedLines.Clear();
        speedLineSeeds.Clear();
    }

    // --------------------------------------------------------------- helpers

    /// <summary>Legacy Text has no letter-spacing, so we fake it. Reads properly retro anyway.</summary>
    private static string Spaced(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;

        System.Text.StringBuilder sb = new System.Text.StringBuilder(source.Length * 2);
        for (int i = 0; i < source.Length; i++)
        {
            sb.Append(source[i]);
            if (i < source.Length - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    private static float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private static float EaseOutBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float inv = t - 1f;
        return 1f + c3 * inv * inv * inv + c1 * inv * inv;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static RectTransform MakeRect(string name, Transform parent, Vector2 pos, Vector2 size,
                                          Vector2 anchor, Vector2 pivot)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    private static Image MakeImage(string name, Transform parent, Color color, Vector2 pos, Vector2 size,
                                   Vector2 anchor, Vector2 pivot, bool stretchHorizontally = false)
    {
        RectTransform rt = MakeRect(name, parent, pos, size, anchor, pivot);
        if (stretchHorizontally)
        {
            rt.anchorMin = new Vector2(0f, anchor.y);
            rt.anchorMax = new Vector2(1f, anchor.y);
            rt.sizeDelta = new Vector2(0f, size.y);
        }

        Image image = rt.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static Image MakeStretchedImage(string name, Transform parent, Color color)
    {
        RectTransform rt = MakeRect(name, parent, Vector2.zero, Vector2.zero, Centre, Centre);
        Stretch(rt);
        Image image = rt.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static SlantedGraphic MakeBar(string name, Transform parent, Vector2 pos, Vector2 size,
                                          Vector2 anchor, Vector2 pivot)
    {
        RectTransform rt = MakeRect(name, parent, pos, size, anchor, pivot);
        SlantedGraphic bar = rt.gameObject.AddComponent<SlantedGraphic>();
        bar.raycastTarget = false;
        return bar;
    }

    private Text MakeText(string name, Transform parent, string content, int size, Color color,
                          TextAnchor anchor, Vector2 pos, Vector2 rectSize, Vector2 anchorPoint, Vector2 pivot)
    {
        RectTransform rt = MakeRect(name, parent, pos, rectSize, anchorPoint, pivot);
        Text text = rt.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.color = color;
        text.alignment = anchor;
        text.text = content;
        text.raycastTarget = false;
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }
}

using UnityEngine;

/// <summary>
/// Drop this on the same GameObject as GranularEngineAudio to get an on-screen
/// debug panel: RPM/load sliders (for testing without a real car controller),
/// live grain stats, and a scrolling output-level graph.
/// Pure OnGUI, no extra packages needed — safe to strip out for a real build.
/// </summary>
[RequireComponent(typeof(GranularEngineAudio))]
public class GranularEngineVisualizer : MonoBehaviour
{
    public GranularEngineAudio engine;
    public CarEngineDataBridge bridge; // optional — auto-found if present

    [Header("Test controls")]
    [Tooltip("If on, shows sliders that directly drive rpm/load — handy before you've wired up a real car controller.")]
    public bool drivingWithSliders = true;

    [Header("Graph")]
    public int graphWidth = 300;
    public int graphHeight = 80;
    public int historyLength = 300;

    private Texture2D graphTex;
    private Color32[] pixels;
    private float[] rmsHistory;
    private int historyIndex;

    void Start()
    {
        if (engine == null) engine = GetComponent<GranularEngineAudio>();
        if (bridge == null) bridge = GetComponent<CarEngineDataBridge>();

        rmsHistory = new float[historyLength];
        graphTex = new Texture2D(graphWidth, graphHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point
        };
        pixels = new Color32[graphWidth * graphHeight];
    }

    void Update()
    {
        if (engine == null) return;

        rmsHistory[historyIndex] = engine.LastOutputRMS;
        historyIndex = (historyIndex + 1) % historyLength;
        DrawGraph();
    }

    void DrawGraph()
    {
        var bg = new Color32(15, 15, 20, 255);
        var line = new Color32(80, 220, 140, 255);

        for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

        for (int x = 0; x < graphWidth; x++)
        {
            int histIdx = (historyIndex + x * historyLength / graphWidth) % historyLength;
            // RMS from grain synthesis is usually small; scale up for a readable graph
            float v = Mathf.Clamp01(rmsHistory[histIdx] * 4f);
            int barHeight = Mathf.RoundToInt(v * (graphHeight - 1));
            for (int y = 0; y <= barHeight; y++)
                pixels[y * graphWidth + x] = line;
        }

        graphTex.SetPixels32(pixels);
        graphTex.Apply(false);
    }

    void OnGUI()
    {
        if (engine == null) return;

        GUILayout.BeginArea(new Rect(10, 10, graphWidth + 20, bridge != null ? 480 : 320), GUI.skin.box);
        GUILayout.Label("<b>Granular Engine Audio — Debug</b>", new GUIStyle(GUI.skin.label) { richText = true });

        if (drivingWithSliders)
        {
            GUILayout.Label($"RPM: {engine.rpmNormalized:F2}");
            engine.rpmNormalized = GUILayout.HorizontalSlider(engine.rpmNormalized, 0f, 1f);

            GUILayout.Label($"Load: {engine.load:F2}");
            engine.load = GUILayout.HorizontalSlider(engine.load, 0f, 1f);
        }

        GUILayout.Space(6);
        if (engine.useThrottleBasedMix)
            GUILayout.Label($"Throttle mix: {engine.throttlePosition:F2} (x CDI {engine.cdiIgnitionMultiplier:F2})");
        else
            GUILayout.Label($"State: {(engine.IsAccelerating ? "accelerating (rev-up clip)" : "decelerating (rev-down clip)")}");
        GUILayout.Label($"Active grains: {engine.ActiveGrainCount}");
        GUILayout.Label($"Grain rate: {engine.CurrentGrainRate:F1} / sec");

        float overlap = engine.ApproxOverlap;
        var overlapStyle = new GUIStyle(GUI.skin.label);
        overlapStyle.normal.textColor = overlap < 2f ? Color.red : (overlap < 3f ? Color.yellow : Color.green);
        GUILayout.Label($"Approx. overlap: {overlap:F1}x  (aim for 3-6x — below ~2x sounds gappy/robotic)", overlapStyle);

        GUILayout.Label($"Last pitch: {engine.CurrentPitch:F2}x");
        GUILayout.Label($"Output RMS: {engine.LastOutputRMS:F3}");

        GUILayout.Space(6);
        GUILayout.Label("Output level (scrolling):");
        GUILayout.Box(graphTex, GUILayout.Width(graphWidth), GUILayout.Height(graphHeight));

        if (bridge != null)
        {
            GUILayout.Space(10);
            GUILayout.Label("<b>Visual Scripting bridge — raw values</b>", new GUIStyle(GUI.skin.label) { richText = true });
            DrawVarLine("Engine_RPM_1", bridge.DebugRpm, bridge.RpmVarFound);
            DrawVarLine("Engine_Idle_RPM", bridge.DebugIdleRpm, bridge.IdleRpmVarFound);
            DrawVarLine("Engine_Redline", bridge.DebugRedline, bridge.RedlineVarFound);
            DrawVarLine("EngineThrottlePosition", bridge.DebugThrottle, bridge.ThrottleVarFound);
            DrawVarLine("CDI_Ignition", bridge.DebugCdi, bridge.CdiVarFound);

            GUILayout.Space(4);
            float computed = bridge.DebugRedline > 0.0001f
                ? (bridge.DebugRpm + bridge.DebugIdleRpm) / bridge.DebugRedline
                : 0f;
            GUILayout.Label($"(RPM+Idle)/Redline = {computed:F3}  ->  clamped: {engine.rpmNormalized:F3}");
        }

        GUILayout.EndArea();
    }

    void DrawVarLine(string name, float value, bool found)
    {
        var style = new GUIStyle(GUI.skin.label);
        style.normal.textColor = found ? Color.green : Color.red;
        string status = found ? "" : "  (NOT FOUND — using fallback)";
        GUILayout.Label($"{name}: {value:F2}{status}", style);
    }
}

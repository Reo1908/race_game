using UnityEngine;

namespace RevAudio
{
    /// <summary>
    /// Test rig for the granular engine. Simulates a crankshaft so you can hear the engine
    /// react the way it will in the game - free revs, a rev limiter that cuts ignition, and
    /// a gearbox whose shifts drop the RPM hard, which is the case the engine has to survive
    /// without artefacts.
    ///
    /// Everything is also reachable from the on-screen panel, so it works no matter which
    /// input backend the project is set to.
    /// </summary>
    [AddComponentMenu("Audio/REV Test Driver")]
    public class RevTestDriver : MonoBehaviour
    {
        public enum DriveMode { FreeRev, Gearbox, ManualRpm }

        [Header("Target")]
        public RevGranularEngine engine;

        [Header("Mode")]
        public DriveMode mode = DriveMode.FreeRev;
        [Range(0f, 1f)] public float throttle = 0f;

        [Header("Simulated engine")]
        [Tooltip("How fast the engine picks up at full throttle, with no load, in rpm per second.")]
        public float revUpRate = 9000f;
        [Tooltip("How fast it drops on a closed throttle, in rpm per second.")]
        public float engineBrakeRate = 5000f;
        [Tooltip("Extra drag that scales with RPM, so it settles instead of running away.")]
        [Range(0f, 4f)] public float rpmDrag = 0.9f;
        [Tooltip("Load slows the pick-up down, the way a gear does.")]
        [Range(0f, 1f)] public float gearLoad = 0.55f;

        [Header("Rev limiter")]
        public bool limiterEnabled = true;
        [Tooltip("How long ignition stays cut once the limiter trips.")]
        [Range(10f, 200f)] public float limiterCutMs = 55f;
        [Range(0f, 1f)] public float limiterCutDepth = 1f;

        [Header("Gearbox")]
        public float[] gearRatios = { 3.6f, 2.35f, 1.72f, 1.32f, 1.06f, 0.86f };
        [Tooltip("Ignition cut during a shift.")]
        [Range(0f, 400f)] public float shiftCutMs = 110f;
        public bool autoShift = true;
        [Range(0.5f, 1f)] public float autoShiftUpAt = 0.95f;
        [Range(0.1f, 0.8f)] public float autoShiftDownAt = 0.35f;

        [Header("UI")]
        public bool showPanel = true;
        public bool showMeters = true;

        float rpm;
        float ignition = 1f;
        float limiterTimer;
        float shiftTimer;
        int gear;
        float manualRpm = 1500f;

        public float Rpm { get { return rpm; } }
        public int Gear { get { return gear; } }

        void Reset()
        {
            engine = GetComponent<RevGranularEngine>();
        }

        void OnEnable()
        {
            if (engine == null) engine = GetComponent<RevGranularEngine>();
            if (engine != null) rpm = engine.rpmRange.x;
            manualRpm = rpm;
        }

        void Update()
        {
            if (engine == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            ReadKeys(dt);

            float idle = engine.rpmRange.x;
            float redline = engine.rpmRange.y;

            if (mode == DriveMode.ManualRpm)
            {
                rpm = Mathf.Clamp(manualRpm, idle, redline);
                ignition = 1f;
            }
            else
            {
                SimulateEngine(dt, idle, redline);
            }

            engine.SetEngineState(rpm, throttle, throttle, ignition);
        }

        void SimulateEngine(float dt, float idle, float redline)
        {
            // Higher gears accelerate the engine more slowly - that is what makes the RPM
            // ramp shape change with the gear instead of always sweeping at the same speed.
            float load = 1f;
            if (mode == DriveMode.Gearbox && gearRatios != null && gearRatios.Length > 0)
            {
                int index = Mathf.Clamp(gear, 0, gearRatios.Length - 1);
                float top = gearRatios[gearRatios.Length - 1];
                float spread = Mathf.Max(0.01f, gearRatios[0] - top);
                load = Mathf.Lerp(1f, 1f - gearLoad, (gearRatios[0] - gearRatios[index]) / spread);
            }

            float effectiveThrottle = throttle * ignition;
            float up = effectiveThrottle * revUpRate * load;
            float down = (1f - effectiveThrottle) * engineBrakeRate;
            float drag = rpmDrag * (rpm - idle) * effectiveThrottle;

            rpm += (up - down - drag) * dt;

            if (limiterTimer > 0f)
            {
                limiterTimer -= dt;
                ignition = Mathf.Lerp(1f, 1f - limiterCutDepth, 1f);
                if (limiterTimer <= 0f) ignition = 1f;
            }
            else if (limiterEnabled && rpm >= redline)
            {
                rpm = redline;
                limiterTimer = limiterCutMs * 0.001f;
                ignition = 1f - limiterCutDepth;
            }
            else
            {
                ignition = 1f;
            }

            if (shiftTimer > 0f)
            {
                shiftTimer -= dt;
                ignition = 0f;
                if (shiftTimer <= 0f) ignition = 1f;
            }

            rpm = Mathf.Clamp(rpm, idle, redline * 1.02f);

            if (mode == DriveMode.Gearbox && autoShift && shiftTimer <= 0f && gearRatios != null && gearRatios.Length > 1)
            {
                float normalised = Mathf.InverseLerp(idle, redline, rpm);
                if (normalised > autoShiftUpAt && gear < gearRatios.Length - 1) Shift(1);
                else if (normalised < autoShiftDownAt && gear > 0) Shift(-1);
            }
        }

        public void Shift(int direction)
        {
            if (gearRatios == null || gearRatios.Length < 2) return;
            int next = Mathf.Clamp(gear + direction, 0, gearRatios.Length - 1);
            if (next == gear) return;

            // The RPM drop across a shift is the whole point of this test: it is the fastest
            // legitimate RPM change the engine ever has to follow.
            rpm *= gearRatios[next] / gearRatios[gear];
            gear = next;
            shiftTimer = shiftCutMs * 0.001f;
        }

        bool keyboardOwnsThrottle;

        void ReadKeys(float dt)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            bool up = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.Space);
            bool down = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow);

            if (up) { throttle = Mathf.MoveTowards(throttle, 1f, dt * 4f); keyboardOwnsThrottle = true; }
            else if (down) { throttle = Mathf.MoveTowards(throttle, 0f, dt * 8f); keyboardOwnsThrottle = true; }
            else if (keyboardOwnsThrottle) throttle = Mathf.MoveTowards(throttle, 0f, dt * 3f);

            if (Input.GetKeyDown(KeyCode.E)) Shift(1);
            if (Input.GetKeyDown(KeyCode.Q)) Shift(-1);
            if (Input.GetKeyDown(KeyCode.M)) mode = (DriveMode)(((int)mode + 1) % 3);
            if (Input.GetKeyDown(KeyCode.Tab) && engine != null) engine.antiPhasing = !engine.antiPhasing;
#else
            // No legacy input backend compiled in - the panel is the only way in, so the
            // keyboard never takes ownership of the throttle.
            if (keyboardOwnsThrottle) keyboardOwnsThrottle = false;
#endif
        }

        void OnGUI()
        {
            if (!showPanel || engine == null) return;

            GUILayout.BeginArea(new Rect(10f, 10f, 330f, Screen.height - 20f), GUI.skin.box);
            GUILayout.Label("<b>REV granular engine - test rig</b>", RichLabel());

            GUILayout.BeginHorizontal();
            for (int i = 0; i < 3; i++)
            {
                DriveMode m = (DriveMode)i;
                if (GUILayout.Toggle(mode == m, ModeName(m), GUI.skin.button) && mode != m) mode = m;
            }
            GUILayout.EndHorizontal();

            float idle = engine.rpmRange.x;
            float redline = engine.rpmRange.y;

            if (mode == DriveMode.ManualRpm)
            {
                GUILayout.Label(string.Format("RPM  {0:0}", rpm));
                manualRpm = GUILayout.HorizontalSlider(manualRpm, idle, redline);
            }
            else
            {
                Bar("RPM", Mathf.InverseLerp(idle, redline, rpm), string.Format("{0:0}", rpm),
                    rpm >= redline * 0.985f ? Color.red : new Color(0.45f, 0.8f, 1f));

                GUILayout.BeginHorizontal();
                GUILayout.Label("Throttle", GUILayout.Width(60));
                float t = GUILayout.HorizontalSlider(throttle, 0f, 1f);
                if (!Mathf.Approximately(t, throttle)) { throttle = t; keyboardOwnsThrottle = false; }
                GUILayout.Label(string.Format("{0:0.00}", throttle), GUILayout.Width(36));
                GUILayout.EndHorizontal();

                if (GUILayout.RepeatButton("Hold throttle")) { throttle = 1f; keyboardOwnsThrottle = false; }
            }

            if (mode == DriveMode.Gearbox)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Gear " + (gear + 1), GUILayout.Width(60));
                if (GUILayout.Button("Down")) Shift(-1);
                if (GUILayout.Button("Up")) Shift(1);
                autoShift = GUILayout.Toggle(autoShift, "Auto");
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            engine.antiPhasing = GUILayout.Toggle(engine.antiPhasing, "Anti-phasing (Tab)");
            engine.rpmDifferenceResampling = GUILayout.Toggle(engine.rpmDifferenceResampling, "RPM difference resampling");
            engine.gateEnabled = GUILayout.Toggle(engine.gateEnabled, "Noise gate");
            engine.lowpassEnabled = GUILayout.Toggle(engine.lowpassEnabled, "Lowpass");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Both layer", GUILayout.Width(70));
            engine.bothLayerLevel = GUILayout.HorizontalSlider(engine.bothLayerLevel, 0f, 1f);
            GUILayout.Label(string.Format("{0:0.00}", engine.bothLayerLevel), GUILayout.Width(36));
            GUILayout.EndHorizontal();

            float engGain, exhGain;
            engine.ComputeBusGains(engine.Frontness, out engGain, out exhGain);
            Bar("Engine / exhaust", engGain / Mathf.Max(0.0001f, engGain + exhGain),
                string.Format("eng {0:0.00}   exh {1:0.00}", engGain, exhGain), new Color(0.45f, 0.8f, 1f));

            if (showMeters)
            {
                RevEngineCore core = engine.Core;
                GUILayout.Space(6);
                GUILayout.Label("<b>DSP</b>", RichLabel());
                Bar("Load", Mathf.Clamp01(core.MeterLoad), string.Format("{0:0.0}% of the audio budget", core.MeterLoad * 100f),
                    core.MeterLoad > 0.6f ? Color.red : new Color(0.5f, 0.9f, 0.6f));
                Bar("Peak", Mathf.Clamp01(core.MeterPeak), string.Format("{0:0.00}", core.MeterPeak),
                    core.MeterPeak > 0.98f ? Color.red : new Color(0.5f, 0.9f, 0.6f));
                GUILayout.Label(string.Format("grains {0}   rate {1:0} Hz   size {2:0.0} ms   overlap {3:0.00}x",
                    core.MeterActiveGrains, core.MeterRateHz, core.MeterSizeMs, core.MeterOverlap));
                GUILayout.Label(string.Format("accel/decel {0:0.00}   gate {1:0.00}   ignition {2:0.00}",
                    core.MeterAccelBlend, core.MeterGateGain, ignition));
                GUILayout.Label(string.Format("playhead  engUp {0:0.000}  engDn {1:0.000}  exhUp {2:0.000}  exhDn {3:0.000}",
                    core.MeterPlayhead[0], core.MeterPlayhead[1], core.MeterPlayhead[2], core.MeterPlayhead[3]));
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            GUILayout.Space(6);
            GUILayout.Label("W / Space throttle   S off throttle   Q / E shift   M mode   Tab anti-phasing");
#endif
            GUILayout.EndArea();
        }

        static string ModeName(DriveMode m)
        {
            switch (m)
            {
                case DriveMode.FreeRev: return "Free rev";
                case DriveMode.Gearbox: return "Gearbox";
                default: return "Manual";
            }
        }

        static GUIStyle richLabel;
        static GUIStyle RichLabel()
        {
            if (richLabel == null)
            {
                richLabel = new GUIStyle(GUI.skin.label);
                richLabel.richText = true;
            }
            return richLabel;
        }

        static Texture2D barTexture;
        void Bar(string label, float fill01, string value, Color colour)
        {
            if (barTexture == null)
            {
                barTexture = new Texture2D(1, 1);
                barTexture.SetPixel(0, 0, Color.white);
                barTexture.Apply();
                barTexture.hideFlags = HideFlags.HideAndDontSave;
            }

            GUILayout.Label(label + "   " + value);
            Rect r = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true));
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(r, barTexture);
            GUI.color = colour;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill01), r.height), barTexture);
            GUI.color = previous;
        }
    }
}

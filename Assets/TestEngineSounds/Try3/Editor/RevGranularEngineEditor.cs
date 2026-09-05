using UnityEditor;
using UnityEngine;

namespace RevAudio.EditorTools
{
    [CustomEditor(typeof(RevGranularEngine))]
    public class RevGranularEngineEditor : Editor
    {
        RevGranularEngine engine;

        bool showMeters = true;
        bool showSamples = true;
        bool showRpm;
        bool showGranular;
        bool showShape;
        bool showAntiPhase;
        bool showDirection;
        bool showFilter;
        bool showMod;
        bool showBake;

        int analyseCylinders = 4;
        float analyseMinRpm = 500f;
        float analyseMaxRpm = 9500f;
        string analyseMessage;

        readonly RevBakeSettings bakeSettings = new RevBakeSettings();

        float[] windowCache;
        int windowCacheKey = int.MinValue;

        static readonly Color ColEngine = new Color(0.35f, 0.72f, 1f);
        static readonly Color ColExhaust = new Color(1f, 0.55f, 0.28f);

        void OnEnable()
        {
            engine = (RevGranularEngine)target;
            analyseCylinders = engine.cylinders;
            analyseMinRpm = Mathf.Max(100f, engine.rpmRange.x * 0.5f);
            analyseMaxRpm = engine.rpmRange.y * 1.25f;
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        void OnEditorUpdate()
        {
            if (engine == null) return;
            if (Application.isPlaying) { Repaint(); return; }
            if (engine.previewInEditMode)
            {
                // ExecuteAlways alone only ticks on scene changes - this keeps the engine
                // running continuously while the preview is on.
                EditorApplication.QueuePlayerLoopUpdate();
                Repaint();
            }
        }

        SerializedProperty P(string path) { return serializedObject.FindProperty(path); }

        bool BeginSection(ref bool state, string title)
        {
            state = EditorGUILayout.BeginFoldoutHeaderGroup(state, title);
            if (state) EditorGUI.indentLevel++;
            return state;
        }

        void EndSection(bool state)
        {
            if (state) EditorGUI.indentLevel--;
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        void Fields(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                SerializedProperty p = P(names[i]);
                if (p != null) EditorGUILayout.PropertyField(p, true);
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            DrawPreview();
            DrawMeters();

            if (BeginSection(ref showSamples, "Samples")) DrawSamples();
            EndSection(showSamples);

            if (BeginSection(ref showRpm, "RPM group + response"))
            {
                Fields("rpmGroupEnabled");
                using (new EditorGUI.DisabledScope(!engine.rpmGroupEnabled))
                {
                    Fields("rpmRange", "cylinders", "rpmDifferenceResampling", "maxResampleSemitones");
                    EditorGUILayout.HelpBox(
                        string.Format("Firing frequency: {0:0.0} Hz at idle, {1:0.0} Hz at redline (4-stroke, {2} cylinders).",
                            engine.rpmRange.x * engine.cylinders / 120f,
                            engine.rpmRange.y * engine.cylinders / 120f,
                            engine.cylinders),
                        MessageType.None);
                }
                EditorGUILayout.Space(2);
                Fields("rpmSmoothMs", "rpmSnapDelta", "rpmRateSmoothMs", "rpmRateReference",
                       "useThrottleForBlend", "accelBlendMs", "rpmRateThreshold");
            }
            EndSection(showRpm);

            if (BeginSection(ref showGranular, "Granular"))
            {
                Fields("grainRate", "grainRateRange", "rateFollowsFiring", "rateFiringMultiplier");
                EditorGUILayout.Space(2);
                Fields("grainSizeRange", "sizeFollowsRpm", "sizeFollowsRpmRate", "overlapRange");
                EditorGUILayout.Space(2);
                Fields("timingJitter", "sizeJitter", "levelJitter", "positionJitterMs", "positionOffsetMs",
                       "pitchJitterCents", "stereoSpread");
                EditorGUILayout.Space(2);
                Fields("normaliseOverlap", "hermiteInterpolation", "maxGrains", "controlBlockSamples");
                int sr = AudioSettings.outputSampleRate;
                EditorGUILayout.LabelField(" ", string.Format("internal control rate: {0:0} Hz  (output {1} Hz)",
                    sr / (float)Mathf.Max(1, engine.controlBlockSamples), sr), EditorStyles.miniLabel);
            }
            EndSection(showGranular);

            if (BeginSection(ref showShape, "Grain shape"))
            {
                Fields("grainShape", "shapeParameter", "shapeSkew");
                if (engine.grainShape == GrainShape.Custom) Fields("customShape");
                DrawWindowPreview();
            }
            EndSection(showShape);

            if (BeginSection(ref showAntiPhase, "Anti-phasing"))
            {
                Fields("antiPhasing");
                using (new EditorGUI.DisabledScope(!engine.antiPhasing))
                    Fields("antiPhaseMaxCycles", "quantiseGrainLength");
                Fields("layerDecorrelationMs");
                if (engine.antiPhasing && !engine.rpmGroupEnabled)
                    EditorGUILayout.HelpBox("Anti-phasing needs the RPM group: it quantises read offsets to whole firing cycles, which needs the cylinder count.", MessageType.Warning);
            }
            EndSection(showAntiPhase);

            if (BeginSection(ref showDirection, "Directional mix"))
            {
                Fields("listenerOverride", "directionalAmount", "minSideLevel", "directionalSharpness",
                       "bothLayerLevel", "engineLevel", "exhaustLevel", "overrideDirection");
                using (new EditorGUI.DisabledScope(!engine.overrideDirection)) Fields("directionOverride");
                DrawDirectionMeter();
            }
            EndSection(showDirection);

            if (BeginSection(ref showFilter, "Filtering"))
            {
                Fields("dcBlock", "highpassEnabled");
                using (new EditorGUI.DisabledScope(!engine.highpassEnabled)) Fields("highpassHz", "highpassQ");
                Fields("lowpassEnabled");
                using (new EditorGUI.DisabledScope(!engine.lowpassEnabled)) Fields("lowpassHz", "lowpassQ");
                Fields("notchEnabled");
                using (new EditorGUI.DisabledScope(!engine.notchEnabled)) Fields("notchHz", "notchQ");
                Fields("gateEnabled");
                using (new EditorGUI.DisabledScope(!engine.gateEnabled))
                    Fields("gateThresholdDb", "gateRangeDb", "gateAttackMs", "gateReleaseMs", "gateHoldMs");
                EditorGUILayout.Space(2);
                Fields("masterVolume", "softClip");
            }
            EndSection(showFilter);

            if (BeginSection(ref showMod, "Modulation matrix")) DrawModulation();
            EndSection(showMod);

            if (BeginSection(ref showBake, "Bake")) DrawBake();
            EndSection(showBake);

            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();

            if (changed)
            {
                engine.Rebuild(false);
                if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        // ------------------------------------------------------------------ preview
        void DrawPreview()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Live preview", EditorStyles.boldLabel, GUILayout.Width(84));

            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField(engine.IsRunning ? "running (play mode)" : "not running", EditorStyles.miniLabel);
            }
            else
            {
                bool wasOn = engine.previewInEditMode;
                bool isOn = GUILayout.Toggle(wasOn, wasOn ? "Stop" : "Play", EditorStyles.miniButton, GUILayout.Width(56));
                if (isOn != wasOn)
                {
                    Undo.RecordObject(engine, "REV preview");
                    engine.previewInEditMode = isOn;
                    engine.Rebuild(false);
                    engine.SyncPlayback();
                }

                using (new EditorGUI.DisabledScope(!isOn))
                {
                    SerializedProperty sweep = P("previewSweep");
                    sweep.boolValue = GUILayout.Toggle(sweep.boolValue, "Auto sweep", EditorStyles.miniButton, GUILayout.Width(80));
                    EditorGUILayout.PropertyField(P("previewSweepSeconds"), GUIContent.none, GUILayout.Width(45));
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(engine.previewSweep && !Application.isPlaying))
            {
                SerializedProperty rpmProp = P("rpm");
                rpmProp.floatValue = EditorGUILayout.Slider("RPM", rpmProp.floatValue, engine.rpmRange.x, engine.rpmRange.y);
            }
            EditorGUILayout.PropertyField(P("throttle"));
            EditorGUILayout.PropertyField(P("load"));
            EditorGUILayout.PropertyField(P("ignition"));

            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------------ meters
        void DrawMeters()
        {
            if (!BeginSection(ref showMeters, "Meters")) { EndSection(showMeters); return; }

            RevEngineCore core = engine.Core;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            Rect loadRect = EditorGUILayout.GetControlRect();
            float loadPercent = Mathf.Clamp01(core.MeterLoad);
            EditorGUI.ProgressBar(loadRect, loadPercent,
                string.Format("DSP load {0:0.0}%   ({1} grains)", core.MeterLoad * 100f, core.MeterActiveGrains));

            EditorGUILayout.LabelField(
                string.Format("rate {0:0} Hz    size {1:0.0} ms    overlap {2:0.00}x",
                    core.MeterRateHz, core.MeterSizeMs, core.MeterOverlap), EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                string.Format("rpm {0:0}    accel/decel {1:0.00}    gate {2:0.00}    peak {3:0.00}  rms {4:0.000}",
                    core.MeterRpm, core.MeterAccelBlend, core.MeterGateGain, core.MeterPeak, core.MeterRms),
                EditorStyles.miniLabel);

            Rect meter = EditorGUILayout.GetControlRect(GUILayout.Height(6f));
            EditorGUI.DrawRect(meter, new Color(0f, 0f, 0f, 0.35f));
            Rect fill = meter;
            fill.width *= Mathf.Clamp01(core.MeterPeak);
            EditorGUI.DrawRect(fill, core.MeterPeak > 0.98f ? Color.red : new Color(0.4f, 0.9f, 0.5f));

            EditorGUILayout.EndVertical();
            EndSection(showMeters);
        }

        void DrawDirectionMeter()
        {
            Rect r = EditorGUILayout.GetControlRect(GUILayout.Height(18f));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.25f));

            float engGain, exhGain;
            engine.ComputeBusGains(engine.Frontness, out engGain, out exhGain);
            float total = Mathf.Max(0.0001f, engGain + exhGain);

            Rect left = r;
            left.width = r.width * (exhGain / total);
            EditorGUI.DrawRect(left, ColExhaust * 0.85f);
            Rect right = r;
            right.x = left.xMax;
            right.width = r.xMax - right.x;
            EditorGUI.DrawRect(right, ColEngine * 0.85f);

            GUI.Label(r, string.Format("  exhaust {0:0.00}", exhGain), EditorStyles.miniLabel);
            GUIStyle rightAligned = new GUIStyle(EditorStyles.miniLabel);
            rightAligned.alignment = TextAnchor.MiddleRight;
            GUI.Label(r, string.Format("engine {0:0.00}  ", engGain), rightAligned);

            EditorGUILayout.LabelField(" ", string.Format("listener frontness {0:0.00}", engine.Frontness), EditorStyles.miniLabel);
        }

        void DrawWindowPreview()
        {
            Rect r = EditorGUILayout.GetControlRect(GUILayout.Height(46f));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.25f));

            // Cached: this repaints at editor frame rate while previewing, and rebuilding a
            // 2049 entry table every repaint is pure garbage.
            int key = engine.grainShape.GetHashCode() * 31 +
                      Mathf.RoundToInt(engine.shapeParameter * 512f) * 7 +
                      Mathf.RoundToInt(engine.shapeSkew * 512f);
            if (windowCache == null || key != windowCacheKey || engine.grainShape == GrainShape.Custom)
            {
                float power;
                windowCache = RevGrainWindow.Build(engine.grainShape, engine.shapeParameter, engine.shapeSkew,
                                                   engine.customShape, out power);
                windowCacheKey = key;
            }
            float[] w = windowCache;
            const int steps = 96;
            Vector3[] points = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float v = w[Mathf.Clamp(Mathf.RoundToInt(t * RevGrainWindow.Resolution), 0, RevGrainWindow.Resolution)];
                points[i] = new Vector3(r.x + t * r.width, r.yMax - 2f - Mathf.Clamp01(v) * (r.height - 4f), 0f);
            }
            Handles.color = new Color(0.5f, 0.85f, 1f);
            Handles.DrawAAPolyLine(2f, points);
            Handles.color = Color.white;
        }

        // ------------------------------------------------------------------ samples
        void DrawSamples()
        {
            DrawLayer("engineUp", RevEngineCore.EngineUp, ColEngine);
            DrawLayer("engineDown", RevEngineCore.EngineDown, ColEngine);
            DrawLayer("exhaustUp", RevEngineCore.ExhaustUp, ColExhaust);
            DrawLayer("exhaustDown", RevEngineCore.ExhaustDown, ColExhaust);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("RPM detection", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            analyseCylinders = EditorGUILayout.IntSlider("Cylinders", analyseCylinders, 1, 16);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            analyseMinRpm = EditorGUILayout.FloatField("Search from", analyseMinRpm);
            analyseMaxRpm = EditorGUILayout.FloatField("to", analyseMaxRpm);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Detect RPM in all assigned clips"))
            {
                Undo.RecordObject(engine, "REV detect RPM");
                string combined = "";
                for (int i = 0; i < RevEngineCore.LayerCount; i++)
                {
                    RevSampleLayer layer = engine.GetLayerSettings(i);
                    if (layer == null || layer.clip == null) continue;
                    string message;
                    RevSampleAnalyser.Analyse(layer, analyseCylinders, analyseMinRpm, analyseMaxRpm, out message);
                    combined += RevGranularEngine.LayerName(i) + " - " + message + "\n";
                }
                analyseMessage = combined.TrimEnd();
                EditorUtility.SetDirty(engine);
                engine.Rebuild(false);
            }

            if (!string.IsNullOrEmpty(analyseMessage))
                EditorGUILayout.HelpBox(analyseMessage, MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        void DrawLayer(string propertyName, int index, Color accent)
        {
            SerializedProperty root = P(propertyName);
            if (root == null) return;
            RevSampleLayer layer = engine.GetLayerSettings(index);
            if (layer == null) return;

            EditorGUILayout.Space(3);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            Rect header = EditorGUILayout.GetControlRect(GUILayout.Height(16f));
            Rect stripe = new Rect(header.x - 2f, header.y, 3f, header.height);
            EditorGUI.DrawRect(stripe, accent);
            Rect toggleRect = new Rect(header.x + 6f, header.y, 16f, header.height);
            Rect labelRect = new Rect(header.x + 22f, header.y, header.width - 22f, header.height);

            SerializedProperty enabled = root.FindPropertyRelative("enabled");
            enabled.boolValue = EditorGUI.Toggle(toggleRect, enabled.boolValue);
            EditorGUI.LabelField(labelRect, RevGranularEngine.LayerName(index), EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                EditorGUILayout.PropertyField(root.FindPropertyRelative("clip"));

                AudioClip clip = layer.clip;
                if (clip != null && clip.loadType != AudioClipLoadType.DecompressOnLoad)
                {
                    EditorGUILayout.HelpBox(
                        "Load Type is " + clip.loadType + ". The engine reads the raw samples once at load, " +
                        "which needs Decompress On Load (Compressed In Memory and Streaming cannot be read).",
                        MessageType.Warning);
                }

                EditorGUILayout.PropertyField(root.FindPropertyRelative("playheadRange"));

                bool usingProfile = layer.hasProfile && layer.useDetectedProfile;
                using (new EditorGUI.DisabledScope(usingProfile))
                {
                    EditorGUILayout.PropertyField(root.FindPropertyRelative("rpmAtRangeStart"));
                    EditorGUILayout.PropertyField(root.FindPropertyRelative("rpmAtRangeEnd"));
                    EditorGUILayout.PropertyField(root.FindPropertyRelative("positionToRpm"));
                }

                if (layer.hasProfile)
                {
                    EditorGUILayout.PropertyField(root.FindPropertyRelative("useDetectedProfile"),
                        new GUIContent("Use detected profile",
                            string.Format("Detected {0:0} - {1:0} rpm with {2} cylinders.",
                                layer.detectedMinRpm, layer.detectedMaxRpm, layer.detectedCylinders)));
                }

                DrawProfileGraph(layer, index, accent);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(root.FindPropertyRelative("gainDb"));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.PropertyField(root.FindPropertyRelative("pitchTrimCents"));
                EditorGUILayout.PropertyField(root.FindPropertyRelative("stereoWidth"));
                EditorGUILayout.PropertyField(root.FindPropertyRelative("forceMono"));

                using (new EditorGUI.DisabledScope(layer.clip == null))
                {
                    if (GUILayout.Button("Detect RPM in this clip", EditorStyles.miniButton))
                    {
                        Undo.RecordObject(engine, "REV detect RPM");
                        string message;
                        RevSampleAnalyser.Analyse(layer, analyseCylinders, analyseMinRpm, analyseMaxRpm, out message);
                        analyseMessage = RevGranularEngine.LayerName(index) + " - " + message;
                        EditorUtility.SetDirty(engine);
                        engine.Rebuild(false);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        void DrawProfileGraph(RevSampleLayer layer, int index, Color accent)
        {
            Rect r = EditorGUILayout.GetControlRect(GUILayout.Height(54f));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.28f));

            float rangeStart = Mathf.Clamp01(Mathf.Min(layer.playheadRange.x, layer.playheadRange.y));
            float rangeEnd = Mathf.Clamp01(Mathf.Max(layer.playheadRange.x, layer.playheadRange.y));

            // Everything outside the playhead range is greyed out - the playhead never goes
            // there, but grains may still read into it, which is the point.
            Color excluded = new Color(0f, 0f, 0f, 0.35f);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * rangeStart, r.height), excluded);
            EditorGUI.DrawRect(new Rect(r.x + r.width * rangeEnd, r.y, r.width * (1f - rangeEnd), r.height), excluded);

            bool useProfile = layer.hasProfile && layer.useDetectedProfile && layer.detectedRpm != null;
            float lo, hi;
            if (useProfile)
            {
                lo = float.MaxValue; hi = float.MinValue;
                for (int i = 0; i < layer.detectedRpm.Length; i++)
                {
                    lo = Mathf.Min(lo, layer.detectedRpm[i]);
                    hi = Mathf.Max(hi, layer.detectedRpm[i]);
                }
            }
            else
            {
                lo = Mathf.Min(layer.rpmAtRangeStart, layer.rpmAtRangeEnd);
                hi = Mathf.Max(layer.rpmAtRangeStart, layer.rpmAtRangeEnd);
            }
            if (hi - lo < 1f) hi = lo + 1f;

            const int steps = 128;
            Vector3[] points = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float rpmValue;
                if (useProfile)
                {
                    rpmValue = RevSampleAnalyser.SampleProfile(layer.detectedRpm, t);
                }
                else
                {
                    float local = Mathf.Clamp01(Mathf.InverseLerp(rangeStart, rangeEnd, t));
                    float shaped = layer.positionToRpm != null ? layer.positionToRpm.Evaluate(local) : local;
                    rpmValue = Mathf.Lerp(layer.rpmAtRangeStart, layer.rpmAtRangeEnd, shaped);
                }
                float y = Mathf.InverseLerp(lo, hi, rpmValue);
                points[i] = new Vector3(r.x + t * r.width, r.yMax - 3f - y * (r.height - 6f), 0f);
            }
            Handles.color = accent;
            Handles.DrawAAPolyLine(2f, points);

            RevLayerRuntime rt = engine.GetLayerRuntime(index);
            if (rt != null && rt.valid && (engine.IsRunning || Application.isPlaying))
            {
                float playhead = Mathf.Clamp01(engine.Core.MeterPlayhead[index]);
                Rect marker = new Rect(r.x + playhead * r.width - 1f, r.y, 2f, r.height);
                EditorGUI.DrawRect(marker, Color.white);
            }
            Handles.color = Color.white;

            GUI.Label(r, string.Format("  {0:0} rpm", hi), EditorStyles.miniLabel);
            GUI.Label(new Rect(r.x, r.yMax - 14f, r.width, 14f), string.Format("  {0:0} rpm", lo), EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------------ modulation
        void DrawModulation()
        {
            EditorGUILayout.PropertyField(P("lfo1"), true);
            EditorGUILayout.PropertyField(P("lfo2"), true);
            EditorGUILayout.Space(3);

            SerializedProperty list = P("modulation");
            EditorGUILayout.LabelField("Routes", EditorStyles.boldLabel);

            int removeAt = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty route = list.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(route.FindPropertyRelative("enabled"), GUIContent.none, GUILayout.Width(16));
                EditorGUILayout.PropertyField(route.FindPropertyRelative("source"), GUIContent.none);
                EditorGUILayout.LabelField("→", GUILayout.Width(14));
                EditorGUILayout.PropertyField(route.FindPropertyRelative("target"), GUIContent.none);
                if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(20))) removeAt = i;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(route.FindPropertyRelative("amount"), new GUIContent("Amount"));
                EditorGUILayout.PropertyField(route.FindPropertyRelative("bipolar"), new GUIContent("+/-"), GUILayout.Width(56));
                EditorGUILayout.PropertyField(route.FindPropertyRelative("shape"), GUIContent.none, GUILayout.Width(60));
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField(" ", TargetUnit((ModTarget)route.FindPropertyRelative("target").enumValueIndex),
                                           EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }

            if (removeAt >= 0) list.DeleteArrayElementAtIndex(removeAt);

            if (GUILayout.Button("Add route"))
            {
                list.InsertArrayElementAtIndex(list.arraySize);
                SerializedProperty added = list.GetArrayElementAtIndex(list.arraySize - 1);
                added.FindPropertyRelative("enabled").boolValue = true;
                added.FindPropertyRelative("source").enumValueIndex = (int)ModSource.RpmNormalised;
                added.FindPropertyRelative("target").enumValueIndex = (int)ModTarget.None;
                added.FindPropertyRelative("amount").floatValue = 0f;
                added.FindPropertyRelative("bipolar").boolValue = false;
                added.FindPropertyRelative("shape").animationCurveValue = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            }
        }

        static string TargetUnit(ModTarget target)
        {
            switch (target)
            {
                case ModTarget.GrainRate: return "amount is in Hz";
                case ModTarget.GrainSize:
                case ModTarget.GrainSizeMin:
                case ModTarget.GrainSizeMax: return "amount is in milliseconds";
                case ModTarget.Pitch: return "amount is in cents";
                case ModTarget.PositionOffset:
                case ModTarget.PositionJitter: return "amount is in milliseconds of source material";
                case ModTarget.LowpassCutoff:
                case ModTarget.HighpassCutoff:
                case ModTarget.NotchFreq: return "amount is in octaves";
                case ModTarget.GateThreshold: return "amount is in dB";
                case ModTarget.Overlap: return "amount is in grains";
                case ModTarget.None: return "pick a target";
                default: return "amount is linear (0..1 style)";
            }
        }

        // ------------------------------------------------------------------ bake
        void DrawBake()
        {
            EditorGUILayout.HelpBox(
                "Renders the engine offline through the same DSP, at any listening angle. " +
                "Sweep gives you one continuous idle-to-redline file; Steady state set gives you one " +
                "seamless loop per RPM step for a cheap sample-player fallback on low-end targets or distant cars.",
                MessageType.None);

            bakeSettings.mode = (RevBakeMode)EditorGUILayout.EnumPopup("Mode", bakeSettings.mode);
            bakeSettings.sampleRate = EditorGUILayout.IntPopup("Sample rate", bakeSettings.sampleRate,
                new[] { "44100", "48000", "96000" }, new[] { 44100, 48000, 96000 });
            bakeSettings.float32 = EditorGUILayout.Toggle("32-bit float", bakeSettings.float32);

            if (bakeSettings.mode == RevBakeMode.RpmSweep)
            {
                bakeSettings.sweepSeconds = EditorGUILayout.Slider("Sweep seconds", bakeSettings.sweepSeconds, 1f, 60f);
                bakeSettings.sweepUp = EditorGUILayout.Toggle("Include up sweep", bakeSettings.sweepUp);
                bakeSettings.sweepDown = EditorGUILayout.Toggle("Include down sweep", bakeSettings.sweepDown);
            }
            else
            {
                bakeSettings.steadySteps = EditorGUILayout.IntSlider("RPM steps", bakeSettings.steadySteps, 2, 48);
                bakeSettings.steadyLoopSeconds = EditorGUILayout.Slider("Loop seconds", bakeSettings.steadyLoopSeconds, 0.25f, 8f);
            }

            bakeSettings.direction01 = EditorGUILayout.Slider("Direction (0 rear .. 1 front)", bakeSettings.direction01, 0f, 1f);
            bakeSettings.throttle = EditorGUILayout.Slider("Throttle", bakeSettings.throttle, 0f, 1f);
            bakeSettings.load = EditorGUILayout.Slider("Load", bakeSettings.load, 0f, 1f);
            bakeSettings.normalise = EditorGUILayout.Toggle("Normalise", bakeSettings.normalise);
            bakeSettings.namePrefix = EditorGUILayout.TextField("Name prefix", bakeSettings.namePrefix);

            EditorGUILayout.BeginHorizontal();
            bakeSettings.outputFolder = EditorGUILayout.TextField("Output folder", bakeSettings.outputFolder);
            if (GUILayout.Button("...", GUILayout.Width(28)))
            {
                string picked = EditorUtility.SaveFolderPanel("Bake output folder", "Assets", "");
                if (!string.IsNullOrEmpty(picked))
                {
                    string dataPath = Application.dataPath.Replace('\\', '/');
                    picked = picked.Replace('\\', '/');
                    bakeSettings.outputFolder = picked.StartsWith(dataPath)
                        ? "Assets" + picked.Substring(dataPath.Length)
                        : picked;
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Bake now"))
                {
                    engine.Rebuild(true);
                    RevBaker.Bake(engine, bakeSettings);
                }
            }
            if (Application.isPlaying)
                EditorGUILayout.LabelField(" ", "Baking is disabled while in play mode.", EditorStyles.miniLabel);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RevAudio
{
    /// <summary>Serialised settings for one of the two LFOs in the modulation matrix.</summary>
    [Serializable]
    public class RevLfoSettings
    {
        public LfoShape shape = LfoShape.Sine;
        [Tooltip("Free-running rate in Hz.")]
        public float rateHz = 5f;
        [Tooltip("Ignore Rate Hz and run at the engine firing frequency times the multiplier. " +
                 "Use a multiplier of 0.5 for a half-order wobble, 1 for a firing-order pulse.")]
        public bool syncToFiringFrequency = false;
        [Range(0.05f, 8f)] public float firingMultiplier = 0.5f;
    }

    /// <summary>
    /// REV-style granular engine audio. Four recordings (engine up/down, exhaust up/down)
    /// are scrubbed by RPM instead of being pitched, so the recorded pitch profile - and
    /// with it the character and perceived power of the engine - stays intact.
    ///
    /// Drive it from your car controller with SetEngineState(rpm, throttle, load) or by
    /// writing the public rpm / throttle fields.
    /// </summary>
    [AddComponentMenu("Audio/REV Granular Engine")]
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class RevGranularEngine : MonoBehaviour
    {
        // ------------------------------------------------------------------ samples
        [Header("Samples")]
        public RevSampleLayer engineUp = new RevSampleLayer();
        public RevSampleLayer engineDown = new RevSampleLayer();
        public RevSampleLayer exhaustUp = new RevSampleLayer();
        public RevSampleLayer exhaustDown = new RevSampleLayer();

        // ------------------------------------------------------------------ engine state
        [Header("Engine state (drive these from the car)")]
        [Tooltip("Absolute engine RPM. Write it every frame - the engine de-zippers it internally at audio rate.")]
        public float rpm = 900f;
        [Range(0f, 1f)] public float throttle = 0f;
        [Range(0f, 1f)] public float load = 0f;
        [Tooltip("Spark / ignition gain. Drop it towards 0 for a rev limiter cut or a kill switch.")]
        [Range(0f, 1f)] public float ignition = 1f;

        // ------------------------------------------------------------------ rpm group
        [Header("RPM detection group")]
        [Tooltip("Master switch for the whole RPM group: cylinder-count aware phase alignment " +
                 "and RPM difference resampling. Off = pure position scrubbing.")]
        public bool rpmGroupEnabled = true;
        [Tooltip("x = idle RPM, y = redline. Also the range the preview slider and the normalised RPM API use.")]
        [MinMaxRange(0f, 20000f, "rpm")] public Vector2 rpmRange = new Vector2(900f, 8000f);
        [Tooltip("Cylinder count, four-stroke. Firing frequency = RPM * cylinders / 120.")]
        [Range(1, 16)] public int cylinders = 4;
        [Tooltip("After scrubbing to the closest recorded RPM, resample the grain to hit the " +
                 "requested RPM exactly. Also extends the recording below idle and past the redline.")]
        public bool rpmDifferenceResampling = true;
        [Tooltip("How far resampling is allowed to stretch the recording before it just clamps.")]
        [Range(0f, 24f)] public float maxResampleSemitones = 7f;

        [Header("RPM response")]
        [Tooltip("Time constant for the audio-rate RPM ramp. Small = tight, large = syrupy.")]
        [Range(0.5f, 60f)] public float rpmSmoothMs = 8f;
        [Tooltip("An RPM jump bigger than this snaps instead of gliding - gearshifts, respawns, teleports.")]
        public float rpmSnapDelta = 1200f;
        [Range(2f, 200f)] public float rpmRateSmoothMs = 35f;
        [Tooltip("RPM per second that counts as a full-scale change for the modulation sources.")]
        public float rpmRateReference = 4000f;

        [Header("Accel / decel crossfade")]
        [Tooltip("Blend on throttle instead of on the measured RPM rate.")]
        public bool useThrottleForBlend = false;
        [Range(1f, 300f)] public float accelBlendMs = 60f;
        [Tooltip("RPM per second at which the blend is fully on the accel recordings.")]
        public float rpmRateThreshold = 600f;

        // ------------------------------------------------------------------ granular
        [Header("Granular - rate")]
        [Tooltip("Base grain rate when Rate Follows Firing is 0.")]
        [Range(5f, 800f)] public float grainRate = 150f;
        [Tooltip("Hard limits for the grain rate after following and modulation.")]
        [MinMaxRange(5f, 800f, "Hz")] public Vector2 grainRateRange = new Vector2(35f, 420f);
        [Tooltip("Blend the rate towards the engine firing frequency. At 1 every grain lines up " +
                 "with one combustion event, which is what keeps the note solid at high RPM.")]
        [Range(0f, 1f)] public float rateFollowsFiring = 0.85f;
        [Range(0.25f, 4f)] public float rateFiringMultiplier = 1f;

        [Header("Granular - size")]
        [Tooltip("Grain length range. x is used at high RPM / fast changes, y at idle / steady state.")]
        [MinMaxRange(2f, 300f, "ms")] public Vector2 grainSizeRange = new Vector2(16f, 85f);
        [Range(0f, 1f)] public float sizeFollowsRpm = 0.7f;
        [Range(0f, 1f)] public float sizeFollowsRpmRate = 0.3f;
        [Tooltip("How many grains are allowed to sound at once. Below the minimum the stream " +
                 "gets gaps, above the maximum it smears and costs CPU. Grain size is trimmed to stay inside.")]
        [MinMaxRange(1f, 12f, "x")] public Vector2 overlapRange = new Vector2(2.5f, 6f);

        [Header("Granular - variation")]
        [Range(0f, 1f)] public float timingJitter = 0.12f;
        [Range(0f, 1f)] public float sizeJitter = 0.10f;
        [Range(0f, 1f)] public float levelJitter = 0.08f;
        [Tooltip("How far a grain may start from the playhead. Only used while anti-phasing is " +
                 "OFF - with it on the wander is measured in whole firing cycles instead (see below).")]
        public float positionJitterMs = 14f;
        [Tooltip("Constant offset of the read position. Negative reads slightly behind the playhead.")]
        public float positionOffsetMs = 0f;
        [Range(0f, 60f)] public float pitchJitterCents = 5f;
        [Range(0f, 1f)] public float stereoSpread = 0.25f;

        [Header("Granular - engine")]
        [Tooltip("Keep the perceived level constant while rate and size move around.")]
        public bool normaliseOverlap = true;
        [Tooltip("4-point Hermite instead of linear interpolation. Noticeably cleaner when " +
                 "resampling far from unity, roughly 30% more CPU in the read loop.")]
        public bool hermiteInterpolation = true;
        [Tooltip("Grain voice pool. Grains beyond this are dropped rather than cutting an audible one.")]
        [Range(8, 256)] public int maxGrains = 96;
        [Tooltip("Internal control block in samples. 32 = ~1.5 kHz control rate at 48 kHz. " +
                 "Lower reacts faster to RPM changes, higher costs less CPU.")]
        [Range(8, 256)] public int controlBlockSamples = 32;

        [Header("Grain shape")]
        public GrainShape grainShape = GrainShape.Hann;
        [Tooltip("Tukey: plateau width. Gaussian: sigma. Expo: decay steepness. Unused for the others.")]
        [Range(0f, 1f)] public float shapeParameter = 0.35f;
        [Tooltip("-1 = attack heavy (percussive), 0 = symmetric, +1 = decay heavy.")]
        [Range(-1f, 1f)] public float shapeSkew = 0f;
        public AnimationCurve customShape = new AnimationCurve(
            new Keyframe(0f, 0f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));

        [Header("Anti-phasing")]
        [Tooltip("Snaps the grain hop, the grain length and every read offset to whole firing " +
                 "cycles, so overlapping grains superimpose in phase instead of comb filtering " +
                 "(pitch-synchronous overlap-add). Turn it off to hear the difference.")]
        public bool antiPhasing = true;
        [Tooltip("How many whole firing cycles a grain may wander from the playhead. This is the " +
                 "position jitter while anti-phasing is on: the grain reads a different combustion " +
                 "cycle of the recording but still lands in phase.")]
        [Range(0f, 32f)] public float antiPhaseMaxCycles = 3f;
        [Tooltip("Round grain length to a whole number of firing cycles as well.")]
        public bool quantiseGrainLength = true;
        [Tooltip("Fixed read offset between the four layers so their streams never sit on top of each other.")]
        [Range(0f, 60f)] public float layerDecorrelationMs = 14f;

        // ------------------------------------------------------------------ directional mix
        [Header("Directional mix")]
        [Tooltip("Whose position decides front/back. Empty = the active AudioListener.")]
        public Transform listenerOverride;
        [Tooltip("0 = ignore the listener direction entirely, 1 = full front/back crossfade.")]
        [Range(0f, 1f)] public float directionalAmount = 1f;
        [Tooltip("Level the quiet side never drops below.")]
        [Range(0f, 1f)] public float minSideLevel = 0.12f;
        [Tooltip("Above 1 the transition from engine to exhaust gets tighter around the sides of the car.")]
        [Range(0.25f, 4f)] public float directionalSharpness = 1.4f;
        [Tooltip("The extra layer on top of the directional mix: how much of BOTH sides is always audible, " +
                 "regardless of where the listener stands.")]
        [Range(0f, 1f)] public float bothLayerLevel = 0.35f;
        [Range(0f, 2f)] public float engineLevel = 1f;
        [Range(0f, 2f)] public float exhaustLevel = 1f;
        [Tooltip("Ignore the listener and use the slider below. Handy while tuning and for baking.")]
        public bool overrideDirection = false;
        [Tooltip("0 = listener fully behind the car (exhaust), 1 = fully in front (engine).")]
        [Range(0f, 1f)] public float directionOverride = 1f;

        // ------------------------------------------------------------------ filtering
        [Header("Filtering")]
        [Tooltip("Removes the DC/subsonic offset that dense grain overlap can build up.")]
        public bool dcBlock = true;
        public bool highpassEnabled = true;
        [Range(10f, 500f)] public float highpassHz = 45f;
        [Range(0.1f, 4f)] public float highpassQ = 0.707f;
        public bool lowpassEnabled = true;
        [Range(500f, 22000f)] public float lowpassHz = 11000f;
        [Range(0.1f, 4f)] public float lowpassQ = 0.707f;
        [Tooltip("Narrow notch for a single resonance in the recording (mic tone, room mode, whine).")]
        public bool notchEnabled = false;
        [Range(50f, 12000f)] public float notchHz = 3000f;
        [Range(0.5f, 20f)] public float notchQ = 6f;
        [Tooltip("Downward expander. Pulls the hiss and room tone down between the loud parts, " +
                 "which granular overlap would otherwise multiply.")]
        public bool gateEnabled = true;
        [Range(-90f, -10f)] public float gateThresholdDb = -52f;
        [Range(0f, 48f)] public float gateRangeDb = 12f;
        [Range(0.05f, 50f)] public float gateAttackMs = 3f;
        [Range(1f, 500f)] public float gateReleaseMs = 120f;
        [Range(0f, 200f)] public float gateHoldMs = 40f;

        // ------------------------------------------------------------------ output
        [Header("Output")]
        [Tooltip("Default leaves headroom for all four layers plus the both-sides layer running at once.")]
        [Range(0f, 2f)] public float masterVolume = 0.75f;
        public bool softClip = true;

        // ------------------------------------------------------------------ modulation
        [Header("Modulation matrix")]
        public List<RevModRoute> modulation = new List<RevModRoute>();
        public RevLfoSettings lfo1 = new RevLfoSettings();
        public RevLfoSettings lfo2 = new RevLfoSettings { shape = LfoShape.SmoothNoise, rateHz = 1.7f };

        // ------------------------------------------------------------------ preview
        [Header("Live preview")]
        [Tooltip("Run the engine while the editor is not playing, so you can audition changes " +
                 "straight from the inspector.")]
        public bool previewInEditMode = false;
        [Tooltip("Sweep the preview RPM up and down automatically.")]
        public bool previewSweep = false;
        [Range(0.5f, 30f)] public float previewSweepSeconds = 6f;

        // ================================================================== runtime
        readonly RevEngineCore core = new RevEngineCore();
        readonly RevParams[] paramBuffers = new RevParams[3];
        readonly RevSampleLayer[] layerSettings = new RevSampleLayer[RevEngineCore.LayerCount];
        readonly RevLayerRuntime[] layerRuntimes = new RevLayerRuntime[RevEngineCore.LayerCount];
        readonly AudioClip[] boundClips = new AudioClip[RevEngineCore.LayerCount];

        AudioSource source;
        AudioClip carrier;
        AudioListener cachedListener;
        RevModRuntime modRuntime;
        float[] window;
        float windowPower = 0.375f;
        int paramCursor;
        int builtSampleRate;
        int builtMaxGrains;
        bool rebuildQueued;
        float frontness = 1f;
        float previewSweepPhase;
        float previewLastTime;

        [NonSerialized] public string lastError;

        public RevEngineCore Core { get { return core; } }
        public float Frontness { get { return frontness; } }
        public bool IsRunning { get { return source != null && source.isPlaying && core.ready; } }

        public RevLayerRuntime GetLayerRuntime(int index)
        {
            return (index >= 0 && index < RevEngineCore.LayerCount) ? layerRuntimes[index] : null;
        }

        public RevSampleLayer GetLayerSettings(int index)
        {
            switch (index)
            {
                case RevEngineCore.EngineUp: return engineUp;
                case RevEngineCore.EngineDown: return engineDown;
                case RevEngineCore.ExhaustUp: return exhaustUp;
                case RevEngineCore.ExhaustDown: return exhaustDown;
            }
            return null;
        }

        public static string LayerName(int index)
        {
            switch (index)
            {
                case RevEngineCore.EngineUp: return "Engine up";
                case RevEngineCore.EngineDown: return "Engine down";
                case RevEngineCore.ExhaustUp: return "Exhaust up";
                case RevEngineCore.ExhaustDown: return "Exhaust down";
            }
            return "?";
        }

        // ------------------------------------------------------------------ public API
        /// <summary>Everything the car controller needs to push, in one call.</summary>
        public void SetEngineState(float engineRpm, float throttle01, float load01 = 0f, float ignition01 = 1f)
        {
            rpm = engineRpm;
            throttle = Mathf.Clamp01(throttle01);
            load = Mathf.Clamp01(load01);
            ignition = Mathf.Clamp01(ignition01);
        }

        /// <summary>RPM as 0..1 between the idle and redline set in the RPM group.</summary>
        public float NormalisedRpm
        {
            get { return Mathf.InverseLerp(rpmRange.x, rpmRange.y, rpm); }
            set { rpm = Mathf.Lerp(rpmRange.x, rpmRange.y, Mathf.Clamp01(value)); }
        }

        // ------------------------------------------------------------------ lifecycle
        void Reset()
        {
            // Sensible starting point: the two down-sweeps run from redline back to idle.
            engineDown.rpmAtRangeStart = exhaustDown.rpmAtRangeStart = 8000f;
            engineDown.rpmAtRangeEnd = exhaustDown.rpmAtRangeEnd = 1000f;
            var src = GetComponent<AudioSource>();
            if (src != null)
            {
                src.playOnAwake = false;
                src.loop = true;
                src.spatialBlend = 1f;
                src.dopplerLevel = 0f;      // the granular engine already carries the pitch
            }
        }

        void OnEnable()
        {
            source = GetComponent<AudioSource>();
            AudioSettings.OnAudioConfigurationChanged += HandleAudioConfigChanged;
            Rebuild(true);
            SyncAudioSource();
        }

        void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= HandleAudioConfigChanged;
            core.ready = false;
            if (source != null && source.isPlaying) source.Stop();
            DestroyCarrier();
        }

        void HandleAudioConfigChanged(bool deviceChanged)
        {
            DestroyCarrier();
            Rebuild(false);
            SyncAudioSource();
        }

        void OnValidate()
        {
            rpmRange.x = Mathf.Max(0f, rpmRange.x);
            rpmRange.y = Mathf.Max(rpmRange.x + 100f, rpmRange.y);
            grainSizeRange.x = Mathf.Max(1f, grainSizeRange.x);
            grainSizeRange.y = Mathf.Max(grainSizeRange.x, grainSizeRange.y);
            grainRateRange.x = Mathf.Max(1f, grainRateRange.x);
            grainRateRange.y = Mathf.Max(grainRateRange.x, grainRateRange.y);
            rebuildQueued = true;      // applied on the next Update, off the serialisation path
        }

        // ------------------------------------------------------------------ build
        /// <summary>
        /// Rebuilds everything the audio thread reads. Safe to call at any time: samples are
        /// only decoded again when a clip reference actually changed, everything else is a
        /// cheap table rebuild.
        /// </summary>
        public void Rebuild(bool reloadSamples)
        {
            layerSettings[RevEngineCore.EngineUp] = engineUp;
            layerSettings[RevEngineCore.EngineDown] = engineDown;
            layerSettings[RevEngineCore.ExhaustUp] = exhaustUp;
            layerSettings[RevEngineCore.ExhaustDown] = exhaustDown;

            int sr = AudioSettings.outputSampleRate;
            if (sr != builtSampleRate || maxGrains != builtMaxGrains)
            {
                core.ready = false;
                core.Configure(sr, maxGrains);
                builtSampleRate = sr;
                builtMaxGrains = maxGrains;
            }

            // Fresh arrays every time - the audio thread may still be reading the old ones
            // through the previously published parameter block.
            window = RevGrainWindow.Build(grainShape, shapeParameter, shapeSkew, customShape, out windowPower);
            modRuntime = RevModRuntime.Build(modulation);

            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevSampleLayer settings = layerSettings[i];
                AudioClip clip = settings != null ? settings.clip : null;
                bool full = reloadSamples || layerRuntimes[i] == null || !layerRuntimes[i].valid || boundClips[i] != clip;

                if (full)
                {
                    string error;
                    layerRuntimes[i] = RevLayerRuntime.Build(settings, out error);
                    boundClips[i] = clip;
                    if (!string.IsNullOrEmpty(error) && error != lastError)
                    {
                        lastError = error;
                        Debug.LogWarning("[REV] " + error, this);
                    }
                }
                else
                {
                    layerRuntimes[i].RefreshMapping(settings);
                }
                core.SetLayer(i, layerRuntimes[i]);
            }

            for (int i = 0; i < paramBuffers.Length; i++)
                if (paramBuffers[i] == null) paramBuffers[i] = new RevParams();

            core.ready = true;
            PublishParams();
        }

        /// <summary>Starts or stops the carrier source after the preview toggle changed.</summary>
        public void SyncPlayback() { SyncAudioSource(); }

        void SyncAudioSource()
        {
            if (source == null) source = GetComponent<AudioSource>();
            if (source == null) return;

            bool shouldRun = Application.isPlaying || previewInEditMode;
            if (shouldRun)
            {
                EnsureCarrier();
                if (!source.isPlaying) source.Play();
            }
            else if (source.isPlaying)
            {
                source.Stop();
                core.ResetState();
            }
        }

        /// <summary>
        /// OnAudioFilterRead only runs on a source that is actually playing, so we feed the
        /// AudioSource a silent looping carrier and overwrite it. Doing it this way keeps
        /// Unity 3D panning, attenuation and mixer routing working normally.
        /// </summary>
        void EnsureCarrier()
        {
            if (carrier == null)
            {
                carrier = AudioClip.Create("REV_Carrier", 4096, 2, AudioSettings.outputSampleRate, false);
                carrier.hideFlags = HideFlags.HideAndDontSave;
            }
            if (source.clip != carrier) source.clip = carrier;
            source.loop = true;
            source.playOnAwake = false;
        }

        void DestroyCarrier()
        {
            if (carrier == null) return;
            if (source != null && source.clip == carrier) source.clip = null;
            if (Application.isPlaying) Destroy(carrier); else DestroyImmediate(carrier);
            carrier = null;
        }

        // ------------------------------------------------------------------ per frame
        void Update()
        {
            if (rebuildQueued)
            {
                rebuildQueued = false;
                Rebuild(false);
                SyncAudioSource();
            }

            if (!Application.isPlaying)
            {
                float now = Time.realtimeSinceStartup;
                float dt = Mathf.Clamp(now - previewLastTime, 0f, 0.25f);
                previewLastTime = now;
                if (previewSweep && previewInEditMode)
                {
                    previewSweepPhase += dt / Mathf.Max(0.5f, previewSweepSeconds);
                    previewSweepPhase -= Mathf.Floor(previewSweepPhase);
                    float tri = 1f - Mathf.Abs(2f * previewSweepPhase - 1f);
                    rpm = Mathf.Lerp(rpmRange.x, rpmRange.y, tri);
                    throttle = previewSweepPhase < 0.5f ? 1f : 0f;
                }
            }

            UpdateDirection();
            PublishParams();
        }

        void UpdateDirection()
        {
            if (overrideDirection)
            {
                frontness = Mathf.Clamp01(directionOverride);
                return;
            }

            Transform target = listenerOverride;
            if (target == null)
            {
                if (cachedListener == null) cachedListener = FindAnyObjectByType<AudioListener>();
                if (cachedListener != null) target = cachedListener.transform;
            }
            if (target == null) { frontness = 1f; return; }

            Vector3 delta = target.position - transform.position;
            float sq = delta.sqrMagnitude;
            if (sq < 1e-6f) { frontness = 0.5f; return; }
            frontness = 0.5f + 0.5f * Vector3.Dot(transform.forward, delta / Mathf.Sqrt(sq));
        }

        bool LayerHasAudio(int index)
        {
            RevLayerRuntime rt = layerRuntimes[index];
            RevSampleLayer s = layerSettings[index];
            return rt != null && rt.valid && s != null && s.enabled;
        }

        /// <summary>
        /// Resolves the front/back crossfade plus the always-on "both sides" layer into two
        /// bus gains. Kept public so the offline baker can render any listening angle.
        /// </summary>
        public void ComputeBusGains(float direction01, out float engineGain, out float exhaustGain)
        {
            float f = Mathf.Clamp01(direction01);
            float sharp = Mathf.Clamp(directionalSharpness, 0.25f, 4f);
            float front = Mathf.Pow(f, sharp);
            float back = Mathf.Pow(1f - f, sharp);
            float sum = front + back;
            if (sum > 1e-5f) { front /= sum; back /= sum; }

            float e = Mathf.Lerp(minSideLevel, 1f, front);
            float x = Mathf.Lerp(minSideLevel, 1f, back);

            // Equal power across the whole rotation, so orbiting the car does not pump.
            float norm = Mathf.Sqrt(e * e + x * x);
            if (norm > 1e-5f) { e /= norm; x /= norm; }

            const float flat = 0.70710678f;
            e = Mathf.Lerp(flat, e, Mathf.Clamp01(directionalAmount));
            x = Mathf.Lerp(flat, x, Mathf.Clamp01(directionalAmount));

            // The extra layer on top: a fixed amount of both sides, always audible.
            e = (e + bothLayerLevel) * engineLevel;
            x = (x + bothLayerLevel) * exhaustLevel;

            bool hasEngine = LayerHasAudio(RevEngineCore.EngineUp) || LayerHasAudio(RevEngineCore.EngineDown);
            bool hasExhaust = LayerHasAudio(RevEngineCore.ExhaustUp) || LayerHasAudio(RevEngineCore.ExhaustDown);
            if (!hasExhaust && hasEngine) { e += x; x = 0f; }
            else if (!hasEngine && hasExhaust) { x += e; e = 0f; }

            engineGain = e;
            exhaustGain = x;
        }

        void PublishParams()
        {
            if (!core.ready || window == null || modRuntime == null) return;
            paramCursor++;
            if (paramCursor >= paramBuffers.Length) paramCursor = 0;
            RevParams p = paramBuffers[paramCursor];
            if (p == null) { p = new RevParams(); paramBuffers[paramCursor] = p; }
            FillParams(p, rpm, throttle, load, ignition, frontness);
            core.Publish(p);
        }

        /// <summary>Copies the serialised settings into a parameter block for the DSP.</summary>
        public void FillParams(RevParams p, float rpmValue, float throttleValue, float loadValue,
                               float ignitionValue, float direction01)
        {
            p.rpm = rpmValue;
            p.throttle = Mathf.Clamp01(throttleValue);
            p.load = Mathf.Clamp01(loadValue);
            p.ignition = Mathf.Clamp01(ignitionValue);
            p.rpmIdle = rpmRange.x;
            p.rpmRedline = rpmRange.y;
            p.direction01 = Mathf.Clamp01(direction01);

            float engGain, exhGain;
            ComputeBusGains(direction01, out engGain, out exhGain);
            p.engineBusGain = engGain;
            p.exhaustBusGain = exhGain;
            p.masterGain = masterVolume;

            p.rpmSmoothMs = rpmSmoothMs;
            p.rpmSnapDelta = rpmSnapDelta;
            p.rpmRateSmoothMs = rpmRateSmoothMs;
            p.rpmRateReference = rpmRateReference;

            p.useThrottleForBlend = useThrottleForBlend;
            p.accelBlendMs = accelBlendMs;
            p.rpmRateThreshold = rpmRateThreshold;

            p.rateHz = grainRate;
            p.rateMinHz = grainRateRange.x;
            p.rateMaxHz = grainRateRange.y;
            p.rateFollowFiring = rpmGroupEnabled ? rateFollowsFiring : 0f;
            p.rateFiringMultiplier = rateFiringMultiplier;
            p.sizeMinMs = grainSizeRange.x;
            p.sizeMaxMs = grainSizeRange.y;
            p.sizeRpmFollow = sizeFollowsRpm;
            p.sizeRateFollow = sizeFollowsRpmRate;
            p.overlapMin = overlapRange.x;
            p.overlapMax = overlapRange.y;
            p.timingJitter = timingJitter;
            p.sizeJitter = sizeJitter;
            p.ampJitter = levelJitter;
            p.positionJitterMs = positionJitterMs;
            p.positionOffsetMs = positionOffsetMs;
            p.pitchJitterCents = pitchJitterCents;
            p.stereoSpread = stereoSpread;
            p.normaliseOverlap = normaliseOverlap;
            p.hermite = hermiteInterpolation;
            p.controlBlock = controlBlockSamples;
            p.maxGrains = maxGrains;

            p.antiPhasing = antiPhasing && rpmGroupEnabled;
            p.antiPhaseCycles = antiPhaseMaxCycles;
            p.quantiseGrainLength = quantiseGrainLength;
            p.layerDecorrelationMs = layerDecorrelationMs;

            p.rpmGroupEnabled = rpmGroupEnabled;
            p.cylinders = cylinders;
            p.rpmDifferenceResampling = rpmDifferenceResampling;
            p.maxResampleSemitones = maxResampleSemitones;

            p.dcBlock = dcBlock;
            p.hpEnabled = highpassEnabled;
            p.hpHz = highpassHz;
            p.hpQ = highpassQ;
            p.lpEnabled = lowpassEnabled;
            p.lpHz = lowpassHz;
            p.lpQ = lowpassQ;
            p.notchEnabled = notchEnabled;
            p.notchHz = notchHz;
            p.notchQ = notchQ;
            p.gateEnabled = gateEnabled;
            p.gateThresholdDb = gateThresholdDb;
            p.gateRangeDb = gateRangeDb;
            p.gateAttackMs = gateAttackMs;
            p.gateReleaseMs = gateReleaseMs;
            p.gateHoldMs = gateHoldMs;
            p.softClip = softClip;

            p.window = window;
            p.windowPower = windowPower;
            p.mod = modRuntime;
            p.lfo1 = ToLfoParams(lfo1);
            p.lfo2 = ToLfoParams(lfo2);

            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevSampleLayer s = layerSettings[i];
                RevLayerRuntime rt = layerRuntimes[i];
                p.layers[i].enabled = s != null && s.enabled && rt != null && rt.valid;
                p.layers[i].gain = s != null ? Mathf.Pow(10f, s.gainDb / 20f) : 0f;
                p.layers[i].pitchCents = s != null ? s.pitchTrimCents : 0f;
                p.layers[i].width = s != null ? s.stereoWidth : 1f;
            }
        }

        static RevLfoParams ToLfoParams(RevLfoSettings s)
        {
            RevLfoParams p = new RevLfoParams();
            if (s == null) { p.rateHz = 1f; p.firingMultiplier = 1f; return p; }
            p.shape = s.shape;
            p.rateHz = s.rateHz;
            p.syncToFiring = s.syncToFiringFrequency;
            p.firingMultiplier = s.firingMultiplier;
            return p;
        }

        // ------------------------------------------------------------------ audio thread
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (channels <= 0) return;
            core.Render(data, channels, data.Length / channels);
        }
    }
}

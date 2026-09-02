using UnityEngine;

/// <summary>
/// Real-time granular engine audio driven by a rev-up and a rev-down recording.
/// Instead of pitch-shifting a single loop, this scrubs through the actual
/// rev-up clip while accelerating and the rev-down clip while decelerating,
/// mapping current RPM to a read position in whichever clip is active.
/// Grains crossfade naturally when switching clips because old grains simply
/// finish their own fade-out (Hann window) while new grains spawn from the
/// other clip — no explicit crossfade logic needed.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class GranularEngineAudio : MonoBehaviour
{
    [Header("Source Clips")]
    [Tooltip("Recording of the engine revving up from idle to redline (or as far as you captured).")]
    public AudioClip revUpClip;
    [Tooltip("Recording of the engine coasting back down from high RPM to idle. If left empty, revUpClip is reused (read backwards).")]
    public AudioClip revDownClip;

    [Header("RPM -> clip position mapping (engine)")]
    [Tooltip("Position (0-1, fraction of clip length) that corresponds to rpmNormalized = 0 on the rev-up clip.")]
    [Range(0f, 1f)] public float revUpPosAtIdle = 0f;
    [Tooltip("Position (0-1) that corresponds to rpmNormalized = 1 on the rev-up clip.")]
    [Range(0f, 1f)] public float revUpPosAtRedline = 1f;
    [Tooltip("Position (0-1) that corresponds to rpmNormalized = 0 on the rev-down clip.")]
    [Range(0f, 1f)] public float revDownPosAtIdle = 1f;
    [Tooltip("Position (0-1) that corresponds to rpmNormalized = 1 on the rev-down clip.")]
    [Range(0f, 1f)] public float revDownPosAtRedline = 0f;

    [Header("Exhaust Source (optional second granular voice)")]
    [Tooltip("Recording of the exhaust revving up from idle to redline. Leave empty to disable the exhaust voice entirely (engine-only, same as before).")]
    public AudioClip exhaustRevUpClip;
    [Tooltip("Recording of the exhaust coasting back down from high RPM to idle. If left empty while exhaustRevUpClip is set, exhaustRevUpClip is reused (read backwards), same convention as the engine clips above.")]
    public AudioClip exhaustRevDownClip;
    [Range(0f, 1f)] public float exhaustRevUpPosAtIdle = 0f;
    [Range(0f, 1f)] public float exhaustRevUpPosAtRedline = 1f;
    [Range(0f, 1f)] public float exhaustRevDownPosAtIdle = 1f;
    [Range(0f, 1f)] public float exhaustRevDownPosAtRedline = 0f;

    [Header("Engine / Exhaust View Mix")]
    [Tooltip("0 = camera fully at the front (engine bay dominant), 1 = camera fully at the rear (exhaust dominant). Drive this from your camera rig, e.g. from the dot product of camera-forward and car-forward.")]
    [Range(0f, 1f)] public float cameraViewBlend = 0.5f;
    [Tooltip("Gain floor for whichever source is 'losing' at the current camera angle, so it's never fully inaudible even at the extreme front or rear view. 0.15-0.3 keeps a believable presence without washing out the dominant source.")]
    [Range(0f, 0.5f)] public float minSourceMix = 0.2f;
    private float currentEngineGain = 1f;
    private float currentExhaustGain = 1f;

    [Header("Engine State (drive these from your car controller)")]
    [Range(0f, 1f)] public float rpmNormalized = 0f;
    [Range(0f, 1f)] public float load = 0f; // throttle/load: affects grain density

    [Header("Accelerating / decelerating")]
    [Tooltip("If on, clip selection is a continuous crossfade driven by throttlePosition instead of the accelerating/decelerating auto-detection below. Recommended if you're feeding this from a real throttle input.")]
    public bool useThrottleBasedMix = false;
    [Tooltip("0 = fully rev-down clip, 1 = fully rev-up clip. Each new grain picks its clip with this as the probability of choosing rev-up, so the mix blends smoothly over many grains.")]
    [Range(0f, 1f)] public float throttlePosition = 0f;
    [Tooltip("Multiplies throttlePosition before it's used for clip selection. Drop this toward 0 to force rev-down grains — e.g. for a rev-limiter/ignition-cut effect — without touching the actual throttle value.")]
    [Range(0f, 1f)] public float cdiIgnitionMultiplier = 1f;
    [Tooltip("If off, acceleration state is detected automatically from the change in rpmNormalized over time.")]
    public bool manualAccelerationControl = false;
    [Tooltip("Only used when manualAccelerationControl is on.")]
    public bool acceleratingOverride = true;
    [Tooltip("How fast rpmNormalized must be rising/falling per second before auto-detection commits to a direction. Prevents flicker near-constant RPM.")]
    public float autoDetectSensitivity = 0.05f;
    [Tooltip("How long (seconds) it takes clip selection to fully cross over from rev-down to rev-up (or back) after the detected direction flips. Grains are chosen probabilistically during that window — same mechanism as the throttle-based mix above — so it fades rather than snapping instantly on the frame the direction changes. Used directly only when adaptCrossfadeToRpm is off.")]
    [Range(0.02f, 2f)] public float crossfadeSeconds = 0.25f;
    [Tooltip("If on, the crossfade duration starts from crossfadeSecondsWhenSteady and is scaled down while RPM is changing quickly (see crossfadeRateMulWhenChangingFast, same rate-of-change measure used for grain adaptation) — a fast rev or lift-off crosses over quicker than a held RPM does, regardless of where in the rev range it happens. Off uses the fixed crossfadeSeconds above instead.")]
    public bool adaptCrossfadeToRpm = true;
    [Tooltip("Crossfade duration used while RPM is essentially steady (rate of change near 0), before the rate-based scaling below is applied, when adaptCrossfadeToRpm is on.")]
    [Range(0.02f, 2f)] public float crossfadeSecondsWhenSteady = 0.25f;
    [Tooltip("Multiplies crossfadeSecondsWhenSteady by this once RPM is changing at rpmRateForMaxGrainAdapt or faster (same rate-of-change measure used for grain adaptation) — a fast rev or lift-off crosses over quicker than a held RPM does. 1 = no extra effect from rate.")]
    [Range(0.1f, 1f)] public float crossfadeRateMulWhenChangingFast = 0.5f;
    [Tooltip("Smooths the measured RPM rate of change used to auto-drive grain pitch drift below. Higher = tracks changes faster/noisier, lower = smoother but laggier.")]
    [Range(0.5f, 20f)] public float rpmRateSmoothing = 6f;
    private bool accelerating = true;
    private float previousRpm;
    private float rpmRateSmoothed; // signed, smoothed d(rpmNormalized)/dt
    private float rpmRateStage1;   // first-stage smoothing pass, feeds rpmRateSmoothed (see Update) - not read anywhere else
    private float mixBlend = 1f;   // 0 = fully rev-down clip, 1 = fully rev-up clip; eases toward accelerating's target
    private float currentPitchMul = 1f; // shared fine-tune pitch trajectory the whole grain stream glides along

    [Header("Grain Settings")]
    [Tooltip("Grain length in seconds. Larger = smoother but blurs fast-changing detail; smaller = crisper but needs more overlap to avoid a robotic/gappy sound. On tonal content like an engine recording, longer grains at high overlap make the near-duplicate-copies comb-filter ('tube') artifact worse — if you're fighting that sound, try shrinking this toward 0.08-0.15 before reaching for other fixes. Used directly only when adaptGrainToRpmRate is off.")]
    [Range(0.02f, 1f)] public float grainSizeSeconds = 0.15f;
    [Tooltip("If on, grain length AND grain spawn rate are driven by how fast RPM is currently changing (|d(rpmNormalized)/dt|) rather than by the RPM value itself. This is the more physically sensible mapping: a held, steady RPM (even at redline) doesn't need small grains — nothing is moving in the mapping — but a fast rev or a fast lift-off does, regardless of what RPM it happens at.")]
    public bool adaptGrainToRpmRate = true;
    [Tooltip("Grain length used while RPM is essentially steady (rate of change near 0) when adaptGrainToRpmRate is on. Longer/smoother is fine here since there's little to track.")]
    [Range(0.02f, 1f)] public float grainSizeWhenSteady = 0.22f;
    [Tooltip("Grain length used once RPM is changing at rpmRateForMaxGrainAdapt or faster, when adaptGrainToRpmRate is on. Shorter grains track a fast rev or lift-off without smearing.")]
    [Range(0.02f, 1f)] public float grainSizeWhenChangingFast = 0.09f;
    [Tooltip("Grain spawn rate multiplier (on top of the load-based rate below) while RPM is steady, when adaptGrainToRpmRate is on. 1 = no change.")]
    [Range(0.25f, 2f)] public float grainRateMulWhenSteady = 1f;
    [Tooltip("Grain spawn rate multiplier while RPM is changing at rpmRateForMaxGrainAdapt or faster, when adaptGrainToRpmRate is on. Shorter grains alone would gap out during fast revs without also spawning them more often; this compensates. Keep it modest — see maxOverlapGrains below, which caps the combined effect so this can't push overlap into broadband-noise territory.")]
    [Range(0.5f, 2.5f)] public float grainRateMulWhenChangingFast = 1.3f;
    [Tooltip("RPM rate of change (in rpmNormalized/second) at which the grain size/rate adaptation above reaches its full ('changing fast') effect. Lower = reaches full effect on even gentle throttle blips; higher = only hard revs/lift-offs do.")]
    public float rpmRateForMaxGrainAdapt = 1.2f;
    [Tooltip("Hard cap on estimated grain overlap (grain rate x grain length), regardless of load or the rate-adaptation above. Dense, heavily-jittered grain overlap beyond about 8-10 stops sounding like a richer engine and starts sounding like broadband noise — this is the actual mechanism behind that 'white noise' character, so capping it is the fix, not just a safety net.")]
    [Range(2f, 20f)] public float maxOverlapGrains = 9f;
    private float lastEffectiveGrainSize = 0.15f;
    private float currentGrainRateMul = 1f;
    [Tooltip("New grains spawned per second at load = 0")]
    public float minGrainRate = 60f;
    [Tooltip("New grains spawned per second at load = 1")]
    public float maxGrainRate = 260f;
    [Tooltip("Bounds of a single continuous fine-tune pitch trajectory shared by the whole grain stream (not redrawn per grain anymore). A new grain starts wherever this trajectory currently sits and glides toward where it's predicted to be when the grain ends — that's what makes consecutive grains connect instead of each jumping to an unrelated random pitch.")]
    public Vector2 fineTunePitchRange = new Vector2(0.95f, 1.05f);
    [Tooltip("Tiny extra multiplier applied per grain on top of the shared trajectory, for decorrelation between overlapping grains (helps the comb-filter/'tube' issue). Applied equally to a grain's start and end so it doesn't disturb the glide/handoff, just offsets it slightly.")]
    [Range(0f, 0.05f)] public float pitchJitter = 0.01f;
    [Tooltip("How fast the shared pitch trajectory wanders on its own per second, independent of RPM (organic texture, mean-reverting within fineTunePitchRange).")]
    [Range(0f, 0.05f)] public float pitchWanderStep = 0.01f;
    [Tooltip("How strongly the trajectory leans within fineTunePitchRange while RPM is changing — leans up while revving up, down while revving down, scaled by how fast RPM is changing, saturating at rpmRateForMaxDrift. Expressed as 'drift per grainSizeSeconds' for tuning consistency with grain length.")]
    [Range(0f, 0.2f)] public float grainPitchDriftMax = 0.06f;
    [Tooltip("The RPM rate of change (in rpmNormalized/second) at which the pitch trajectory's lean reaches grainPitchDriftMax. Lower = leans fully sooner (even a gentle throttle blip); higher = only hard revs get the full lean.")]
    public float rpmRateForMaxDrift = 1.5f;
    [Tooltip("Local jitter of each grain's read start, as a fraction of the CURRENT grain length (not a fixed time). Overlapping grains that read almost the same position are the main cause of the 'tube'/comb-filter sound on tonal content — this needs to be a meaningful fraction to actually decorrelate them, but keeping it a fraction (rather than a fixed seconds value) is what stops it from silently ballooning into a huge fraction of the grain — and effectively random, noise-like reads — whenever grain length shrinks (e.g. during the fast-RPM-change adaptation above). Default 0.4 matches the previous fixed 0.06s at the previous default 0.15s grain.")]
    [Range(0f, 0.6f)] public float positionJitterFraction = 0.4f;
    [Tooltip("Snaps each grain's position jitter to the nearest whole multiple of the clip's local engine-cycle period (found offline in LoadClip, see AnalyzeCyclePeriods) instead of leaving it at an arbitrary fractional offset. Overlapping grains that land a whole cycle apart reinforce the fundamental instead of phase-cancelling it — this is what actually prevents comb-filtering, the same principle behind Crankcase REV's 'engine cycle tracking', rather than just scattering the artifact around with more randomness. Off falls back to plain random jitter.")]
    public bool useCycleAlignedJitter = true;
    [Tooltip("Randomizes the timing between grain spawns. If this is 0, grains spawn at perfectly even intervals, which causes a comb-filtered/'tube' resonance from the regularly overlapping windows. 0.15-0.3 usually clears it up.")]
    [Range(0f, 0.5f)] public float spawnTimingJitter = 0.2f;
    [Tooltip("Randomizes each grain's length by up to this fraction. Helps further break up periodic comb-filtering.")]
    [Range(0f, 0.3f)] public float grainLengthJitter = 0.15f;
    [Tooltip("Randomizes each grain's amplitude by up to this fraction. Small amounts help break phase coherence between overlapping grains.")]
    [Range(0f, 0.3f)] public float grainAmplitudeJitter = 0.1f;
    [Tooltip("Overall max width of the stereo placement (0 = dead center/mono, 1 = full L-R spread). This is the biggest lever against the 'metallic tube' sound: with many grains overlapping and reading nearly the same spot in the clip, they comb-filter each other — spreading them across the stereo field stops those near-duplicates from summing coherently in one channel. Needs channels >= 2 to have any effect.")]
    [Range(0f, 1f)] public float stereoSpread = 0.6f;
    [Tooltip("How far the stereo image can wander per spawned grain (a slow, mean-reverting random walk). Grains near each other in time land near each other in the stereo field instead of each jumping to a fresh random pan — that independent-per-grain randomness is what reads as constant left-right bouncing at high grain rates.")]
    [Range(0f, 0.2f)] public float panWanderStep = 0.03f;
    [Tooltip("Small extra random offset applied on top of the wandering pan center, per grain, for local decorrelation without big jumps.")]
    [Range(0f, 0.5f)] public float panLocalJitter = 0.2f;

    [Header("Output")]
    [Range(0f, 2f)] public float masterGain = 1f;
    [Tooltip("Divides the summed grain output by sqrt(overlap count) so loudness stays roughly constant as grain density changes (e.g. with load). Without this, denser grain overlap = louder output as a side effect, independent of any loudness curve you're designing on purpose.")]
    public bool normalizeForOverlap = true;
    [Tooltip("Soft-clips the final sample so occasional constructive overlap of jittered-amplitude grains can't hard-clip into harsh digital distortion.")]
    public bool softClipOutput = true;

    [Header("Denoise (post-processing on the final mix)")]
    // This is a band-aid over broadband hiss that's already in the signal, not a
    // fix for its source — grain overlap/jitter tuning is the real fix, and this
    // works alongside that rather than instead of it. It runs on the fully mixed
    // output, after grains and view-mix gain, before the soft clipper.
    [Tooltip("Gentle low-pass filter that rolls off very high frequencies, where broadband hiss concentrates and where an engine/exhaust recording usually has little real content. Lowering the cutoff trades away top-end brightness for less hiss.")]
    public bool denoiseLowpassEnabled = true;
    [Tooltip("Frequencies above this are progressively attenuated. 9-12 kHz is usually inaudible-loss on engine content but takes a real bite out of hiss; drop toward 5-7 kHz if hiss is still audible, at the cost of a duller top end.")]
    [Range(1000f, 20000f)] public float denoiseLowpassCutoffHz = 9000f;
    [Tooltip("Downward-expander style noise gate: attenuates the signal (toward denoiseGateFloor, never fully muting) when the frame's peak level sits in/below the threshold band. Targets the residual noise floor that sits between grain bursts, without needing to touch tonal content once a grain cluster is actually sounding.")]
    public bool denoiseGateEnabled = true;
    [Tooltip("Below this peak level, the gate applies its full attenuation (down to denoiseGateFloor).")]
    [Range(0f, 0.2f)] public float denoiseGateThresholdLow = 0.01f;
    [Tooltip("Above this peak level, the gate applies no attenuation (gain = 1). Between the low and high thresholds it fades smoothly — this soft knee is what keeps it from sounding like it's chattering on and off.")]
    [Range(0f, 0.3f)] public float denoiseGateThresholdHigh = 0.05f;
    [Tooltip("Minimum gain the gate can apply even fully below threshold. Keep this above 0 (0.25-0.4) so quiet moments duck rather than go dead silent, which would sound like gating rather than denoising.")]
    [Range(0f, 1f)] public float denoiseGateFloor = 0.35f;
    [Tooltip("How quickly the gate opens (returns to gain 1) once level rises above threshold. Fast so it doesn't chop the front of a rev.")]
    [Range(0.001f, 0.2f)] public float denoiseGateAttackSeconds = 0.01f;
    [Tooltip("How quickly the gate closes (attenuates toward denoiseGateFloor) once level falls below threshold. Slower than attack so it doesn't pump/pulse on every small dip.")]
    [Range(0.01f, 1f)] public float denoiseGateReleaseSeconds = 0.15f;
    private float[] denoiseRaw;      // scratch buffer for the pre-filter per-channel signal, avoids per-frame GC
    private float[] denoiseLpState;  // one-pole low-pass filter state, per channel
    private float denoiseGateGain = 1f;

    // Local engine-cycle period (in samples), sampled at fixed intervals across
    // a clip and estimated via autocorrelation once at load time (see
    // AnalyzeCyclePeriods). This is the same idea behind Crankcase REV's
    // "engine cycle tracking" tool: know where a recording's actual cycle
    // boundaries are so grains can be aligned to them, rather than guessing
    // with pure random position jitter and hoping it decorrelates enough.
    private struct PeriodTable
    {
        public float[] periodsSamples;
        public int clipTotalFrames;
    }

    // --- internal per-clip sample data ---
    private struct ClipData
    {
        public float[] samples;
        public int channels;
        public int sampleRate;
        public int totalFrames;
        public bool valid;
        public PeriodTable periods;
    }
    private ClipData revUp;
    private ClipData revDown;
    private ClipData exhaustRevUp;
    private ClipData exhaustRevDown;
    private bool exhaustEnabled;
    private int mixSampleRate;

    private struct Grain
    {
        public bool useRevUp;
        public bool isExhaust;
        public double readPos;
        public double playbackRate;
        public int lengthSamples;
        public int age;
        public float gain;
        public float pan; // -1 (left) .. 1 (right)
        public float pitchMulNow;  // current within-grain pitch multiplier (applied on top of playbackRate)
        public float pitchMulStep; // added to pitchMulNow each sample
    }

    // Fixed-capacity pool instead of a List<Grain> of heap-allocated objects.
    // OnAudioFilterRead runs on Unity's audio thread; allocating there risks a
    // GC pause landing inside the callback, which is an audible glitch, not
    // just a performance number. Grain is now a struct stored directly in
    // this array, so spawning and removing a grain touches zero managed heap.
    // 128 is a generous ceiling — maxOverlapGrains already keeps steady-state
    // active count far below this; it only guards the rare transient spike.
    private const int MaxGrains = 128;
    private readonly Grain[] grainPool = new Grain[MaxGrains];
    private int activeGrainCount;
    private double grainSpawnAccumulator;
    private double nextSpawnThreshold = 1.0;
    private readonly System.Random rng = new System.Random();
    private float panWalk = 0f; // slowly wandering stereo center, see panWanderStep

    // --- exposed for the visualizer / debugging ---
    public int ActiveGrainCount => activeGrainCount;
    public float LastOutputRMS { get; private set; }
    public float CurrentGrainRate { get; private set; }
    public float CurrentPitch { get; private set; }
    public bool IsAccelerating => accelerating;
    public float CurrentRpmRate => rpmRateSmoothed;
    public float CurrentMixBlend => mixBlend;
    /// Roughly how many grains overlap at once. Below ~2-3 it will sound gappy/robotic.
    public float ApproxOverlap => CurrentGrainRate * lastEffectiveGrainSize * (exhaustEnabled ? 2f : 1f);

    void Start()
    {
        mixSampleRate = AudioSettings.outputSampleRate;
        revUp = LoadClip(revUpClip);
        revDown = revDownClip != null ? LoadClip(revDownClip) : revUp;

        exhaustEnabled = exhaustRevUpClip != null;
        if (exhaustEnabled)
        {
            exhaustRevUp = LoadClip(exhaustRevUpClip);
            exhaustRevDown = exhaustRevDownClip != null ? LoadClip(exhaustRevDownClip) : exhaustRevUp;
        }

        previousRpm = rpmNormalized;
        currentPitchMul = (fineTunePitchRange.x + fineTunePitchRange.y) * 0.5f;
    }

    ClipData LoadClip(AudioClip clip)
    {
        var data = new ClipData();
        if (clip == null) return data;

        data.channels = clip.channels;
        data.sampleRate = clip.frequency;
        data.samples = new float[clip.samples * clip.channels];
        clip.GetData(data.samples, 0);
        data.totalFrames = clip.samples;
        data.valid = true;
        data.periods = AnalyzeCyclePeriods(data);
        return data;
    }

    // --- offline engine-cycle analysis (runs once per clip, in LoadClip) ---

    // How many points across the clip to estimate a local period for.
    // Consecutive points are close enough together (a multi-second rev sweep
    // / this = tens of ms apart) that the true period can't have moved far
    // between them, which LookupPeriod's interpolation and the proximity
    // bias below both lean on.
    const int PeriodTableResolution = 200;
    // Plausible engine fundamental frequency band to search within. Covers a
    // low idle rumble up to a screaming high-RPM small-displacement engine.
    // Widen this if a specific recording's idle or redline stops tracking.
    const float MinEngineFreqHz = 25f;
    const float MaxEngineFreqHz = 400f;

    PeriodTable AnalyzeCyclePeriods(ClipData clip)
    {
        var table = new PeriodTable { periodsSamples = new float[PeriodTableResolution], clipTotalFrames = clip.totalFrames };
        if (!clip.valid || clip.totalFrames < 4) return table;

        int minLag = Mathf.Max(1, Mathf.FloorToInt(clip.sampleRate / MaxEngineFreqHz));
        int maxLag = Mathf.Max(minLag + 1, Mathf.CeilToInt(clip.sampleRate / MinEngineFreqHz));
        int windowSize = maxLag * 3; // enough span to see several cycles at even the lowest frequency searched

        float lastGoodPeriod = (minLag + maxLag) * 0.5f;

        for (int i = 0; i < PeriodTableResolution; i++)
        {
            float frac = PeriodTableResolution <= 1 ? 0f : (float)i / (PeriodTableResolution - 1);
            int center = Mathf.RoundToInt(frac * (clip.totalFrames - 1));
            int windowStart = Mathf.Clamp(center - windowSize / 2, 0, Mathf.Max(0, clip.totalFrames - windowSize));
            int available = Mathf.Min(windowSize, clip.totalFrames - windowStart);

            lastGoodPeriod = EstimatePeriodAutocorrelation(clip, windowStart, available, minLag, maxLag, lastGoodPeriod);
            table.periodsSamples[i] = lastGoodPeriod;
        }

        return table;
    }

    // Normalized autocorrelation over [minLag, maxLag]; returns the lag with
    // the strongest periodicity, i.e. the local cycle length in samples.
    // This is a one-time cost paid in LoadClip (at Start(), or your own
    // preload point) — not per-frame — but it's still real work: a few
    // seconds of stereo audio at 48kHz can take a noticeable moment to
    // analyze. If that shows up as a load-time hitch, the fix is to move
    // this call off the main thread (a background Task/Job before gameplay
    // starts) or run it once in the editor and cache periodsSamples to an
    // asset instead of recomputing it every time the clip loads.
    float EstimatePeriodAutocorrelation(ClipData clip, int windowStart, int windowLength, int minLag, int maxLag, float lastGoodPeriod)
    {
        if (windowLength <= maxLag) return lastGoodPeriod;

        float bestScore = float.NegativeInfinity;
        int bestLag = Mathf.RoundToInt(lastGoodPeriod);

        const int lagStep = 2; // brute-force every lag is unnecessary for a smooth period curve; skipping some keeps analysis time reasonable
        for (int lag = minLag; lag <= maxLag; lag += lagStep)
        {
            int compareLength = windowLength - lag;
            if (compareLength <= 0) continue;

            double sum = 0.0, energyA = 0.0, energyB = 0.0;
            int step = Mathf.Max(1, compareLength / 512); // sparse sampling within the window is plenty for a correlation estimate
            int samples = 0;
            for (int s = 0; s < compareLength; s += step)
            {
                float a = SourceFrame(clip, windowStart + s);
                float b = SourceFrame(clip, windowStart + s + lag);
                sum += a * b;
                energyA += a * a;
                energyB += b * b;
                samples++;
            }
            if (samples == 0) continue;

            double denom = System.Math.Sqrt(energyA * energyB);
            float score = denom > 1e-9 ? (float)(sum / denom) : 0f;

            // Small bias toward lags near the previous window's period, so an
            // equally-scoring octave/subharmonic doesn't flip the estimate
            // between adjacent analysis points that should track smoothly.
            float proximityBias = 1f - Mathf.Clamp01(Mathf.Abs(lag - lastGoodPeriod) / (maxLag - minLag)) * 0.15f;
            score *= proximityBias;

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag;
    }

    // Interpolated lookup into a clip's period table at a fractional clip
    // position (in samples). Returns 0 if no table is available, which
    // callers treat as "fall back to plain jitter".
    static float LookupPeriod(PeriodTable table, double clipFrame)
    {
        if (table.periodsSamples == null || table.periodsSamples.Length == 0 || table.clipTotalFrames <= 1)
            return 0f;
        float frac = Mathf.Clamp01((float)(clipFrame / (table.clipTotalFrames - 1)));
        float idxF = frac * (table.periodsSamples.Length - 1);
        int i0 = Mathf.FloorToInt(idxF);
        int i1 = Mathf.Min(i0 + 1, table.periodsSamples.Length - 1);
        float t = idxF - i0;
        return Mathf.Lerp(table.periodsSamples[i0], table.periodsSamples[i1], t);
    }

    void Update()
    {
        // Track the raw rate unconditionally (used to auto-drive grain pitch
        // drift below) even when manualAccelerationControl overrides clip
        // selection itself — the two are independent concerns.
        float delta = (rpmNormalized - previousRpm) / Mathf.Max(Time.deltaTime, 0.0001f);
        float rateAlpha = 1f - Mathf.Exp(-rpmRateSmoothing * Time.deltaTime);
        // Two cascaded one-pole passes instead of one. A single pass only rolls
        // off at 20dB/decade above its cutoff, which isn't enough to reject a
        // real oscillation like rev-limiter bounce - RPM genuinely swinging up
        // and down fast still leaks through as a large, sign-flipping value,
        // which everything downstream (grain size/rate adapt, crossfade rate
        // multiplier, pitch drift) reads as "changing fast" even though the
        // limiter is holding a steady average RPM. Cascading the same filter
        // twice gives ~40dB/decade rolloff, which kills that oscillation far
        // more effectively while barely slowing the response to a genuine
        // sustained rev or lift-off (a ramp/step, not an oscillation).
        rpmRateStage1 = Mathf.Lerp(rpmRateStage1, delta, rateAlpha);
        rpmRateSmoothed = Mathf.Lerp(rpmRateSmoothed, rpmRateStage1, rateAlpha);
        previousRpm = rpmNormalized;

        if (!manualAccelerationControl)
        {
            if (delta > autoDetectSensitivity) accelerating = true;
            else if (delta < -autoDetectSensitivity) accelerating = false;
            // else: keep previous state (avoids flicker when RPM is roughly flat)
        }
        else
        {
            accelerating = acceleratingOverride;
        }

        // Ease mixBlend toward accelerating's target over the (optionally adaptive)
        // crossfade duration instead of snapping the instant the direction flips —
        // this is what actually crossfades grain selection between clips (see SpawnGrain).
        // The duration starts from a single steady-state base and is scaled down
        // by how fast RPM is moving right now (same rate measure as the grain
        // adaptation above) — a fast rev crosses over quicker than a held RPM
        // does, wherever in the rev range it happens.
        float effectiveCrossfadeSeconds;
        if (adaptCrossfadeToRpm)
        {
            float rateFrac = Mathf.Clamp01(Mathf.Abs(rpmRateSmoothed) / Mathf.Max(0.0001f, rpmRateForMaxGrainAdapt));
            float rateMul = Mathf.Lerp(1f, crossfadeRateMulWhenChangingFast, rateFrac);
            effectiveCrossfadeSeconds = crossfadeSecondsWhenSteady * rateMul;
        }
        else
        {
            effectiveCrossfadeSeconds = crossfadeSeconds;
        }
        float mixTarget = accelerating ? 1f : 0f;
        mixBlend = Mathf.MoveTowards(mixBlend, mixTarget, Time.deltaTime / Mathf.Max(0.02f, effectiveCrossfadeSeconds));

        // Engine/exhaust view mix: a straight lerp between cameraViewBlend's two
        // extremes, clamped so neither source ever drops below minSourceMix —
        // both stay audible no matter how far front/rear the camera sits.
        currentEngineGain = Mathf.Lerp(1f, minSourceMix, cameraViewBlend);
        currentExhaustGain = Mathf.Lerp(minSourceMix, 1f, cameraViewBlend);

        // Advance the one shared pitch trajectory every grain reads its start
        // from. grainPitchDriftMax/rpmRateForMaxDrift set how hard it leans
        // with the revs; pitchWanderStep adds a little organic motion on top.
        // Using UnityEngine.Random here (not the audio-thread `rng`) since this
        // runs on the main thread.
        // Use the same effective grain size SpawnGrainForSource uses to predict
        // a grain's end pitch (lastEffectiveGrainSize - adaptive when
        // adaptGrainToRpmRate is on, otherwise just grainSizeSeconds). If this
        // used the fixed grainSizeSeconds instead, the trajectory's actual
        // per-second drift here would disagree with the per-second drift a
        // grain assumes when gliding toward its predicted end, and the two
        // would fall out of sync - worst exactly when RPM is changing fast,
        // since that's when the adaptive grain size diverges most from the
        // fixed one. Keeping both reads in lockstep is what makes a new grain's
        // start line up with the previous grain's predicted end.
        float driftRateFrac = Mathf.Clamp(rpmRateSmoothed / Mathf.Max(0.0001f, rpmRateForMaxDrift), -1f, 1f);
        float pitchDriftPerSecond = driftRateFrac * grainPitchDriftMax / Mathf.Max(0.01f, lastEffectiveGrainSize);
        currentPitchMul += pitchDriftPerSecond * Time.deltaTime;
        currentPitchMul += UnityEngine.Random.Range(-1f, 1f) * pitchWanderStep * Mathf.Sqrt(Mathf.Max(Time.deltaTime, 0.0001f));
        currentPitchMul = Mathf.Clamp(currentPitchMul, fineTunePitchRange.x, fineTunePitchRange.y);
    }

    float GrainRateForState() => Mathf.Lerp(minGrainRate, maxGrainRate, load) * currentGrainRateMul;

    // Computes grain size and the grain-rate multiplier for this audio buffer,
    // based on how fast RPM is currently changing rather than the RPM value
    // itself — a held RPM (even at redline) needs no special treatment, but a
    // fast rev or lift-off does, wherever it happens in the rev range. Called
    // once per OnAudioFilterRead so both the engine and exhaust grains spawned
    // in the same tick use consistent values, and so GrainRateForState (which
    // depends on the rate multiplier) sees up-to-date numbers.
    void UpdateAdaptiveGrainParams()
    {
        if (!adaptGrainToRpmRate)
        {
            lastEffectiveGrainSize = grainSizeSeconds;
            currentGrainRateMul = 1f;
            return;
        }

        float rateFrac = Mathf.Clamp01(Mathf.Abs(rpmRateSmoothed) / Mathf.Max(0.0001f, rpmRateForMaxGrainAdapt));
        lastEffectiveGrainSize = Mathf.Lerp(grainSizeWhenSteady, grainSizeWhenChangingFast, rateFrac);
        currentGrainRateMul = Mathf.Lerp(grainRateMulWhenSteady, grainRateMulWhenChangingFast, rateFrac);
    }

    // Spawns one grain from the engine voice and, if an exhaust clip is assigned,
    // one grain from the exhaust voice at the same moment. Both voices share the
    // same RPM, load and pitch-trajectory state — only their clips, position
    // mapping, and the view-based gain applied at mix time (see OnAudioFilterRead)
    // differ. This is what keeps both sources continuously present instead of
    // switching between them.
    void SpawnGrain()
    {
        SpawnGrainForSource(isExhaust: false);
        if (exhaustEnabled) SpawnGrainForSource(isExhaust: true);
    }

    void SpawnGrainForSource(bool isExhaust)
    {
        ClipData sourceRevUp = isExhaust ? exhaustRevUp : revUp;
        ClipData sourceRevDown = isExhaust ? exhaustRevDown : revDown;

        bool useRevUp;
        if (useThrottleBasedMix)
        {
            float effectiveThrottle = Mathf.Clamp01(throttlePosition * cdiIgnitionMultiplier);
            useRevUp = sourceRevDown.valid ? rng.NextDouble() < effectiveThrottle : true;
        }
        else
        {
            // mixBlend eases from 0 to 1 (or back) over the crossfade duration
            // whenever the detected accel direction flips, instead of an instant
            // switch — each grain independently rolls against it, same technique
            // as the throttle-based path above, so the crossfade is grain-
            // probability based rather than a hard cut.
            useRevUp = sourceRevDown.valid ? rng.NextDouble() < mixBlend : true;
        }

        ClipData clip = useRevUp ? sourceRevUp : sourceRevDown;
        if (!clip.valid || clip.totalFrames <= 1) return;

        float posAtIdle = isExhaust
            ? (useRevUp ? exhaustRevUpPosAtIdle : exhaustRevDownPosAtIdle)
            : (useRevUp ? revUpPosAtIdle : revDownPosAtIdle);
        float posAtRedline = isExhaust
            ? (useRevUp ? exhaustRevUpPosAtRedline : exhaustRevDownPosAtRedline)
            : (useRevUp ? revUpPosAtRedline : revDownPosAtRedline);
        float targetFrac = Mathf.Lerp(posAtIdle, posAtRedline, rpmNormalized);

        double centerFrame = targetFrac * clip.totalFrames;
        double jitterFrames = positionJitterFraction * lastEffectiveGrainSize * clip.sampleRate;
        double rawJitter = (rng.NextDouble() * 2.0 - 1.0) * jitterFrames;

        // Snap the jitter offset to the nearest whole multiple of the clip's
        // local cycle period at this position (from the offline analysis in
        // LoadClip) instead of leaving it at an arbitrary fractional offset.
        // See useCycleAlignedJitter's tooltip for why that's the actual fix
        // for comb-filtering rather than just scattering it around.
        double snappedJitter = rawJitter;
        if (useCycleAlignedJitter)
        {
            float localPeriod = LookupPeriod(clip.periods, centerFrame);
            if (localPeriod >= 1f)
            {
                double periodCount = System.Math.Round(rawJitter / localPeriod);
                snappedJitter = periodCount * localPeriod;
            }
        }

        double start = centerFrame + snappedJitter;
        double maxStart = clip.totalFrames - 2.0; // leave room for interpolation to the next sample
        if (start < 0.0) start = 0.0;
        else if (start > maxStart) start = maxStart;

        // Grain length was already computed for this audio buffer in
        // UpdateAdaptiveGrainParams (based on RPM rate of change, or fixed if
        // adaptGrainToRpmRate is off) — reuse it here so engine and exhaust
        // grains spawned in the same tick agree.
        float effGrainSize = lastEffectiveGrainSize;

        int baseLength = Mathf.Max(1, (int)(effGrainSize * mixSampleRate));
        float lengthJitterMul = 1f + ((float)rng.NextDouble() * 2f - 1f) * grainLengthJitter;
        int lengthOutSamples = Mathf.Max(1, (int)(baseLength * lengthJitterMul));
        float grainDurationSeconds = lengthOutSamples / (float)mixSampleRate;

        // Start exactly where the shared trajectory currently sits (no more
        // independent random pick per grain — that's what made adjacent grains'
        // pitches unrelated to each other) and glide toward where that same
        // trajectory is predicted to be by the time THIS grain ends. Because the
        // next grain also reads its own start from the trajectory, its start
        // lines up with this grain's predicted end — that's the "connects
        // smoothly to the next" part.
        float driftRateFrac = Mathf.Clamp(rpmRateSmoothed / Mathf.Max(0.0001f, rpmRateForMaxDrift), -1f, 1f);
        float pitchDriftPerSecond = driftRateFrac * grainPitchDriftMax / Mathf.Max(0.01f, effGrainSize);
        float predictedEnd = Mathf.Clamp(currentPitchMul + pitchDriftPerSecond * grainDurationSeconds, fineTunePitchRange.x, fineTunePitchRange.y);

        // Tiny per-grain jitter for decorrelation (comb-filter mitigation), applied
        // equally to both ends so it offsets the whole glide without bending it.
        float jitterMul = 1f + ((float)rng.NextDouble() * 2f - 1f) * pitchJitter;
        float startPitch = currentPitchMul * jitterMul;
        float endPitch = predictedEnd * jitterMul;
        CurrentPitch = startPitch;

        float glideEndMul = endPitch / Mathf.Max(0.0001f, startPitch);
        float pitchMulStep = (glideEndMul - 1f) / Mathf.Max(1, lengthOutSamples - 1);

        float gain = 1f + ((float)rng.NextDouble() * 2f - 1f) * grainAmplitudeJitter;

        // Slowly wander the stereo center (mean-reverting toward 0) instead of an
        // independent random pan every grain — that's what caused the constant
        // left-right bouncing. Nearby-in-time grains now land near each other.
        panWalk = panWalk * 0.98f + (float)(rng.NextDouble() * 2.0 - 1.0) * panWanderStep;
        panWalk = Mathf.Clamp(panWalk, -1f, 1f);
        float panLocal = (float)(rng.NextDouble() * 2.0 - 1.0) * panLocalJitter;
        float pan = Mathf.Clamp(panWalk + panLocal, -1f, 1f) * stereoSpread;

        // Pool exhausted: drop this grain rather than grow the array (which
        // would allocate on the audio thread). Shouldn't happen in practice —
        // maxOverlapGrains caps steady-state count well below MaxGrains — so
        // this only trims the rare transient spike, silently and harmlessly.
        if (activeGrainCount >= MaxGrains) return;

        grainPool[activeGrainCount] = new Grain
        {
            useRevUp = useRevUp,
            isExhaust = isExhaust,
            readPos = start,
            playbackRate = (double)clip.sampleRate / mixSampleRate * startPitch,
            lengthSamples = lengthOutSamples,
            age = 0,
            pan = pan,
            pitchMulNow = 1f,
            pitchMulStep = pitchMulStep,
            gain = gain
        };
        activeGrainCount++;
    }

    // Swap-with-last removal: O(1) and keeps the pool contiguous (no gaps),
    // unlike List<T>.RemoveAt which shifts every following element down.
    void RemoveGrainAt(int index)
    {
        activeGrainCount--;
        grainPool[index] = grainPool[activeGrainCount];
    }

    static float Window(int age, int length)
    {
        if (length <= 1) return 1f;
        float t = (float)age / (length - 1);
        return 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * t);
    }

    private float[] channelAccum; // reused scratch buffer sized to the output channel count; avoids per-frame GC on the audio thread

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!revUp.valid && !revDown.valid) return;
        if (channelAccum == null || channelAccum.Length != channels) channelAccum = new float[channels];
        if (denoiseRaw == null || denoiseRaw.Length != channels) denoiseRaw = new float[channels];
        if (denoiseLpState == null || denoiseLpState.Length != channels) denoiseLpState = new float[channels];

        int framesInBuffer = data.Length / channels;

        UpdateAdaptiveGrainParams();

        float rate = GrainRateForState();
        // Cap combined overlap (rate x grain length x source count) regardless of
        // how high load or the fast-change multiplier push it — this is the main
        // fix for the "white noise" character: dense, heavily-jittered grain
        // overlap beyond ~8-10 stops reading as a richer engine and starts
        // reading as broadband hiss, since incoherent overlapping grains are
        // literally how noise generators are built.
        float sourceCountForOverlap = exhaustEnabled ? 2f : 1f;
        float maxRateForOverlap = maxOverlapGrains / Mathf.Max(0.01f, lastEffectiveGrainSize * sourceCountForOverlap);
        rate = Mathf.Min(rate, maxRateForOverlap);
        CurrentGrainRate = rate;
        grainSpawnAccumulator += rate * ((double)framesInBuffer / mixSampleRate);
        while (grainSpawnAccumulator >= nextSpawnThreshold)
        {
            SpawnGrain();
            grainSpawnAccumulator -= nextSpawnThreshold;
            nextSpawnThreshold = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * spawnTimingJitter;
        }

        double sumSquares = 0.0;

        // Incoherent (randomly phased) grains sum roughly like sqrt(N) in amplitude,
        // so without this, output loudness silently rises with grain density (load).
        float overlapNorm = normalizeForOverlap
            ? Mathf.Sqrt(Mathf.Max(1f, ApproxOverlap))
            : 1f;

        bool stereo = channels >= 2;

        // One-pole low-pass coefficient for the denoise stage — constant for the
        // whole buffer since the cutoff field doesn't change per-sample.
        float lpAlpha = denoiseLowpassEnabled
            ? 1f - Mathf.Exp(-2f * Mathf.PI * denoiseLowpassCutoffHz / mixSampleRate)
            : 1f;

        for (int frame = 0; frame < framesInBuffer; frame++)
        {
            System.Array.Clear(channelAccum, 0, channels);

            for (int gi = activeGrainCount - 1; gi >= 0; gi--)
            {
                // ref, not a copy: mutations to g.readPos/pitchMulNow/age below
                // need to land back in the pool slot, not a throwaway struct copy.
                ref Grain g = ref grainPool[gi];
                if (g.age >= g.lengthSamples)
                {
                    RemoveGrainAt(gi);
                    continue;
                }

                ClipData clip = g.isExhaust
                    ? (g.useRevUp ? exhaustRevUp : exhaustRevDown)
                    : (g.useRevUp ? revUp : revDown);
                int sIndex = (int)g.readPos;
                if (sIndex < 0 || sIndex >= clip.totalFrames - 1)
                {
                    RemoveGrainAt(gi);
                    continue;
                }

                // View-based mix: engine dominant toward the front, exhaust dominant
                // toward the rear, each floored at minSourceMix so neither disappears.
                float sourceGain = g.isExhaust ? currentExhaustGain : currentEngineGain;
                float s0 = SourceFrame(clip, sIndex);
                float s1 = SourceFrame(clip, sIndex + 1);
                float frac = (float)(g.readPos - sIndex);
                float voice = Mathf.Lerp(s0, s1, frac) * Window(g.age, g.lengthSamples) * g.gain * sourceGain;

                // Equal-power pan per grain. This is the key move against the "tube"
                // comb-filter artifact: many overlapping grains read near-identical
                // content at tiny delays, which comb-filters hard when it's all
                // summed into one mono signal. Spreading grains across the stereo
                // field stops those near-duplicates from reinforcing/cancelling
                // coherently in either ear.
                if (stereo)
                {
                    float panAngle = (g.pan * 0.5f + 0.5f) * (Mathf.PI * 0.5f);
                    channelAccum[0] += voice * Mathf.Cos(panAngle);
                    channelAccum[1] += voice * Mathf.Sin(panAngle);
                    for (int c = 2; c < channels; c++)
                        channelAccum[c] += voice * 0.7071f;
                }
                else
                {
                    channelAccum[0] += voice;
                }

                g.readPos += g.playbackRate * g.pitchMulNow;
                g.pitchMulNow += g.pitchMulStep;
                g.age++;
            }

            // Raw per-channel signal (normalized + master gain), and the frame's
            // peak level, which drives the noise gate below.
            float framePeak = 0f;
            for (int c = 0; c < channels; c++)
            {
                float raw = channelAccum[c] / overlapNorm * masterGain;
                denoiseRaw[c] = raw;
                float absRaw = Mathf.Abs(raw);
                if (absRaw > framePeak) framePeak = absRaw;
            }

            // Downward-expander gate: a single scalar gain shared across all
            // channels (so it doesn't disturb the stereo image), smoothly
            // attenuating toward denoiseGateFloor as the frame's peak level drops
            // through the threshold band, with separate attack/release speeds so
            // it opens fast but closes without pumping.
            float targetGateGain = 1f;
            if (denoiseGateEnabled)
            {
                float thresholdHighSafe = Mathf.Max(denoiseGateThresholdHigh, denoiseGateThresholdLow + 0.0001f);
                float t = Mathf.Clamp01(Mathf.InverseLerp(denoiseGateThresholdLow, thresholdHighSafe, framePeak));
                targetGateGain = Mathf.Lerp(denoiseGateFloor, 1f, Mathf.SmoothStep(0f, 1f, t));
            }
            float gateAlpha = targetGateGain > denoiseGateGain
                ? 1f - Mathf.Exp(-1f / (Mathf.Max(0.001f, denoiseGateAttackSeconds) * mixSampleRate))
                : 1f - Mathf.Exp(-1f / (Mathf.Max(0.001f, denoiseGateReleaseSeconds) * mixSampleRate));
            denoiseGateGain = Mathf.Lerp(denoiseGateGain, targetGateGain, gateAlpha);

            float frameSumSquares = 0f;
            for (int c = 0; c < channels; c++)
            {
                float v = denoiseRaw[c];
                if (denoiseLowpassEnabled)
                {
                    denoiseLpState[c] += lpAlpha * (v - denoiseLpState[c]);
                    v = denoiseLpState[c];
                }
                v *= denoiseGateGain;
                if (softClipOutput)
                    v = v / (1f + Mathf.Abs(v)); // cheap softsign-style limiter, no hard ceiling
                data[frame * channels + c] = v;
                frameSumSquares += v * v;
            }
            sumSquares += frameSumSquares / channels;
        }

        LastOutputRMS = Mathf.Sqrt((float)(sumSquares / Mathf.Max(1, framesInBuffer)));
    }

    float SourceFrame(ClipData clip, int frameIndex)
    {
        int baseIdx = frameIndex * clip.channels;
        float sum = 0f;
        for (int c = 0; c < clip.channels; c++)
            sum += clip.samples[baseIdx + c];
        return sum / clip.channels;
    }
}
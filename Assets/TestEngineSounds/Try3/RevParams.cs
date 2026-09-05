namespace RevAudio
{
    /// <summary>Per-layer values the audio thread needs. Filled on the main thread.</summary>
    public struct RevLayerParams
    {
        public bool enabled;
        public float gain;
        public float pitchCents;
        public float width;
    }

    /// <summary>Settings for one of the two LFOs.</summary>
    public struct RevLfoParams
    {
        public LfoShape shape;
        public float rateHz;
        public bool syncToFiring;        // rate follows the engine firing frequency instead of rateHz
        public float firingMultiplier;
    }

    /// <summary>
    /// Immutable snapshot of everything the DSP needs for one block. The component fills one
    /// of three rotating instances on the main thread and hands it over with a single atomic
    /// reference swap, so the audio thread never reads a half-written value and never blocks.
    /// </summary>
    public sealed class RevParams
    {
        // ---- live engine state ----
        public float rpm;
        public float throttle;
        public float load;
        public float ignition;
        public float rpmIdle;
        public float rpmRedline;

        // ---- bus levels (directional mix is resolved on the main thread) ----
        public float engineBusGain;
        public float exhaustBusGain;
        public float masterGain;
        public float direction01;

        // ---- rpm conditioning ----
        public float rpmSmoothMs;
        public float rpmSnapDelta;
        public float rpmRateSmoothMs;
        public float rpmRateReference;

        // ---- accel / decel crossfade ----
        public bool useThrottleForBlend;
        public float accelBlendMs;
        public float rpmRateThreshold;

        // ---- grain scheduling ----
        public float rateHz;
        public float rateMinHz, rateMaxHz;
        public float rateFollowFiring;
        public float rateFiringMultiplier;
        public float sizeMinMs, sizeMaxMs;
        public float sizeRpmFollow;
        public float sizeRateFollow;
        public float overlapMin, overlapMax;
        public float timingJitter;
        public float sizeJitter;
        public float ampJitter;
        public float positionJitterMs;
        public float positionOffsetMs;
        public float pitchJitterCents;
        public float stereoSpread;
        public bool normaliseOverlap;
        public bool hermite;
        public int controlBlock;
        public int maxGrains;

        // ---- anti-phasing ----
        public bool antiPhasing;
        public float antiPhaseCycles;
        public bool quantiseGrainLength;
        public float layerDecorrelationMs;

        // ---- RPM detection group ----
        public bool rpmGroupEnabled;
        public int cylinders;
        public bool rpmDifferenceResampling;
        public float maxResampleSemitones;

        // ---- filtering ----
        public bool dcBlock;
        public bool hpEnabled, lpEnabled, notchEnabled, gateEnabled;
        public float hpHz, hpQ;
        public float lpHz, lpQ;
        public float notchHz, notchQ;
        public float gateThresholdDb, gateRangeDb, gateAttackMs, gateReleaseMs, gateHoldMs;
        public bool softClip;

        // ---- resources ----
        public float[] window;
        public float windowPower;
        public RevModRuntime mod;
        public RevLfoParams lfo1, lfo2;
        public RevLayerParams[] layers = new RevLayerParams[RevEngineCore.LayerCount];
    }
}

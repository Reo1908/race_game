using System;
using System.Collections.Generic;
using UnityEngine;

namespace RevAudio
{
    public enum ModSource
    {
        None = 0,
        RpmNormalised,      // 0 at idle, 1 at redline
        RpmRate,            // signed rate of change, normalised by Rpm Rate Reference
        RpmRateAbs,         // magnitude only
        Throttle,
        Load,
        Ignition,
        Direction,          // 0 = listener behind the car, 1 = in front
        AccelDecel,         // 0 = fully on the decel samples, 1 = fully on the accel samples
        Lfo1,
        Lfo2,
        RandomPerGrain,     // new value for every single grain
        Constant            // always 1, use it to offset a target by a fixed amount
    }

    public enum ModTarget
    {
        None = 0,
        GrainRate,          // Hz
        GrainSize,          // ms, both ends of the range
        GrainSizeMin,       // ms
        GrainSizeMax,       // ms
        Pitch,              // cents
        PositionOffset,     // ms into the sample
        PositionJitter,     // ms
        TimingJitter,       // 0..1
        StereoSpread,       // 0..1
        Overlap,            // grains
        EngineLevel,        // linear
        ExhaustLevel,       // linear
        MasterLevel,        // linear
        LowpassCutoff,      // octaves
        HighpassCutoff,     // octaves
        NotchFreq,          // octaves
        GateThreshold,      // dB
        AccelDecelBlend,    // 0..1
        GrainLevel,         // linear, per grain
        GrainPan            // -1..1, per grain
    }

    public enum LfoShape { Sine, Triangle, SawUp, SawDown, Square, SampleHold, SmoothNoise }

    /// <summary>One row of the modulation matrix.</summary>
    [Serializable]
    public class RevModRoute
    {
        public bool enabled = true;
        public ModSource source = ModSource.None;
        public ModTarget target = ModTarget.None;
        [Tooltip("Amount in the target unit (Hz, ms, cents, octaves, dB or linear - see the target list).")]
        public float amount = 0f;
        [Tooltip("Remaps the source from 0..1 to -1..+1 before scaling, so the route can push both ways.")]
        public bool bipolar = false;
        [Tooltip("Response curve. x = source value, y = scaled amount.")]
        public AnimationCurve shape = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    }

    /// <summary>
    /// Flattened, allocation-free version of the matrix. Curves are baked into one packed
    /// LUT array on the main thread so the audio thread never touches an AnimationCurve
    /// (which is not thread safe) and never allocates.
    /// </summary>
    public sealed class RevModRuntime
    {
        public const int LutSize = 129;

        public struct Route
        {
            public int source;
            public int target;
            public float amount;
            public bool bipolar;
            public int lutOffset;
        }

        public Route[] control = new Route[0];     // evaluated once per control tick
        public Route[] perGrain = new Route[0];    // evaluated once per spawned grain
        public float[] luts = new float[0];

        public static readonly int SourceCount = Enum.GetValues(typeof(ModSource)).Length;
        public static readonly int TargetCount = Enum.GetValues(typeof(ModTarget)).Length;

        public static RevModRuntime Build(List<RevModRoute> routes)
        {
            var rt = new RevModRuntime();
            var control = new List<Route>();
            var perGrain = new List<Route>();
            var luts = new List<float>();

            if (routes != null)
            {
                for (int i = 0; i < routes.Count; i++)
                {
                    RevModRoute r = routes[i];
                    if (r == null || !r.enabled) continue;
                    if (r.source == ModSource.None || r.target == ModTarget.None) continue;
                    if (Mathf.Approximately(r.amount, 0f)) continue;

                    Route baked = new Route
                    {
                        source = (int)r.source,
                        target = (int)r.target,
                        amount = r.amount,
                        bipolar = r.bipolar,
                        lutOffset = luts.Count
                    };

                    for (int k = 0; k < LutSize; k++)
                    {
                        float x = (float)k / (LutSize - 1);
                        luts.Add(r.shape != null ? r.shape.Evaluate(x) : x);
                    }

                    if (r.source == ModSource.RandomPerGrain) perGrain.Add(baked);
                    else control.Add(baked);
                }
            }

            rt.control = control.ToArray();
            rt.perGrain = perGrain.ToArray();
            rt.luts = luts.ToArray();
            return rt;
        }

        float Shaped(ref Route r, float value01)
        {
            float x = Mathf.Clamp01(value01) * (LutSize - 1);
            int i = (int)x;
            if (i > LutSize - 2) i = LutSize - 2;
            float f = x - i;
            float a = luts[r.lutOffset + i];
            float s = a + (luts[r.lutOffset + i + 1] - a) * f;
            return r.bipolar ? s * 2f - 1f : s;
        }

        /// <summary>Accumulates every control-rate route into output, indexed by ModTarget.</summary>
        public void EvaluateControl(float[] sources, float[] output)
        {
            Array.Clear(output, 0, output.Length);
            for (int i = 0; i < control.Length; i++)
            {
                output[control[i].target] += Shaped(ref control[i], sources[control[i].source]) * control[i].amount;
            }
        }

        /// <summary>Per-grain routes only. random01 is one fresh random value for this grain.</summary>
        public float EvaluatePerGrain(ModTarget target, float random01)
        {
            float sum = 0f;
            int t = (int)target;
            for (int i = 0; i < perGrain.Length; i++)
            {
                if (perGrain[i].target != t) continue;
                sum += Shaped(ref perGrain[i], random01) * perGrain[i].amount;
            }
            return sum;
        }

        public bool HasPerGrain { get { return perGrain.Length > 0; } }
    }

    /// <summary>Audio-thread LFO. Advanced once per control tick, output is always 0..1.</summary>
    public struct RevLfo
    {
        float phase;
        float shValue;
        float prevValue;
        float nextValue;
        uint rng;

        public float Advance(LfoShape shape, float rateHz, float sampleRate, int samples)
        {
            if (rng == 0u) rng = 0x9E3779B9u;

            float inc = rateHz * samples / Mathf.Max(1f, sampleRate);
            phase += inc;
            bool wrapped = false;
            while (phase >= 1f) { phase -= 1f; wrapped = true; }
            if (phase < 0f) phase = 0f;

            switch (shape)
            {
                case LfoShape.Triangle: return 1f - Mathf.Abs(2f * phase - 1f);
                case LfoShape.SawUp: return phase;
                case LfoShape.SawDown: return 1f - phase;
                case LfoShape.Square: return phase < 0.5f ? 1f : 0f;
                case LfoShape.SampleHold:
                    if (wrapped || shValue == 0f) shValue = NextRandom();
                    return shValue;
                case LfoShape.SmoothNoise:
                    if (wrapped) { prevValue = nextValue; nextValue = NextRandom(); }
                    float t = phase * phase * (3f - 2f * phase);
                    return Mathf.Lerp(prevValue, nextValue, t);
                default: return 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * phase);
            }
        }

        float NextRandom()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return (rng & 0xFFFFFF) / 16777215f;
        }
    }
}

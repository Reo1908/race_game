using System;
using System.Threading;
using UnityEngine;

namespace RevAudio
{
    /// <summary>
    /// The granular engine itself. Deliberately free of MonoBehaviour so it can be driven
    /// by the audio thread at runtime and by the offline baker in the editor with identical
    /// results.
    ///
    /// Design notes
    /// ------------
    /// * Nothing here allocates once Configure has run. No locks, no Unity API calls, no
    ///   AnimationCurve.Evaluate, no logging on the audio thread.
    /// * Parameters arrive through a single atomic reference swap (Publish), so the game
    ///   thread can update at 60 Hz while the DSP keeps running at its own rate.
    /// * The internal control rate is independent of the frame rate: every block is walked
    ///   in small sub-blocks (controlBlock samples, 32 by default = ~1.5 kHz at 48 kHz) and
    ///   RPM, modulation, LFOs, grain rate and grain size are re-evaluated for each one.
    ///   Grain onsets are placed at sample accuracy inside the block. That is what keeps a
    ///   fast RPM sweep smooth instead of stepping once per rendered frame.
    /// </summary>
    public sealed class RevEngineCore
    {
        public const int LayerCount = 4;
        public const int EngineUp = 0;
        public const int EngineDown = 1;
        public const int ExhaustUp = 2;
        public const int ExhaustDown = 3;

        const float HalfPi = 1.5707963f;

        struct Grain
        {
            public bool active;
            public RevLayerRuntime src;
            public int bus;              // 0 = engine, 1 = exhaust
            public double readPos;       // fractional frame in the source
            public double readStep;      // source frames per output sample
            public int length;           // total output samples
            public int age;
            public int startOffset;      // first sample inside the current block
            public float ampL, ampR;
            public float width;
            public float winPhase, winStep;
        }

        // ---- resources ----
        readonly RevLayerRuntime[] layers = new RevLayerRuntime[LayerCount];
        Grain[] grains = new Grain[192];
        int allocCursor;
        int activeGrains;

        float[] busEngL, busEngR, busExhL, busExhR, mixL, mixR;
        int bufferFrames;

        float[] modSources;
        float[] modOut;

        // ---- parameter handover ----
        RevParams pending;
        RevParams current;

        // ---- dsp state ----
        int sampleRate = 48000;
        float rpmCurrent, rpmPrev, rpmRate;
        float accelBlend = 1f;
        float engGainSm, exhGainSm, masterSm;
        readonly float[] nextGrainCountdown = new float[LayerCount];
        static readonly float[] LayerDecorrelation = { 0f, 0.61803f, -0.38197f, 0.23607f };
        RevLfo lfo1, lfo2;
        RevBiquad hp, lp, notch;
        RevDcBlocker dcBlocker;
        RevGate gate;
        uint rng = 2463534242u;

        // ---- values derived once per control tick ----
        float ctlRpm, ctlRateHz, ctlSizeMs, ctlAmpComp, ctlPitchCents;
        float ctlPosJitterMs, ctlPosOffsetMs, ctlSpread, ctlTimingJitter, ctlBlend;
        float ctlEngGain, ctlExhGain, ctlMaster;
        float ctlHpHz, ctlLpHz, ctlNotchHz, ctlGateThresholdDb;
        float ctlFiringHz, ctlOverlap;

        // ---- metering, read from the main thread ----
        public volatile bool ready;
        public int MeterActiveGrains;
        public float MeterRpm, MeterRateHz, MeterSizeMs, MeterOverlap;
        public float MeterRms, MeterPeak, MeterLoad, MeterAccelBlend, MeterGateGain;
        public readonly float[] MeterPlayhead = new float[LayerCount];

        public int SampleRate { get { return sampleRate; } }

        public void Configure(int outputSampleRate, int maxGrains)
        {
            sampleRate = Mathf.Clamp(outputSampleRate, 8000, 192000);
            int pool = Mathf.Clamp(maxGrains, 8, 512);
            if (grains.Length != pool) grains = new Grain[pool];
            modSources = new float[RevModRuntime.SourceCount];
            modOut = new float[RevModRuntime.TargetCount];
            EnsureBuffers(2048);
            ResetState();
        }

        public void SetLayer(int index, RevLayerRuntime runtime)
        {
            if (index < 0 || index >= LayerCount) return;
            layers[index] = runtime;      // reference assignment is atomic; live grains keep their own reference
        }

        public RevLayerRuntime GetLayer(int index)
        {
            return (index >= 0 && index < LayerCount) ? layers[index] : null;
        }

        /// <summary>Hand a freshly filled parameter block to the audio thread.</summary>
        public void Publish(RevParams p)
        {
            Interlocked.Exchange(ref pending, p);
        }

        public void ResetState()
        {
            for (int i = 0; i < grains.Length; i++) grains[i] = default(Grain);
            for (int i = 0; i < LayerCount; i++)
            {
                // Stagger the four streams so they do not all fire on the same sample.
                nextGrainCountdown[i] = i * 37f;
                MeterPlayhead[i] = 0f;
            }
            activeGrains = 0;
            allocCursor = 0;
            rpmCurrent = rpmPrev = 0f;
            rpmRate = 0f;
            accelBlend = 1f;
            engGainSm = exhGainSm = masterSm = 0f;
            hp.Reset(); lp.Reset(); notch.Reset(); dcBlocker.Reset(); gate.Reset();
        }

        void EnsureBuffers(int frames)
        {
            if (bufferFrames >= frames && busEngL != null) return;
            bufferFrames = 256;
            while (bufferFrames < frames) bufferFrames <<= 1;
            busEngL = new float[bufferFrames];
            busEngR = new float[bufferFrames];
            busExhL = new float[bufferFrames];
            busExhR = new float[bufferFrames];
            mixL = new float[bufferFrames];
            mixR = new float[bufferFrames];
        }

        // ---------------------------------------------------------------- random
        float NextUnit()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return (rng & 0xFFFFFFu) * (1f / 16777215f);
        }

        float NextBipolar() { return NextUnit() * 2f - 1f; }

        // ---------------------------------------------------------------- render
        public void Render(float[] data, int channels, int frames)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            RevParams incoming = Interlocked.Exchange(ref pending, null);
            if (incoming != null) current = incoming;
            RevParams p = current;

            if (!ready || p == null || p.window == null || p.mod == null || frames <= 0 || channels <= 0)
            {
                if (data != null) Array.Clear(data, 0, Math.Min(data.Length, frames * channels));
                return;
            }

            EnsureBuffers(frames);
            Array.Clear(busEngL, 0, frames);
            Array.Clear(busEngR, 0, frames);
            Array.Clear(busExhL, 0, frames);
            Array.Clear(busExhR, 0, frames);

            int cb = Mathf.Clamp(p.controlBlock, 8, 512);
            int pos = 0;
            while (pos < frames)
            {
                int n = Math.Min(cb, frames - pos);
                UpdateControl(p, n);
                ScheduleGrains(p, pos, n);
                pos += n;
            }

            RenderGrains(p, frames);
            MixAndFilter(p, data, channels, frames);

            double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            double budget = frames / (double)sampleRate;
            MeterLoad += ((float)(elapsed / budget) - MeterLoad) * 0.15f;
            MeterActiveGrains = activeGrains;
            MeterRpm = ctlRpm;
            MeterRateHz = ctlRateHz;
            MeterSizeMs = ctlSizeMs;
            MeterOverlap = ctlOverlap;
            MeterAccelBlend = ctlBlend;
        }

        // ---------------------------------------------------------------- control rate
        void UpdateControl(RevParams p, int n)
        {
            float dt = n / (float)sampleRate;

            // --- RPM: de-zipper the frame-rate value at audio rate. A large step (gearshift,
            //     respawn, teleport) snaps through instead of gliding, so it stays tight.
            float target = Mathf.Max(0f, p.rpm);
            float delta = target - rpmCurrent;
            if (Mathf.Abs(delta) >= Mathf.Max(1f, p.rpmSnapDelta))
            {
                rpmCurrent = target;
            }
            else
            {
                float tau = Mathf.Max(0.1f, p.rpmSmoothMs) * 0.001f;
                rpmCurrent += delta * (1f - Mathf.Exp(-dt / tau));
            }
            ctlRpm = rpmCurrent;

            float rawRate = (rpmCurrent - rpmPrev) / Mathf.Max(1e-6f, dt);
            rpmPrev = rpmCurrent;
            float rateTau = Mathf.Max(1f, p.rpmRateSmoothMs) * 0.001f;
            rpmRate += (rawRate - rpmRate) * (1f - Mathf.Exp(-dt / rateTau));

            // --- accel / decel crossfade between the up-sweep and down-sweep recordings
            float blendTarget;
            if (p.useThrottleForBlend)
            {
                blendTarget = Mathf.Clamp01(p.throttle);
            }
            else
            {
                float thr = Mathf.Max(1f, p.rpmRateThreshold);
                blendTarget = Mathf.Clamp01(0.5f + 0.5f * (rpmRate / thr));
            }
            float blendTau = Mathf.Max(1f, p.accelBlendMs) * 0.001f;
            accelBlend += (blendTarget - accelBlend) * (1f - Mathf.Exp(-dt / blendTau));

            // --- modulation sources
            float rpmSpan = Mathf.Max(1f, p.rpmRedline - p.rpmIdle);
            float rpmNorm = Mathf.Clamp01((rpmCurrent - p.rpmIdle) / rpmSpan);
            float rateRef = Mathf.Max(1f, p.rpmRateReference);
            float rateNormSigned = Mathf.Clamp01(0.5f + 0.5f * rpmRate / rateRef);
            float rateNormAbs = Mathf.Clamp01(Mathf.Abs(rpmRate) / rateRef);

            // 4-stroke: every cylinder fires once per two revolutions.
            ctlFiringHz = p.cylinders > 0 ? rpmCurrent * p.cylinders / 120f : rpmCurrent / 60f;

            float lfo1Rate = p.lfo1.syncToFiring ? ctlFiringHz * p.lfo1.firingMultiplier : p.lfo1.rateHz;
            float lfo2Rate = p.lfo2.syncToFiring ? ctlFiringHz * p.lfo2.firingMultiplier : p.lfo2.rateHz;

            modSources[(int)ModSource.None] = 0f;
            modSources[(int)ModSource.RpmNormalised] = rpmNorm;
            modSources[(int)ModSource.RpmRate] = rateNormSigned;
            modSources[(int)ModSource.RpmRateAbs] = rateNormAbs;
            modSources[(int)ModSource.Throttle] = Mathf.Clamp01(p.throttle);
            modSources[(int)ModSource.Load] = Mathf.Clamp01(p.load);
            modSources[(int)ModSource.Ignition] = Mathf.Clamp01(p.ignition);
            modSources[(int)ModSource.Direction] = Mathf.Clamp01(p.direction01);
            modSources[(int)ModSource.AccelDecel] = accelBlend;
            modSources[(int)ModSource.Lfo1] = lfo1.Advance(p.lfo1.shape, lfo1Rate, sampleRate, n);
            modSources[(int)ModSource.Lfo2] = lfo2.Advance(p.lfo2.shape, lfo2Rate, sampleRate, n);
            modSources[(int)ModSource.RandomPerGrain] = 0f;   // resolved at spawn time
            modSources[(int)ModSource.Constant] = 1f;

            p.mod.EvaluateControl(modSources, modOut);

            ctlBlend = Mathf.Clamp01(accelBlend + modOut[(int)ModTarget.AccelDecelBlend]);

            // --- grain rate. Locking it to the firing frequency makes one grain line up with
            //     one combustion event, which is what keeps the note solid at high RPM.
            float rate = Mathf.Lerp(p.rateHz, ctlFiringHz * Mathf.Max(0.01f, p.rateFiringMultiplier),
                                    Mathf.Clamp01(p.rateFollowFiring));
            rate += modOut[(int)ModTarget.GrainRate];
            float rateLo = Mathf.Min(p.rateMinHz, p.rateMaxHz);
            float rateHi = Mathf.Max(p.rateMinHz, p.rateMaxHz);
            ctlRateHz = Mathf.Clamp(rate, Mathf.Max(1f, rateLo), Mathf.Max(2f, rateHi));

            if (p.antiPhasing && ctlFiringHz > 1f)
            {
                // Coherent overlap needs the hop between two grains to be a whole number of
                // firing cycles (the pitch-synchronous overlap-add condition). Snapping the
                // rate to an integer subdivision of the firing frequency does exactly that,
                // and the size/overlap maths below then works off the real rate.
                float division = Mathf.Max(1f, Mathf.Round(ctlFiringHz / Mathf.Max(1f, ctlRateHz)));
                ctlRateHz = ctlFiringHz / division;
            }

            // --- grain size, picked inside the configured range
            float sizeCommon = modOut[(int)ModTarget.GrainSize];
            float sizeMin = p.sizeMinMs + sizeCommon + modOut[(int)ModTarget.GrainSizeMin];
            float sizeMax = p.sizeMaxMs + sizeCommon + modOut[(int)ModTarget.GrainSizeMax];
            if (sizeMax < sizeMin) { float sw = sizeMin; sizeMin = sizeMax; sizeMax = sw; }
            float sel = Mathf.Clamp01(rpmNorm * p.sizeRpmFollow + rateNormAbs * p.sizeRateFollow);
            float sizeMs = Mathf.Lerp(sizeMax, sizeMin, sel);

            // --- keep the overlap inside its range: too little and the stream gets gaps and
            //     amplitude ripple, too much and it smears into mush (and costs CPU).
            float modOverlap = modOut[(int)ModTarget.Overlap];
            float overlapLo = Mathf.Max(1f, Mathf.Min(p.overlapMin, p.overlapMax) + modOverlap);
            float overlapHi = Mathf.Max(overlapLo + 0.1f, Mathf.Max(p.overlapMin, p.overlapMax) + modOverlap);
            float overlap = ctlRateHz * sizeMs * 0.001f;
            if (overlap > overlapHi) sizeMs = overlapHi * 1000f / ctlRateHz;
            else if (overlap < overlapLo) sizeMs = overlapLo * 1000f / ctlRateHz;
            ctlSizeMs = Mathf.Clamp(sizeMs, 1.5f, 600f);
            ctlOverlap = ctlRateHz * ctlSizeMs * 0.001f;

            // Constant perceived loudness while rate and size move around.
            ctlAmpComp = p.normaliseOverlap
                ? 1f / Mathf.Sqrt(Mathf.Max(1f, ctlOverlap) * Mathf.Max(1e-4f, p.windowPower) * 2f)
                : 1f;

            ctlPitchCents = modOut[(int)ModTarget.Pitch];
            ctlPosJitterMs = Mathf.Max(0f, p.positionJitterMs + modOut[(int)ModTarget.PositionJitter]);
            ctlPosOffsetMs = p.positionOffsetMs + modOut[(int)ModTarget.PositionOffset];
            ctlSpread = Mathf.Clamp01(p.stereoSpread + modOut[(int)ModTarget.StereoSpread]);
            ctlTimingJitter = Mathf.Clamp01(p.timingJitter + modOut[(int)ModTarget.TimingJitter]);

            ctlEngGain = Mathf.Max(0f, p.engineBusGain + modOut[(int)ModTarget.EngineLevel]);
            ctlExhGain = Mathf.Max(0f, p.exhaustBusGain + modOut[(int)ModTarget.ExhaustLevel]);
            ctlMaster = Mathf.Max(0f, (p.masterGain + modOut[(int)ModTarget.MasterLevel]) * Mathf.Clamp01(p.ignition));

            ctlHpHz = p.hpHz * Mathf.Pow(2f, modOut[(int)ModTarget.HighpassCutoff]);
            ctlLpHz = p.lpHz * Mathf.Pow(2f, modOut[(int)ModTarget.LowpassCutoff]);
            ctlNotchHz = p.notchHz * Mathf.Pow(2f, modOut[(int)ModTarget.NotchFreq]);
            ctlGateThresholdDb = p.gateThresholdDb + modOut[(int)ModTarget.GateThreshold];
        }

        // ---------------------------------------------------------------- scheduling
        bool LayerReady(RevParams p, int index)
        {
            RevLayerRuntime rt = layers[index];
            return rt != null && rt.valid && p.layers[index].enabled && p.layers[index].gain > 0.0004f;
        }

        void ScheduleGrains(RevParams p, int blockStart, int n)
        {
            // Equal-power crossfade between the rev-up and the rev-down recording of each side.
            float gUpBase = Mathf.Sin(ctlBlend * HalfPi);
            float gDownBase = Mathf.Cos(ctlBlend * HalfPi);

            for (int bus = 0; bus < 2; bus++)
            {
                int up = bus == 0 ? EngineUp : ExhaustUp;
                int down = bus == 0 ? EngineDown : ExhaustDown;
                bool upOk = LayerReady(p, up);
                bool downOk = LayerReady(p, down);
                if (!upOk && !downOk) continue;

                // If one direction is missing, the other one carries the whole side rather
                // than dropping out - so a half-configured setup still makes sound.
                float gUp = upOk ? (downOk ? gUpBase : 1f) : 0f;
                float gDown = downOk ? (upOk ? gDownBase : 1f) : 0f;

                if (upOk) ScheduleLayer(p, up, bus, gUp, blockStart, n);
                if (downOk) ScheduleLayer(p, down, bus, gDown, blockStart, n);
            }
        }

        void ScheduleLayer(RevParams p, int index, int bus, float sideGain, int blockStart, int n)
        {
            float gain = sideGain * p.layers[index].gain;
            if (gain < 0.0004f)
            {
                // Silent layer: keep the clock running so it re-enters in phase, spawn nothing.
                nextGrainCountdown[index] = Mathf.Max(0f, nextGrainCountdown[index] - n);
                return;
            }

            float countdown = nextGrainCountdown[index];
            float period = sampleRate / Mathf.Max(1f, ctlRateHz);

            while (countdown < n)
            {
                int offset = blockStart + (int)Mathf.Max(0f, countdown);
                SpawnGrain(p, index, bus, gain, offset);

                float step = period;
                if (ctlTimingJitter > 0f) step *= 1f + NextBipolar() * ctlTimingJitter * 0.5f;
                if (p.antiPhasing && ctlFiringHz > 1f)
                {
                    // Keep the jittered hop on the cycle grid too, otherwise timing jitter
                    // would undo the phase alignment it is supposed to protect.
                    float cyclePeriod = sampleRate / ctlFiringHz;
                    step = Mathf.Max(1f, Mathf.Round(step / cyclePeriod)) * cyclePeriod;
                }
                countdown += Mathf.Max(8f, step);
            }
            nextGrainCountdown[index] = countdown - n;
        }

        int AllocGrain()
        {
            int count = grains.Length;
            for (int i = 0; i < count; i++)
            {
                int idx = allocCursor;
                allocCursor++;
                if (allocCursor >= count) allocCursor = 0;
                if (!grains[idx].active) return idx;
            }
            return -1;   // pool exhausted: drop the grain instead of stealing an audible one
        }

        // ---------------------------------------------------------------- grain spawn
        void SpawnGrain(RevParams p, int index, int bus, float gain, int offset)
        {
            RevLayerRuntime rt = layers[index];
            if (rt == null || !rt.valid) return;
            int slot = AllocGrain();
            if (slot < 0) return;

            float rnd = NextUnit();                    // one value per grain for the mod matrix
            bool perGrain = p.mod.HasPerGrain;

            // --- where the playhead sits for this RPM (this is the scrub, not a playing clip)
            float pos = rt.PositionFromRpm(ctlRpm);
            MeterPlayhead[index] = pos;
            float sampleRpm = rt.RpmAtPosition(pos);
            double centreFrame = (double)pos * (rt.frames - 1);

            // --- firing period at that spot, in SOURCE frames. Everything phase related is
            //     quantised to this, which is what stops overlapping grains comb-filtering.
            double periodFrames = 0.0;
            if (p.rpmGroupEnabled && p.cylinders > 0 && sampleRpm > 30f)
            {
                double firing = sampleRpm * p.cylinders / 120.0;
                if (firing > 0.5) periodFrames = rt.sampleRate / firing;
            }

            // --- RPM difference resampling: the playhead lands on the closest recorded RPM,
            //     this covers whatever is left over (and extends the clip past its recorded
            //     range at idle or over the redline).
            double ratio = 1.0;
            if (p.rpmGroupEnabled && p.rpmDifferenceResampling && sampleRpm > 30f)
            {
                double limit = Math.Pow(2.0, Mathf.Max(0f, p.maxResampleSemitones) / 12.0);
                ratio = ctlRpm / sampleRpm;
                if (ratio > limit) ratio = limit;
                else if (ratio < 1.0 / limit) ratio = 1.0 / limit;
            }

            float cents = p.layers[index].pitchCents + ctlPitchCents;
            if (p.pitchJitterCents > 0f) cents += NextBipolar() * p.pitchJitterCents;
            if (perGrain) cents += p.mod.EvaluatePerGrain(ModTarget.Pitch, rnd);
            if (cents != 0f) ratio *= Math.Pow(2.0, cents / 1200.0);

            double step = ratio * rt.sampleRate / (double)sampleRate;
            if (step < 0.03) step = 0.03;
            else if (step > 8.0) step = 8.0;

            // --- length
            float sizeMs = ctlSizeMs;
            if (p.sizeJitter > 0f) sizeMs *= 1f + NextBipolar() * p.sizeJitter;
            if (perGrain) sizeMs += p.mod.EvaluatePerGrain(ModTarget.GrainSize, rnd);
            int length = Mathf.Clamp(Mathf.RoundToInt(sizeMs * 0.001f * sampleRate), 16, sampleRate);

            if (p.antiPhasing && p.quantiseGrainLength && periodFrames > 1.0)
            {
                // A whole number of firing cycles per grain: the window then repeats in step
                // with the engine order instead of cutting it at an arbitrary phase.
                double cycles = Math.Round(length * step / periodFrames);
                if (cycles < 1.0) cycles = 1.0;
                length = Mathf.Clamp((int)Math.Round(cycles * periodFrames / step), 16, sampleRate);
            }

            // --- read offset
            double jitter = 0.0;
            if (p.antiPhasing && periodFrames > 1.0)
            {
                // Whole cycles only. The grain reads a DIFFERENT combustion cycle of the
                // recording - which is where the liveliness comes from - while still landing
                // in phase with every other grain in flight.
                double cycles = Math.Round(NextBipolar() * Mathf.Max(0f, p.antiPhaseCycles));
                jitter += cycles * periodFrames;
            }
            else if (ctlPosJitterMs > 0.01f)
            {
                jitter += NextBipolar() * ctlPosJitterMs * 0.001 * rt.sampleRate;
            }
            if (p.layerDecorrelationMs > 0.01f)
            {
                // Fixed per-layer offset so the four streams never sit on top of each other.
                double d = LayerDecorrelation[index] * p.layerDecorrelationMs * 0.001 * rt.sampleRate;
                if (p.antiPhasing && periodFrames > 1.0) d = Math.Round(d / periodFrames) * periodFrames;
                jitter += d;
            }
            jitter += ctlPosOffsetMs * 0.001 * rt.sampleRate;
            if (perGrain) jitter += p.mod.EvaluatePerGrain(ModTarget.PositionOffset, rnd) * 0.001 * rt.sampleRate;

            double start = centreFrame - length * step * 0.5 + jitter;

            // Clamp against the CLIP, never against the playhead range: the range only limits
            // where the playhead may sit, grains are free to read past it so nothing is cut.
            double maxStart = rt.frames - 3 - length * step;
            if (maxStart < 1.0)
            {
                length = Mathf.Max(16, (int)((rt.frames - 4) / step));
                maxStart = Math.Max(1.0, rt.frames - 3 - length * step);
            }
            if (start < 1.0) start = 1.0;
            else if (start > maxStart) start = maxStart;

            // --- level and stereo placement
            float amp = gain * ctlAmpComp;
            if (p.ampJitter > 0f) amp *= Mathf.Max(0f, 1f + NextBipolar() * p.ampJitter);
            if (perGrain) amp *= Mathf.Max(0f, 1f + p.mod.EvaluatePerGrain(ModTarget.GrainLevel, rnd));

            float pan = ctlSpread > 0f ? NextBipolar() * ctlSpread : 0f;
            if (perGrain) pan += p.mod.EvaluatePerGrain(ModTarget.GrainPan, rnd);
            pan = Mathf.Clamp(pan, -1f, 1f);
            float angle = (pan + 1f) * 0.25f * Mathf.PI;      // constant power, unity in the centre

            grains[slot].active = true;
            grains[slot].src = rt;
            grains[slot].bus = bus;
            grains[slot].readPos = start;
            grains[slot].readStep = step;
            grains[slot].length = length;
            grains[slot].age = 0;
            grains[slot].startOffset = offset;
            grains[slot].ampL = amp * Mathf.Cos(angle) * 1.41421356f;
            grains[slot].ampR = amp * Mathf.Sin(angle) * 1.41421356f;
            grains[slot].width = p.layers[index].width;
            grains[slot].winPhase = 0f;
            grains[slot].winStep = 1f / length;
            activeGrains++;
        }

        // ---------------------------------------------------------------- grain rendering
        void RenderGrains(RevParams p, int frames)
        {
            float[] win = p.window;
            bool hermite = p.hermite;
            int alive = 0;
            for (int i = 0; i < grains.Length; i++)
            {
                if (!grains[i].active) continue;
                RenderGrain(ref grains[i], win, hermite, frames);
                if (grains[i].active) alive++;
                else grains[i].src = null;      // let the layer be collected if it was replaced
            }
            activeGrains = alive;
        }

        void RenderGrain(ref Grain g, float[] win, bool hermite, int frames)
        {
            RevLayerRuntime rt = g.src;
            if (rt == null || rt.samples == null || rt.frames < 8) { g.active = false; return; }

            float[] src = rt.samples;
            bool stereo = rt.channels == 2;
            float[] bl = g.bus == 0 ? busEngL : busExhL;
            float[] br = g.bus == 0 ? busEngR : busExhR;

            int i = g.startOffset;
            if (i >= frames) { g.startOffset = i - frames; return; }   // cannot happen, but stays correct if it did
            g.startOffset = 0;

            int remaining = g.length - g.age;
            if (remaining <= 0) { g.active = false; return; }
            int n = Math.Min(frames - i, remaining);
            if (n <= 0) return;

            double rp = g.readPos;
            double stp = g.readStep;
            float ph = g.winPhase;
            float phStep = g.winStep;
            float aL = g.ampL, aR = g.ampR, width = g.width;

            int lo = hermite ? 1 : 0;
            int hi = hermite ? rt.frames - 3 : rt.frames - 2;
            const int WinRes = RevGrainWindow.Resolution;

            for (int k = 0; k < n; k++, i++)
            {
                int i0 = (int)rp;
                float f = (float)(rp - i0);
                // Clamping instead of bailing out keeps the envelope intact, so running into
                // the very edge of a clip fades out rather than clicking.
                if (i0 < lo) { i0 = lo; f = 0f; }
                else if (i0 > hi) { i0 = hi; f = 0f; }

                float wx = ph * WinRes;
                int wi = (int)wx;
                if (wi < 0) wi = 0; else if (wi > WinRes - 1) wi = WinRes - 1;
                float w = win[wi] + (win[wi + 1] - win[wi]) * (wx - wi);

                float l, r;
                if (hermite)
                {
                    if (stereo)
                    {
                        int b = (i0 - 1) << 1;
                        l = Hermite(src[b], src[b + 2], src[b + 4], src[b + 6], f);
                        r = Hermite(src[b + 1], src[b + 3], src[b + 5], src[b + 7], f);
                    }
                    else
                    {
                        l = Hermite(src[i0 - 1], src[i0], src[i0 + 1], src[i0 + 2], f);
                        r = l;
                    }
                }
                else
                {
                    if (stereo)
                    {
                        int b = i0 << 1;
                        float l0 = src[b], r0 = src[b + 1];
                        l = l0 + (src[b + 2] - l0) * f;
                        r = r0 + (src[b + 3] - r0) * f;
                    }
                    else
                    {
                        float s0 = src[i0];
                        l = s0 + (src[i0 + 1] - s0) * f;
                        r = l;
                    }
                }

                float mid = (l + r) * 0.5f;
                float side = (l - r) * 0.5f * width;
                bl[i] += (mid + side) * aL * w;
                br[i] += (mid - side) * aR * w;

                rp += stp;
                ph += phStep;
            }

            g.readPos = rp;
            g.winPhase = ph;
            g.age += n;
            if (g.age >= g.length) g.active = false;
        }

        static float Hermite(float y0, float y1, float y2, float y3, float f)
        {
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            return ((c3 * f + c2) * f + c1) * f + y1;
        }

        // ---------------------------------------------------------------- mix, filter, output
        void MixAndFilter(RevParams p, float[] data, int channels, int frames)
        {
            // Bus levels are ramped across the block instead of stepping once per block, so a
            // camera swinging around the car never zippers.
            float engFrom = engGainSm, exhFrom = exhGainSm;
            float engTo = ctlEngGain, exhTo = ctlExhGain;
            float inv = 1f / frames;

            for (int i = 0; i < frames; i++)
            {
                float t = i * inv;
                float ge = engFrom + (engTo - engFrom) * t;
                float gx = exhFrom + (exhTo - exhFrom) * t;
                mixL[i] = busEngL[i] * ge + busExhL[i] * gx;
                mixR[i] = busEngR[i] * ge + busExhR[i] * gx;
            }
            engGainSm = engTo;
            exhGainSm = exhTo;

            if (p.dcBlock) dcBlocker.ProcessStereo(mixL, mixR, 0, frames, sampleRate);

            if (p.hpEnabled)
            {
                hp.Configure(RevBiquad.Kind.Highpass, sampleRate, ctlHpHz, p.hpQ);
                hp.ProcessStereo(mixL, mixR, 0, frames);
            }
            if (p.notchEnabled)
            {
                notch.Configure(RevBiquad.Kind.Notch, sampleRate, ctlNotchHz, p.notchQ);
                notch.ProcessStereo(mixL, mixR, 0, frames);
            }
            if (p.lpEnabled)
            {
                lp.Configure(RevBiquad.Kind.Lowpass, sampleRate, ctlLpHz, p.lpQ);
                lp.ProcessStereo(mixL, mixR, 0, frames);
            }
            if (p.gateEnabled)
            {
                gate.ProcessStereo(mixL, mixR, 0, frames, sampleRate, ctlGateThresholdDb,
                                   p.gateRangeDb, p.gateAttackMs, p.gateReleaseMs, p.gateHoldMs);
                MeterGateGain = gate.CurrentGain;
            }
            else
            {
                MeterGateGain = 1f;
            }

            float masFrom = masterSm, masTo = ctlMaster;
            bool clip = p.softClip;
            float peak = 0f;
            double sumSq = 0.0;

            if (channels == 2)
            {
                for (int i = 0; i < frames; i++)
                {
                    float m = masFrom + (masTo - masFrom) * (i * inv);
                    float l = mixL[i] * m;
                    float r = mixR[i] * m;
                    if (clip) { l = SoftClip(l); r = SoftClip(r); }
                    data[i * 2] = l;
                    data[i * 2 + 1] = r;
                    float a = Mathf.Abs(l), b = Mathf.Abs(r);
                    if (a > peak) peak = a;
                    if (b > peak) peak = b;
                    sumSq += (double)l * l + (double)r * r;
                }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    float m = masFrom + (masTo - masFrom) * (i * inv);
                    float l = mixL[i] * m;
                    float r = mixR[i] * m;
                    if (clip) { l = SoftClip(l); r = SoftClip(r); }
                    int b = i * channels;
                    if (channels == 1)
                    {
                        data[b] = (l + r) * 0.5f;
                    }
                    else
                    {
                        data[b] = l;
                        data[b + 1] = r;
                        for (int c = 2; c < channels; c++) data[b + c] = 0f;
                    }
                    float pa = Mathf.Abs(l), pb = Mathf.Abs(r);
                    if (pa > peak) peak = pa;
                    if (pb > peak) peak = pb;
                    sumSq += (double)l * l + (double)r * r;
                }
            }

            masterSm = masTo;
            MeterPeak = peak;
            MeterRms = Mathf.Sqrt((float)(sumSq / Math.Max(1, frames * 2)));
        }

        /// <summary>Pade approximation of tanh: unity slope at zero, hard limit at +/-1.</summary>
        public static float SoftClip(float x)
        {
            if (x <= -3f) return -1f;
            if (x >= 3f) return 1f;
            float x2 = x * x;
            return x * (27f + x2) / (27f + 9f * x2);
        }
    }
}

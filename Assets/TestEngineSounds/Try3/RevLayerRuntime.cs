using UnityEngine;

namespace RevAudio
{
    /// <summary>
    /// Decoded, audio-thread-ready version of a RevSampleLayer. Built entirely on the
    /// main thread; the audio thread only ever reads it. Holds the two lookup tables that
    /// turn RPM into a playhead position and back again.
    /// </summary>
    public sealed class RevLayerRuntime
    {
        public const int Lut = 256;

        public float[] samples;          // interleaved
        public int channels;
        public int frames;
        public int sampleRate;
        public bool valid;

        public float rangeStart, rangeEnd;   // normalised clip position
        public float rpmMin, rpmMax;         // sorted
        public bool ascending;               // true if RPM rises with position

        public float[] posToRpm;             // Lut entries across [rangeStart .. rangeEnd]
        public float[] rpmToPos;             // Lut entries across [rpmMin .. rpmMax] -> normalised clip position

        public float gain = 1f;
        public float pitchCents;
        public float width = 1f;

        public static RevLayerRuntime Build(RevSampleLayer layer, out string error)
        {
            error = null;
            var rt = new RevLayerRuntime();
            if (layer == null || !layer.IsAssigned) return rt;

            AudioClip clip = layer.clip;
            if (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData())
            {
                error = clip.name + ": audio data could not be loaded. Set Load Type to Decompress On Load and enable Preload Audio Data.";
                return rt;
            }

            int srcChannels = clip.channels;
            int srcFrames = clip.samples;
            if (srcFrames < 64 || srcChannels < 1)
            {
                error = clip.name + ": clip is too short or has no channels.";
                return rt;
            }

            var raw = new float[srcFrames * srcChannels];
            if (!clip.GetData(raw, 0))
            {
                error = clip.name + ": GetData failed. Compressed-in-memory and streaming clips cannot be read.";
                return rt;
            }

            if (srcChannels > 2 || (layer.forceMono && srcChannels > 1))
            {
                // Fold anything we cannot render natively down to mono.
                var mono = new float[srcFrames];
                float norm = 1f / srcChannels;
                for (int i = 0; i < srcFrames; i++)
                {
                    float sum = 0f;
                    int b = i * srcChannels;
                    for (int c = 0; c < srcChannels; c++) sum += raw[b + c];
                    mono[i] = sum * norm;
                }
                rt.samples = mono;
                rt.channels = 1;
            }
            else
            {
                rt.samples = raw;
                rt.channels = srcChannels;
            }

            rt.frames = srcFrames;
            rt.sampleRate = clip.frequency;
            rt.gain = Mathf.Pow(10f, layer.gainDb / 20f);
            rt.pitchCents = layer.pitchTrimCents;
            rt.width = layer.stereoWidth;

            rt.rangeStart = Mathf.Clamp01(Mathf.Min(layer.playheadRange.x, layer.playheadRange.y));
            rt.rangeEnd = Mathf.Clamp01(Mathf.Max(layer.playheadRange.x, layer.playheadRange.y));
            if (rt.rangeEnd - rt.rangeStart < 1e-4f) rt.rangeEnd = Mathf.Min(1f, rt.rangeStart + 1e-4f);

            BuildMapping(layer, rt);
            rt.valid = true;
            return rt;
        }

        /// <summary>
        /// Re-derives the RPM mapping without touching the decoded samples. Cheap enough to
        /// call while the user drags a slider in the inspector.
        /// </summary>
        public void RefreshMapping(RevSampleLayer layer)
        {
            if (!valid || layer == null) return;
            gain = Mathf.Pow(10f, layer.gainDb / 20f);
            pitchCents = layer.pitchTrimCents;
            width = layer.stereoWidth;
            rangeStart = Mathf.Clamp01(Mathf.Min(layer.playheadRange.x, layer.playheadRange.y));
            rangeEnd = Mathf.Clamp01(Mathf.Max(layer.playheadRange.x, layer.playheadRange.y));
            if (rangeEnd - rangeStart < 1e-4f) rangeEnd = Mathf.Min(1f, rangeStart + 1e-4f);
            BuildMapping(layer, this);
        }

        static void BuildMapping(RevSampleLayer layer, RevLayerRuntime rt)
        {
            // Built into locals and published at the end, so the audio thread never reads a
            // half-filled table while the inspector is being dragged.
            float[] posToRpm = new float[Lut];
            bool useProfile = layer.useDetectedProfile && layer.hasProfile &&
                              layer.detectedRpm != null && layer.detectedRpm.Length > 1;

            for (int i = 0; i < Lut; i++)
            {
                float t = (float)i / (Lut - 1);
                float pos = Mathf.Lerp(rt.rangeStart, rt.rangeEnd, t);
                if (useProfile)
                {
                    posToRpm[i] = SampleArray(layer.detectedRpm, pos);
                }
                else
                {
                    float shaped = layer.positionToRpm != null ? layer.positionToRpm.Evaluate(t) : t;
                    posToRpm[i] = Mathf.Lerp(layer.rpmAtRangeStart, layer.rpmAtRangeEnd, shaped);
                }
            }

            // The table has to be strictly monotonic to be invertible. A real recording
            // wobbles (shift dips, mic noise), so force it in the dominant direction.
            bool ascending = posToRpm[Lut - 1] >= posToRpm[0];
            const float minStep = 0.5f;   // rpm
            float rpmLo, rpmHi;
            if (ascending)
            {
                for (int i = 1; i < Lut; i++)
                    if (posToRpm[i] < posToRpm[i - 1] + minStep) posToRpm[i] = posToRpm[i - 1] + minStep;
                rpmLo = posToRpm[0];
                rpmHi = posToRpm[Lut - 1];
            }
            else
            {
                for (int i = 1; i < Lut; i++)
                    if (posToRpm[i] > posToRpm[i - 1] - minStep) posToRpm[i] = posToRpm[i - 1] - minStep;
                rpmLo = posToRpm[Lut - 1];
                rpmHi = posToRpm[0];
            }

            // Numerically invert into rpm -> position.
            float[] rpmToPos = new float[Lut];
            int cursor = 0;
            for (int i = 0; i < Lut; i++)
            {
                float rpm = Mathf.Lerp(rpmLo, rpmHi, (float)i / (Lut - 1));
                float t = FindNormalisedPosition(posToRpm, ascending, rpm, ref cursor);
                rpmToPos[i] = Mathf.Lerp(rt.rangeStart, rt.rangeEnd, t);
            }

            rt.ascending = ascending;
            rt.rpmMin = rpmLo;
            rt.rpmMax = rpmHi;
            rt.posToRpm = posToRpm;
            rt.rpmToPos = rpmToPos;
        }

        static float FindNormalisedPosition(float[] a, bool ascending, float rpm, ref int cursor)
        {
            // The table is monotonic and we are called with rpm increasing, so a forward
            // scan from the previous hit keeps the whole inversion O(Lut).
            if (ascending)
            {
                cursor = Mathf.Clamp(cursor, 0, Lut - 2);
                while (cursor < Lut - 2 && a[cursor + 1] < rpm) cursor++;
                float span = a[cursor + 1] - a[cursor];
                float f = span > 1e-6f ? Mathf.Clamp01((rpm - a[cursor]) / span) : 0f;
                return (cursor + f) / (Lut - 1);
            }
            else
            {
                int i = Lut - 2;
                while (i > 0 && a[i] < rpm) i--;
                float span = a[i] - a[i + 1];
                float f = span > 1e-6f ? Mathf.Clamp01((a[i] - rpm) / span) : 0f;
                return (i + f) / (Lut - 1);
            }
        }

        static float SampleArray(float[] a, float t01)
        {
            float x = Mathf.Clamp01(t01) * (a.Length - 1);
            int i = (int)x;
            if (i >= a.Length - 1) return a[a.Length - 1];
            return Mathf.Lerp(a[i], a[i + 1], x - i);
        }

        /// <summary>Normalised clip position (0..1) the playhead should sit at for this RPM.</summary>
        public float PositionFromRpm(float rpm)
        {
            if (rpmToPos == null) return rangeStart;
            float span = rpmMax - rpmMin;
            float t = span > 1e-3f ? Mathf.Clamp01((rpm - rpmMin) / span) : 0f;
            float x = t * (Lut - 1);
            int i = (int)x;
            if (i >= Lut - 1) return rpmToPos[Lut - 1];
            return rpmToPos[i] + (rpmToPos[i + 1] - rpmToPos[i]) * (x - i);
        }

        /// <summary>RPM the recording is actually at, at this normalised clip position.</summary>
        public float RpmAtPosition(float pos01)
        {
            if (posToRpm == null) return 0f;
            float span = rangeEnd - rangeStart;
            float t = span > 1e-6f ? Mathf.Clamp01((pos01 - rangeStart) / span) : 0f;
            float x = t * (Lut - 1);
            int i = (int)x;
            if (i >= Lut - 1) return posToRpm[Lut - 1];
            return posToRpm[i] + (posToRpm[i + 1] - posToRpm[i]) * (x - i);
        }
    }
}

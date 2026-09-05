using System;
using UnityEditor;
using UnityEngine;

namespace RevAudio.EditorTools
{
    /// <summary>
    /// Offline RPM detection. Tracks the firing frequency through a recording with a
    /// normalised autocorrelation (the same family of estimator as the McLeod pitch method),
    /// converts it to RPM with the cylinder count, and stores the result as the layer's
    /// position-to-RPM profile.
    ///
    /// Four-stroke: every cylinder fires once per two revolutions, so
    ///     firing frequency = RPM * cylinders / 120
    ///     RPM             = firing frequency * 120 / cylinders
    /// </summary>
    public static class RevSampleAnalyser
    {
        public const int ProfileResolution = 512;
        const int TargetRate = 6000;      // everything above the firing frequency is noise for this job

        public static bool Analyse(RevSampleLayer layer, int cylinders, float searchMinRpm, float searchMaxRpm,
                                   out string message)
        {
            message = null;
            if (layer == null || layer.clip == null) { message = "No clip assigned."; return false; }
            if (cylinders < 1) { message = "Cylinder count must be at least 1."; return false; }
            if (searchMaxRpm <= searchMinRpm + 50f) { message = "Search range is too narrow."; return false; }

            AudioClip clip = layer.clip;
            if (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData())
            {
                message = "Could not load audio data. Set Load Type to Decompress On Load.";
                return false;
            }

            int channels = clip.channels;
            int frames = clip.samples;
            var raw = new float[frames * channels];
            if (!clip.GetData(raw, 0))
            {
                message = "GetData failed. Compressed-in-memory and streaming clips cannot be analysed.";
                return false;
            }

            try
            {
                float[] mono = Decimate(raw, frames, channels, clip.frequency, out int decRate);
                if (mono.Length < 2048) { message = "Clip is too short to analyse."; return false; }

                float fMin = searchMinRpm * cylinders / 120f;
                float fMax = searchMaxRpm * cylinders / 120f;
                int lagMin = Mathf.Max(2, Mathf.FloorToInt(decRate / Mathf.Max(1f, fMax)));
                int lagMax = Mathf.Min(mono.Length / 3, Mathf.CeilToInt(decRate / Mathf.Max(0.5f, fMin)));
                if (lagMax <= lagMin + 2) { message = "Search range does not resolve at this sample rate."; return false; }

                int window = Mathf.Clamp(lagMax * 4, 1024, 8192);
                window = Mathf.Min(window, mono.Length);

                var rpmOut = new float[ProfileResolution];
                var confidence = new float[ProfileResolution];
                int lastLag = -1;

                for (int i = 0; i < ProfileResolution; i++)
                {
                    if ((i & 15) == 0 &&
                        EditorUtility.DisplayCancelableProgressBar("REV - RPM detection",
                            clip.name + "  " + (i * 100 / ProfileResolution) + "%", i / (float)ProfileResolution))
                    {
                        message = "Cancelled.";
                        return false;
                    }

                    float t = ProfileResolution > 1 ? i / (float)(ProfileResolution - 1) : 0f;
                    int start = Mathf.Clamp(Mathf.RoundToInt(t * (mono.Length - window)), 0, mono.Length - window);

                    float lag = EstimateLag(mono, start, window, lagMin, lagMax, lastLag, out float conf);
                    if (lag > 0f && conf > 0.25f)
                    {
                        lastLag = Mathf.RoundToInt(lag);
                        rpmOut[i] = decRate / lag * 120f / cylinders;
                        confidence[i] = conf;
                    }
                    else
                    {
                        rpmOut[i] = 0f;
                        confidence[i] = 0f;
                        lastLag = -1;         // lost track, search the full range again
                    }
                }

                int good = PostProcess(rpmOut, confidence, searchMinRpm, searchMaxRpm);
                if (good < ProfileResolution / 8)
                {
                    message = "Only " + good + " of " + ProfileResolution +
                              " windows produced a confident reading. Check the cylinder count and the search range.";
                    return false;
                }

                layer.detectedRpm = rpmOut;
                layer.detectedMinRpm = Mathf.Min(rpmOut[0], rpmOut[ProfileResolution - 1]);
                layer.detectedMaxRpm = Mathf.Max(rpmOut[0], rpmOut[ProfileResolution - 1]);
                layer.detectedCylinders = cylinders;
                layer.hasProfile = true;
                layer.useDetectedProfile = true;

                // Mirror the detected values into the manual fields so both paths agree.
                float rs = Mathf.Clamp01(Mathf.Min(layer.playheadRange.x, layer.playheadRange.y));
                float re = Mathf.Clamp01(Mathf.Max(layer.playheadRange.x, layer.playheadRange.y));
                layer.rpmAtRangeStart = SampleProfile(rpmOut, rs);
                layer.rpmAtRangeEnd = SampleProfile(rpmOut, re);

                message = string.Format("{0}: {1:0} - {2:0} rpm across the clip, {3:0} - {4:0} rpm inside the playhead range ({5}% confident windows).",
                    clip.name, layer.detectedMinRpm, layer.detectedMaxRpm,
                    Mathf.Min(layer.rpmAtRangeStart, layer.rpmAtRangeEnd),
                    Mathf.Max(layer.rpmAtRangeStart, layer.rpmAtRangeEnd),
                    Mathf.RoundToInt(100f * good / ProfileResolution));
                return true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static float SampleProfile(float[] profile, float t01)
        {
            if (profile == null || profile.Length == 0) return 0f;
            float x = Mathf.Clamp01(t01) * (profile.Length - 1);
            int i = (int)x;
            if (i >= profile.Length - 1) return profile[profile.Length - 1];
            return Mathf.Lerp(profile[i], profile[i + 1], x - i);
        }

        // ------------------------------------------------------------------ signal prep
        static float[] Decimate(float[] raw, int frames, int channels, int sourceRate, out int decimatedRate)
        {
            int factor = Mathf.Max(1, Mathf.RoundToInt(sourceRate / (float)TargetRate));
            decimatedRate = Mathf.RoundToInt(sourceRate / (float)factor);

            int outFrames = frames / factor;
            var output = new float[Mathf.Max(0, outFrames)];
            float invChannels = 1f / channels;
            float invFactor = 1f / factor;

            for (int i = 0; i < outFrames; i++)
            {
                float sum = 0f;
                int baseFrame = i * factor;
                for (int k = 0; k < factor; k++)
                {
                    int idx = (baseFrame + k) * channels;
                    float s = 0f;
                    for (int c = 0; c < channels; c++) s += raw[idx + c];
                    sum += s * invChannels;
                }
                output[i] = sum * invFactor;    // box average doubles as the anti-alias filter
            }

            double mean = 0.0;
            for (int i = 0; i < output.Length; i++) mean += output[i];
            if (output.Length > 0)
            {
                float dc = (float)(mean / output.Length);
                for (int i = 0; i < output.Length; i++) output[i] -= dc;
            }
            return output;
        }

        // ------------------------------------------------------------------ pitch tracking
        static float[] prefix;
        static float[] nsdf;

        static float EstimateLag(float[] x, int start, int window, int lagMin, int lagMax, int trackLag,
                                 out float confidence)
        {
            if (trackLag > 0)
            {
                int lo = Mathf.Max(lagMin, Mathf.RoundToInt(trackLag * 0.72f));
                int hi = Mathf.Min(lagMax, Mathf.RoundToInt(trackLag * 1.40f));
                if (hi > lo + 2)
                {
                    float tracked = Search(x, start, window, lo, hi, out confidence);
                    if (confidence > 0.45f) return tracked;      // stayed locked, no need for a full sweep
                }
            }
            return Search(x, start, window, lagMin, lagMax, out confidence);
        }

        static float Search(float[] x, int start, int window, int lagLo, int lagHi, out float confidence)
        {
            confidence = 0f;
            if (lagHi <= lagLo + 1) return -1f;

            if (prefix == null || prefix.Length < window + 1) prefix = new float[window + 1];
            int span = lagHi - lagLo + 1;
            if (nsdf == null || nsdf.Length < span) nsdf = new float[span];

            prefix[0] = 0f;
            for (int i = 0; i < window; i++)
            {
                float s = x[start + i];
                prefix[i + 1] = prefix[i] + s * s;
            }
            if (prefix[window] < 1e-9f) return -1f;    // silence

            float best = -1f;
            int bestIndex = -1;
            for (int lag = lagLo; lag <= lagHi; lag++)
            {
                int n = window - lag;
                if (n < 32) break;

                float acc = 0f;
                int a = start;
                int b = start + lag;
                for (int i = 0; i < n; i++) acc += x[a + i] * x[b + i];

                float e1 = prefix[n] - prefix[0];
                float e2 = prefix[window] - prefix[lag];
                float denom = Mathf.Sqrt(e1 * e2);
                float r = denom > 1e-9f ? acc / denom : 0f;

                int idx = lag - lagLo;
                nsdf[idx] = r;
                if (r > best) { best = r; bestIndex = idx; }
            }

            if (bestIndex < 0 || best < 0.2f) return -1f;

            // Take the FIRST peak that reaches most of the maximum rather than the maximum
            // itself - that is what stops the tracker locking onto a sub-harmonic (half the
            // real firing rate), which would report half the RPM.
            float threshold = best * 0.90f;
            int chosen = bestIndex;
            for (int i = 1; i < span - 1; i++)
            {
                if (nsdf[i] < threshold) continue;
                if (nsdf[i] >= nsdf[i - 1] && nsdf[i] >= nsdf[i + 1]) { chosen = i; break; }
            }

            confidence = nsdf[chosen];

            // Parabolic refinement around the chosen peak for sub-sample resolution.
            float lagValue = lagLo + chosen;
            if (chosen > 0 && chosen < span - 1)
            {
                float y0 = nsdf[chosen - 1], y1 = nsdf[chosen], y2 = nsdf[chosen + 1];
                float d = y0 - 2f * y1 + y2;
                if (Mathf.Abs(d) > 1e-9f) lagValue += Mathf.Clamp(0.5f * (y0 - y2) / d, -0.5f, 0.5f);
            }
            return lagValue;
        }

        // ------------------------------------------------------------------ clean-up
        static int PostProcess(float[] rpm, float[] confidence, float minRpm, float maxRpm)
        {
            int n = rpm.Length;
            int good = 0;
            for (int i = 0; i < n; i++) if (confidence[i] > 0f) good++;
            if (good == 0) return 0;

            // 1. fill the windows that lost track by interpolating between confident ones
            int first = -1, last = -1;
            for (int i = 0; i < n; i++) if (confidence[i] > 0f) { first = i; break; }
            for (int i = n - 1; i >= 0; i--) if (confidence[i] > 0f) { last = i; break; }
            for (int i = 0; i < first; i++) rpm[i] = rpm[first];
            for (int i = last + 1; i < n; i++) rpm[i] = rpm[last];

            int cursor = first;
            while (cursor < last)
            {
                int next = cursor + 1;
                while (next <= last && confidence[next] <= 0f) next++;
                if (next > cursor + 1)
                {
                    for (int i = cursor + 1; i < next; i++)
                        rpm[i] = Mathf.Lerp(rpm[cursor], rpm[next], (i - cursor) / (float)(next - cursor));
                }
                cursor = next;
            }

            // 2. median filter kills the remaining octave jumps
            var work = new float[n];
            var win = new float[5];
            for (int i = 0; i < n; i++)
            {
                for (int k = -2; k <= 2; k++) win[k + 2] = rpm[Mathf.Clamp(i + k, 0, n - 1)];
                Array.Sort(win);
                work[i] = win[2];
            }

            // 3. gentle smoothing
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int count = 0;
                for (int k = -3; k <= 3; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= n) continue;
                    sum += work[j];
                    count++;
                }
                rpm[i] = sum / count;
            }

            // 4. force it monotonic: the mapping has to be invertible to scrub by RPM
            float headAverage = 0f, tailAverage = 0f;
            int edge = Mathf.Max(1, n / 10);
            for (int i = 0; i < edge; i++) { headAverage += rpm[i]; tailAverage += rpm[n - 1 - i]; }
            bool ascending = tailAverage >= headAverage;

            const float minStep = 0.25f;
            if (ascending)
            {
                for (int i = 1; i < n; i++)
                    if (rpm[i] < rpm[i - 1] + minStep) rpm[i] = rpm[i - 1] + minStep;
            }
            else
            {
                for (int i = 1; i < n; i++)
                    if (rpm[i] > rpm[i - 1] - minStep) rpm[i] = rpm[i - 1] - minStep;
            }

            for (int i = 0; i < n; i++) rpm[i] = Mathf.Clamp(rpm[i], minRpm * 0.5f, maxRpm * 1.5f);
            return good;
        }
    }
}

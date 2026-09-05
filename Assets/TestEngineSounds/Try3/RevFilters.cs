using UnityEngine;

namespace RevAudio
{
    /// <summary>Stereo RBJ biquad, transposed direct form II. Coefficients are recomputed only when the cutoff actually moves.</summary>
    public struct RevBiquad
    {
        float b0, b1, b2, a1, a2;
        float z1L, z2L, z1R, z2R;
        float cachedFreq, cachedQ;
        int cachedType;

        public enum Kind { Lowpass = 0, Highpass = 1, Notch = 2 }

        public void Reset()
        {
            z1L = z2L = z1R = z2R = 0f;
            cachedFreq = -1f;
        }

        public void Configure(Kind kind, float sampleRate, float freq, float q)
        {
            freq = Mathf.Clamp(freq, 10f, sampleRate * 0.45f);
            q = Mathf.Max(0.05f, q);
            // Skip the trig when nothing meaningful changed (modulation moves this every control tick).
            if ((int)kind == cachedType && Mathf.Abs(freq - cachedFreq) < cachedFreq * 0.002f && Mathf.Abs(q - cachedQ) < 1e-4f)
                return;

            cachedType = (int)kind;
            cachedFreq = freq;
            cachedQ = q;

            float w0 = 2f * Mathf.PI * freq / sampleRate;
            float cw = Mathf.Cos(w0);
            float sw = Mathf.Sin(w0);
            float alpha = sw / (2f * q);
            float a0;

            switch (kind)
            {
                case Kind.Highpass:
                    b0 = (1f + cw) * 0.5f; b1 = -(1f + cw); b2 = b0;
                    a0 = 1f + alpha; a1 = -2f * cw; a2 = 1f - alpha;
                    break;
                case Kind.Notch:
                    b0 = 1f; b1 = -2f * cw; b2 = 1f;
                    a0 = 1f + alpha; a1 = -2f * cw; a2 = 1f - alpha;
                    break;
                default:
                    b0 = (1f - cw) * 0.5f; b1 = 1f - cw; b2 = b0;
                    a0 = 1f + alpha; a1 = -2f * cw; a2 = 1f - alpha;
                    break;
            }

            float inv = 1f / a0;
            b0 *= inv; b1 *= inv; b2 *= inv; a1 *= inv; a2 *= inv;
        }

        public void ProcessStereo(float[] l, float[] r, int offset, int count)
        {
            float lb0 = b0, lb1 = b1, lb2 = b2, la1 = a1, la2 = a2;
            float s1L = z1L, s2L = z2L, s1R = z1R, s2R = z2R;

            for (int i = offset; i < offset + count; i++)
            {
                float xl = l[i];
                float yl = lb0 * xl + s1L;
                s1L = lb1 * xl - la1 * yl + s2L;
                s2L = lb2 * xl - la2 * yl;
                l[i] = yl;

                float xr = r[i];
                float yr = lb0 * xr + s1R;
                s1R = lb1 * xr - la1 * yr + s2R;
                s2R = lb2 * xr - la2 * yr;
                r[i] = yr;
            }

            // Denormal flush - a stalled filter state costs more CPU than the filter itself.
            const float tiny = 1e-25f;
            z1L = Mathf.Abs(s1L) < tiny ? 0f : s1L;
            z2L = Mathf.Abs(s2L) < tiny ? 0f : s2L;
            z1R = Mathf.Abs(s1R) < tiny ? 0f : s1R;
            z2R = Mathf.Abs(s2R) < tiny ? 0f : s2R;
        }
    }

    /// <summary>One-pole DC / subsonic blocker. Removes the rumble offset that overlapping grains can accumulate.</summary>
    public struct RevDcBlocker
    {
        float xL, yL, xR, yR;

        public void Reset() { xL = yL = xR = yR = 0f; }

        public void ProcessStereo(float[] l, float[] r, int offset, int count, float sampleRate)
        {
            float rr = 1f - (2f * Mathf.PI * 12f / sampleRate);   // ~12 Hz corner
            for (int i = offset; i < offset + count; i++)
            {
                float inL = l[i];
                yL = inL - xL + rr * yL; xL = inL; l[i] = yL;
                float inR = r[i];
                yR = inR - xR + rr * yR; xR = inR; r[i] = yR;
            }
            if (Mathf.Abs(yL) < 1e-25f) yL = 0f;
            if (Mathf.Abs(yR) < 1e-25f) yR = 0f;
        }
    }

    /// <summary>
    /// Downward expander / noise gate. Sits at the end of the chain to pull down the
    /// hiss and room tone that granular overlap otherwise multiplies.
    /// </summary>
    public struct RevGate
    {
        float env;
        float gain;
        float hold;

        public float CurrentGain => gain;

        public void Reset() { env = 0f; gain = 1f; hold = 0f; }

        public void ProcessStereo(float[] l, float[] r, int offset, int count, float sampleRate,
                                  float thresholdDb, float rangeDb, float attackMs, float releaseMs, float holdMs)
        {
            float threshold = Mathf.Pow(10f, thresholdDb / 20f);
            float knee = threshold * 3.5f;                       // soft expansion zone above the threshold
            float floorGain = Mathf.Pow(10f, -Mathf.Abs(rangeDb) / 20f);
            float atk = Mathf.Exp(-1f / (Mathf.Max(0.05f, attackMs) * 0.001f * sampleRate));
            float rel = Mathf.Exp(-1f / (Mathf.Max(1f, releaseMs) * 0.001f * sampleRate));
            float envCoef = Mathf.Exp(-1f / (0.002f * sampleRate));
            float holdSamples = Mathf.Max(0f, holdMs) * 0.001f * sampleRate;

            for (int i = offset; i < offset + count; i++)
            {
                float peak = Mathf.Max(Mathf.Abs(l[i]), Mathf.Abs(r[i]));
                env = peak > env ? peak : env * envCoef + peak * (1f - envCoef);

                float target;
                if (env >= knee) { target = 1f; hold = holdSamples; }
                else if (env <= threshold)
                {
                    if (hold > 0f) { hold -= 1f; target = gain; }
                    else target = floorGain;
                }
                else
                {
                    float t = (env - threshold) / Mathf.Max(1e-6f, knee - threshold);
                    target = Mathf.Lerp(floorGain, 1f, t * t);
                    hold = holdSamples;
                }

                float c = target > gain ? atk : rel;
                gain = target + (gain - target) * c;

                l[i] *= gain;
                r[i] *= gain;
            }
            if (env < 1e-25f) env = 0f;
        }
    }
}

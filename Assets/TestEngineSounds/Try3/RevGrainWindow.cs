using UnityEngine;

namespace RevAudio
{
    /// <summary>Envelope applied to every grain. Determines how "smeared" or "percussive" the texture is.</summary>
    public enum GrainShape
    {
        Hann,               // safest all-rounder, no plateau
        Tukey,              // flat top with cosine edges -> more of the original transient survives
        Gaussian,           // very smooth, heavy overlap friendly
        Triangle,           // cheap, slightly brighter than Hann
        BlackmanHarris,     // widest fade, lowest sideband splatter
        ExpoDecay,          // fast attack / exponential tail -> emphasises firing pulses
        ReverseExpoDecay,   // mirrored, "sucking in" character
        Custom              // AnimationCurve
    }

    /// <summary>
    /// Builds the grain envelope once into a lookup table (main thread only). The audio
    /// thread just does a linear-interpolated table read, which keeps the per-sample cost
    /// at one multiply-add regardless of how expensive the shape is to evaluate.
    /// </summary>
    public static class RevGrainWindow
    {
        public const int Resolution = 2048;          // table has Resolution + 1 entries (guard sample for interpolation)

        /// <param name="shapeParam">Tukey: plateau fraction. Gaussian: sigma. Expo: decay steepness. Unused otherwise.</param>
        /// <param name="skew">-1 = attack-heavy, 0 = symmetric, +1 = decay-heavy.</param>
        /// <param name="power">Mean square of the window, used for constant-loudness overlap compensation.</param>
        public static float[] Build(GrainShape shape, float shapeParam, float skew, AnimationCurve custom, out float power, float[] reuse = null)
        {
            float[] w = (reuse != null && reuse.Length == Resolution + 1) ? reuse : new float[Resolution + 1];

            // Warping t before evaluating the window moves the peak without changing its ends,
            // so an asymmetric grain still starts and finishes at exactly zero (no clicks).
            float skewExp = Mathf.Pow(2f, Mathf.Clamp(skew, -1f, 1f) * 1.5f);

            for (int i = 0; i <= Resolution; i++)
            {
                float t = (float)i / Resolution;
                float ts = Mathf.Approximately(skewExp, 1f) ? t : Mathf.Pow(t, skewExp);
                w[i] = Mathf.Max(0f, Evaluate(shape, shapeParam, custom, ts));
            }

            // Hard-zero the ends no matter what the user drew into a custom curve.
            w[0] = 0f;
            w[Resolution] = 0f;

            double sum = 0.0;
            for (int i = 0; i <= Resolution; i++) sum += (double)w[i] * w[i];
            power = Mathf.Max(1e-6f, (float)(sum / (Resolution + 1)));
            return w;
        }

        static float Evaluate(GrainShape shape, float p, AnimationCurve custom, float t)
        {
            switch (shape)
            {
                case GrainShape.Hann:
                    return 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * t));

                case GrainShape.Tukey:
                {
                    float plateau = Mathf.Clamp(p, 0f, 0.98f);
                    float edge = (1f - plateau) * 0.5f;
                    if (edge <= 1e-5f) return 1f;
                    if (t < edge) return 0.5f * (1f - Mathf.Cos(Mathf.PI * t / edge));
                    if (t > 1f - edge) return 0.5f * (1f - Mathf.Cos(Mathf.PI * (1f - t) / edge));
                    return 1f;
                }

                case GrainShape.Gaussian:
                {
                    float sigma = Mathf.Clamp(p, 0.05f, 0.5f);
                    float x = (t - 0.5f) / sigma;
                    float g = Mathf.Exp(-0.5f * x * x);
                    float atEdge = Mathf.Exp(-0.5f * (0.5f / sigma) * (0.5f / sigma));
                    return (g - atEdge) / (1f - atEdge);      // shifted so the ends really hit zero
                }

                case GrainShape.Triangle:
                    return 1f - Mathf.Abs(2f * t - 1f);

                case GrainShape.BlackmanHarris:
                {
                    const float a0 = 0.35875f, a1 = 0.48829f, a2 = 0.14128f, a3 = 0.01168f;
                    float x = 2f * Mathf.PI * t;
                    return a0 - a1 * Mathf.Cos(x) + a2 * Mathf.Cos(2f * x) - a3 * Mathf.Cos(3f * x);
                }

                case GrainShape.ExpoDecay:
                {
                    float k = Mathf.Lerp(2f, 12f, Mathf.Clamp01(p));
                    const float attack = 0.02f;
                    float env = t < attack ? t / attack : Mathf.Exp(-k * (t - attack) / (1f - attack));
                    float tail = Mathf.Min(1f, (1f - t) / 0.05f);   // forced fade so the tail cannot click
                    return env * tail;
                }

                case GrainShape.ReverseExpoDecay:
                    return Evaluate(GrainShape.ExpoDecay, p, custom, 1f - t);

                case GrainShape.Custom:
                    return custom != null ? custom.Evaluate(t) : 0f;
            }
            return 0f;
        }
    }
}

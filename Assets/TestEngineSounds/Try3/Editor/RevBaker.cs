using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RevAudio.EditorTools
{
    public enum RevBakeMode
    {
        RpmSweep,        // one continuous idle -> redline (-> idle) render
        SteadyStateSet   // one seamless loop per RPM step, for a cheap sample player fallback
    }

    [Serializable]
    public class RevBakeSettings
    {
        public RevBakeMode mode = RevBakeMode.RpmSweep;
        public int sampleRate = 48000;
        public bool float32 = false;

        [Tooltip("Sweep length for one direction.")]
        public float sweepSeconds = 12f;
        public bool sweepUp = true;
        public bool sweepDown = true;

        [Tooltip("How many steady-state loops between idle and redline.")]
        public int steadySteps = 12;
        public float steadyLoopSeconds = 2f;

        [Tooltip("Listening angle to bake: 0 = fully behind the car, 1 = fully in front.")]
        [Range(0f, 1f)] public float direction01 = 1f;
        [Range(0f, 1f)] public float throttle = 1f;
        [Range(0f, 1f)] public float load = 0.5f;

        public bool normalise = true;
        public string outputFolder = "Assets/BakedEngine";
        public string namePrefix = "engine";
    }

    /// <summary>
    /// Renders the granular engine offline into WAV assets. Same DSP core as the live
    /// component, so a bake sounds like what you tuned - only frozen, for platforms or
    /// distant cars where you would rather pay for a sample player than for grains.
    /// </summary>
    public static class RevBaker
    {
        const int Chunk = 1024;

        public static void Bake(RevGranularEngine engine, RevBakeSettings s)
        {
            if (engine == null || s == null) return;

            bool any = false;
            for (int i = 0; i < RevEngineCore.LayerCount; i++)
            {
                RevLayerRuntime rt = engine.GetLayerRuntime(i);
                if (rt != null && rt.valid) { any = true; break; }
            }
            if (!any)
            {
                EditorUtility.DisplayDialog("REV bake", "No usable sample layers. Assign at least one clip first.", "OK");
                return;
            }

            Directory.CreateDirectory(s.outputFolder);

            try
            {
                if (s.mode == RevBakeMode.RpmSweep) BakeSweep(engine, s);
                else BakeSteadySet(engine, s);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }

        static RevEngineCore CreateCore(RevGranularEngine engine, RevBakeSettings s)
        {
            var core = new RevEngineCore();
            core.Configure(s.sampleRate, engine.maxGrains);
            for (int i = 0; i < RevEngineCore.LayerCount; i++) core.SetLayer(i, engine.GetLayerRuntime(i));
            core.ready = true;
            return core;
        }

        static void BakeSweep(RevGranularEngine engine, RevBakeSettings s)
        {
            if (!s.sweepUp && !s.sweepDown) return;

            int perLeg = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(0.25f, s.sweepSeconds) * s.sampleRate));
            int legs = (s.sweepUp ? 1 : 0) + (s.sweepDown ? 1 : 0);
            int total = perLeg * legs;

            var core = CreateCore(engine, s);
            var p = new RevParams();
            var output = new float[total * 2];

            float idle = engine.rpmRange.x;
            float redline = engine.rpmRange.y;

            // Pre-roll so the RPM ramp, the filters and the grain pool are settled before
            // the first sample we keep.
            PreRoll(core, engine, p, s, s.sweepUp ? idle : redline, s.sweepUp ? s.throttle : 0f);

            var scratch = new float[Chunk * 2];
            int written = 0;
            int leg = 0;

            for (int legIndex = 0; legIndex < 2; legIndex++)
            {
                bool up = legIndex == 0;
                if (up && !s.sweepUp) continue;
                if (!up && !s.sweepDown) continue;

                for (int f = 0; f < perLeg; f += Chunk)
                {
                    int n = Mathf.Min(Chunk, perLeg - f);
                    float t = perLeg > 1 ? f / (float)(perLeg - 1) : 0f;
                    float rpm = up ? Mathf.Lerp(idle, redline, t) : Mathf.Lerp(redline, idle, t);

                    engine.FillParams(p, rpm, up ? s.throttle : 0f, s.load, 1f, s.direction01);
                    core.Publish(p);
                    core.Render(scratch, 2, n);
                    Array.Copy(scratch, 0, output, written * 2, n * 2);
                    written += n;

                    if ((f & 8191) == 0 &&
                        EditorUtility.DisplayCancelableProgressBar("REV bake", "Rendering sweep...",
                            (leg * perLeg + f) / (float)total))
                        return;
                }
                leg++;
            }

            if (s.normalise) Normalise(output, written * 2, 0.89f);

            string path = Path.Combine(s.outputFolder, s.namePrefix + "_sweep_" +
                (s.sweepUp && s.sweepDown ? "updown" : (s.sweepUp ? "up" : "down")) + ".wav");
            WriteWav(path, output, written, 2, s.sampleRate, s.float32);
        }

        static void BakeSteadySet(RevGranularEngine engine, RevBakeSettings s)
        {
            int steps = Mathf.Clamp(s.steadySteps, 1, 64);
            float idle = engine.rpmRange.x;
            float redline = engine.rpmRange.y;
            var scratch = new float[Chunk * 2];

            for (int step = 0; step < steps; step++)
            {
                float t = steps > 1 ? step / (float)(steps - 1) : 0f;
                float rpm = Mathf.Lerp(idle, redline, t);

                // Round the loop to a whole number of firing cycles so the loop point does not
                // land in the middle of a combustion event.
                int loopFrames = Mathf.Max(1024, Mathf.RoundToInt(Mathf.Max(0.25f, s.steadyLoopSeconds) * s.sampleRate));
                if (engine.cylinders > 0 && rpm > 30f)
                {
                    double period = s.sampleRate * 120.0 / (rpm * engine.cylinders);
                    if (period > 4.0)
                    {
                        double cycles = Math.Max(1.0, Math.Round(loopFrames / period));
                        loopFrames = (int)Math.Round(cycles * period);
                    }
                }

                int tail = Mathf.Min(loopFrames / 2, Mathf.RoundToInt(0.25f * s.sampleRate));
                int total = loopFrames + tail;

                var core = CreateCore(engine, s);
                var p = new RevParams();
                PreRoll(core, engine, p, s, rpm, s.throttle);

                var output = new float[total * 2];
                int written = 0;
                while (written < total)
                {
                    int n = Mathf.Min(Chunk, total - written);
                    engine.FillParams(p, rpm, s.throttle, s.load, 1f, s.direction01);
                    core.Publish(p);
                    core.Render(scratch, 2, n);
                    Array.Copy(scratch, 0, output, written * 2, n * 2);
                    written += n;
                }

                // Wrap the tail back onto the head: the loop then joins itself smoothly
                // without the usual crossfade dip in the middle of the clip.
                for (int i = 0; i < tail; i++)
                {
                    float w = i / (float)tail;
                    int a = i * 2;
                    int b = (loopFrames + i) * 2;
                    output[a] = output[a] * w + output[b] * (1f - w);
                    output[a + 1] = output[a + 1] * w + output[b + 1] * (1f - w);
                }

                if (s.normalise) Normalise(output, loopFrames * 2, 0.89f);

                string path = Path.Combine(s.outputFolder,
                    string.Format("{0}_{1:0000}rpm.wav", s.namePrefix, Mathf.RoundToInt(rpm)));
                WriteWav(path, output, loopFrames, 2, s.sampleRate, s.float32);

                if (EditorUtility.DisplayCancelableProgressBar("REV bake",
                        "Steady state " + Mathf.RoundToInt(rpm) + " rpm", (step + 1) / (float)steps))
                    return;
            }
        }

        static void PreRoll(RevEngineCore core, RevGranularEngine engine, RevParams p, RevBakeSettings s,
                            float rpm, float throttle)
        {
            var scratch = new float[Chunk * 2];
            int frames = s.sampleRate / 2;
            for (int f = 0; f < frames; f += Chunk)
            {
                int n = Mathf.Min(Chunk, frames - f);
                engine.FillParams(p, rpm, throttle, s.load, 1f, s.direction01);
                core.Publish(p);
                core.Render(scratch, 2, n);
            }
        }

        static void Normalise(float[] data, int count, float target)
        {
            float peak = 0f;
            for (int i = 0; i < count; i++)
            {
                float a = Mathf.Abs(data[i]);
                if (a > peak) peak = a;
            }
            if (peak < 1e-5f) return;
            float gain = target / peak;
            if (gain > 8f) gain = 8f;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
        }

        // ------------------------------------------------------------------ wav writer
        public static void WriteWav(string path, float[] interleaved, int frames, int channels, int sampleRate, bool float32)
        {
            int bitsPerSample = float32 ? 32 : 16;
            int bytesPerSample = bitsPerSample / 8;
            int dataBytes = frames * channels * bytesPerSample;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new char[] { 'W', 'A', 'V', 'E' });

                w.Write(new char[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)(float32 ? 3 : 1));                  // 3 = IEEE float, 1 = PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * bytesPerSample);    // byte rate
                w.Write((short)(channels * bytesPerSample));        // block align
                w.Write((short)bitsPerSample);

                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);

                int count = frames * channels;
                if (float32)
                {
                    for (int i = 0; i < count; i++) w.Write(interleaved[i]);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        float v = Mathf.Clamp(interleaved[i], -1f, 1f);
                        w.Write((short)Mathf.RoundToInt(v * 32767f));
                    }
                }
            }

            string assetPath = path.Replace('\\', '/');
            if (assetPath.StartsWith("Assets/")) AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }
    }
}

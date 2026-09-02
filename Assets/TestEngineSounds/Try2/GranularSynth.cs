using System;
using UnityEngine;

/// <summary>
/// High-quality real-time granular scrubber.
///
/// Feeds short, overlapping "grains" read from a source AudioClip. Each grain
/// always plays forward through the source at its native pitch - moving the
/// read position (scrubPosition) slowly or quickly, forwards or backwards,
/// produces smooth, pitch-accurate scrubbing through the recording.
///
/// This is the same principle behind Crankcase Audio's REV (bi-directional,
/// variable-speed playback of a recording without altering its pitch) and
/// classic granular synthesis (see: https://blog.native-instruments.com/granular-synthesis/).
/// Holding the slider still gives a sustained "frozen" tone at that exact
/// instant in the recording - REV's "steady-state" behavior.
///
/// Setup:
///  1. Add this component to a GameObject.
///  2. Assign an AudioClip to "Clip". For best results set its Import Settings
///     Load Type to "Decompress On Load" so random-access reads are fast.
///  3. Press Play and drag "Scrub Position" (Inspector slider, or the
///     on-screen debug slider) between 0 and 1.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class GranularSynth : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The recording to scrub through.")]
    public AudioClip clip;

    [Header("Debug Control")]
    [Tooltip("Normalized read position within the clip. 0 = start, 1 = end.")]
    [Range(0f, 1f)] public float scrubPosition = 0f;

    [Tooltip("Draw an on-screen slider too, so you can drag scrubPosition in a build, not just the Inspector.")]
    public bool showOnScreenSlider = true;

    [Header("Grain Settings")]
    [Tooltip("Length of each grain in milliseconds. Shorter = more responsive/grainier, longer = smoother/more tonal.")]
    [Range(10f, 120f)] public float grainDurationMs = 40f;

    [Tooltip("How many grains overlap at once. Higher = smoother crossfades, more CPU. Even numbers (4, 6, 8) sum flattest.")]
    [Range(2, 8)] public int overlapCount = 4;

    [Tooltip("How quickly the internal read head follows scrubPosition, in seconds. 0 = instant (can click on big jumps).")]
    [Range(0f, 0.25f)] public float positionSmoothingTime = 0.03f;

    [Header("Output")]
    [Range(0f, 2f)] public float outputGain = 1f;
    [Tooltip("Gentle soft-clip safety limiter on the final output.")]
    public bool softLimiter = true;

    // ---- internal state ----
    float[] sourceData;        // interleaved source samples
    int sourceChannels;
    int sourceFrameCount;      // per-channel sample count
    double sampleRateRatio;    // source sample rate / output sample rate

    double smoothedSourcePos;  // current (smoothed) read position, in SOURCE frames
    float smoothingCoeff;

    struct Grain
    {
        public bool active;
        public double readPos; // fractional frame index into source
        public int age;        // elapsed output samples
    }

    Grain[] grains;
    int grainLengthSamples;    // in OUTPUT samples
    int hopSamples;            // in OUTPUT samples
    int grainClock;
    int nextSlot;
    float[] window;            // precomputed Hann window, length == grainLengthSamples
    float grainGainComp;       // COLA normalization = 2 / overlapCount

    readonly object dataLock = new object();
    bool needsRebuildGrainTiming = true;
    int cachedOutputSampleRate;

    AudioSource audioSource;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        // AudioSettings.outputSampleRate must be read on the main thread -
        // cache it here so the audio callback never has to touch it.
        cachedOutputSampleRate = AudioSettings.outputSampleRate;
    }

    void Start()
    {
        LoadClip(clip);

        // OnAudioFilterRead only fires while the AudioSource is "playing" - we
        // don't need an actual clip assigned to it, since we generate the
        // output ourselves inside the callback.
        audioSource.playOnAwake = false;
        audioSource.loop = true;
        audioSource.Play();
    }

    void OnAudioConfigurationChanged(bool deviceWasChanged)
    {
        // Sample rate/buffer size can change (e.g. device swap) - refresh on
        // the main thread and force the grain timing to rebuild.
        cachedOutputSampleRate = AudioSettings.outputSampleRate;
        needsRebuildGrainTiming = true;
    }

    void OnValidate()
    {
        needsRebuildGrainTiming = true;
    }

    /// <summary>Load (or reload) the source clip's samples for random-access scrubbing.</summary>
    public void LoadClip(AudioClip newClip)
    {
        if (newClip == null) return;

        newClip.LoadAudioData();

        var data = new float[newClip.samples * newClip.channels];
        newClip.GetData(data, 0);

        lock (dataLock)
        {
            clip = newClip;
            sourceData = data;
            sourceChannels = newClip.channels;
            sourceFrameCount = newClip.samples;
            sampleRateRatio = (double)newClip.frequency / AudioSettings.outputSampleRate;
            smoothedSourcePos = Mathf.Clamp01(scrubPosition) * (sourceFrameCount - 1);
        }
    }

    void RebuildGrainTiming(int outputSampleRate)
    {
        grainLengthSamples = Mathf.Max(8, Mathf.RoundToInt(grainDurationMs * 0.001f * outputSampleRate));
        int overlap = Mathf.Max(2, overlapCount);
        hopSamples = Mathf.Max(1, grainLengthSamples / overlap);
        // Force grain length to be an exact multiple of the hop so precisely
        // "overlap" grains are always active at once (classic OLA scheduling).
        grainLengthSamples = hopSamples * overlap;

        window = new float[grainLengthSamples];
        for (int i = 0; i < grainLengthSamples; i++)
        {
            float t = (float)i / (grainLengthSamples - 1);
            window[i] = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * t); // Hann
        }

        grains = new Grain[overlap];
        grainClock = 0;
        nextSlot = 0;

        // COLA normalization: N overlapping Hann grains spaced at length/N sum
        // to a constant N/2, so scale by 2/N to bring the level back to ~1.0.
        grainGainComp = 2f / overlap;

        float smoothingSeconds = Mathf.Max(0.0001f, positionSmoothingTime);
        smoothingCoeff = 1f - Mathf.Exp(-1f / (smoothingSeconds * outputSampleRate));

        needsRebuildGrainTiming = false;
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        lock (dataLock)
        {
            if (sourceData == null || sourceFrameCount < 4) return;

            if (needsRebuildGrainTiming || grains == null) RebuildGrainTiming(cachedOutputSampleRate);

            float targetPos = Mathf.Clamp01(scrubPosition) * (sourceFrameCount - 1);

            int frameCount = data.Length / channels;
            for (int frame = 0; frame < frameCount; frame++)
            {
                // 1) smooth the read head toward the (possibly jumpy) UI target
                smoothedSourcePos += (targetPos - smoothedSourcePos) * smoothingCoeff;

                // 2) spawn a new grain on schedule
                if (grainClock % hopSamples == 0)
                {
                    grains[nextSlot].active = true;
                    grains[nextSlot].age = 0;
                    grains[nextSlot].readPos = smoothedSourcePos;
                    nextSlot = (nextSlot + 1) % grains.Length;
                }
                grainClock++;

                // 3) sum active grains per output channel
                for (int ch = 0; ch < channels; ch++)
                {
                    float sum = 0f;
                    for (int g = 0; g < grains.Length; g++)
                    {
                        if (!grains[g].active) continue;
                        float w = window[grains[g].age];
                        int srcCh = sourceChannels == 1 ? 0 : (ch % sourceChannels);
                        sum += SampleCubic(grains[g].readPos, srcCh) * w;
                    }
                    sum *= grainGainComp * outputGain;
                    if (softLimiter) sum = SoftClip(sum);
                    data[frame * channels + ch] = sum;
                }

                // 4) advance all active grains (always forward => pitch preserved)
                for (int g = 0; g < grains.Length; g++)
                {
                    if (!grains[g].active) continue;
                    grains[g].readPos += sampleRateRatio;
                    grains[g].age++;
                    if (grains[g].age >= grainLengthSamples) grains[g].active = false;
                }
            }
        }
    }

    // 4-point cubic (Catmull-Rom) interpolation for smooth, low-artifact fractional reads.
    float SampleCubic(double pos, int channel)
    {
        int i1 = (int)Math.Floor(pos);
        float frac = (float)(pos - i1);

        int i0 = Clamp(i1 - 1);
        int i2 = Clamp(i1 + 1);
        int i3 = Clamp(i1 + 2);
        i1 = Clamp(i1);

        float y0 = GetFrame(i0, channel);
        float y1 = GetFrame(i1, channel);
        float y2 = GetFrame(i2, channel);
        float y3 = GetFrame(i3, channel);

        float a0 = y3 - y2 - y0 + y1;
        float a1 = y0 - y1 - a0;
        float a2 = y2 - y0;
        float a3 = y1;

        return a0 * frac * frac * frac + a1 * frac * frac + a2 * frac + a3;
    }

    int Clamp(int idx) => idx < 0 ? 0 : (idx >= sourceFrameCount ? sourceFrameCount - 1 : idx);

    float GetFrame(int frameIndex, int channel) => sourceData[frameIndex * sourceChannels + channel];

    static float SoftClip(float x)
    {
        // Cheap tanh-style soft limiter: transparent under ~0.8, gently rounds off peaks.
        const float t = 0.8f;
        float a = Mathf.Abs(x);
        if (a <= t) return x;
        float sign = x < 0f ? -1f : 1f;
        return sign * (t + (1f - t) * (float)Math.Tanh((a - t) / (1f - t)));
    }

    void OnGUI()
    {
        if (!showOnScreenSlider) return;
        const int w = 320, h = 24, margin = 16;
        GUI.Box(new Rect(margin - 4, margin - 4, w + 8, h + 26), GUIContent.none);
        GUI.Label(new Rect(margin, margin, w, 20), $"Scrub Position: {scrubPosition:F3}");
        scrubPosition = GUI.HorizontalSlider(new Rect(margin, margin + 20, w, h), scrubPosition, 0f, 1f);
    }
}
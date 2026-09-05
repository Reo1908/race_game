using System;
using UnityEngine;

namespace RevAudio
{
    /// <summary>
    /// One recorded sweep. Four of these make a full engine:
    /// engine-up, engine-down, exhaust-up, exhaust-down.
    /// </summary>
    [Serializable]
    public class RevSampleLayer
    {
        public bool enabled = true;
        public AudioClip clip;

        [Header("Playhead range")]
        [Tooltip("Limits where the playhead is allowed to sit, as a fraction of the clip. " +
                 "The sample itself is NOT trimmed: a grain that starts near the edge is still " +
                 "allowed to read past the range, so nothing gets chopped off mid-grain. " +
                 "Use this to keep the playhead out of the garbage at the head/tail of the recording.")]
        [MinMaxRange(0f, 1f)] public Vector2 playheadRange = new Vector2(0.02f, 0.98f);

        [Header("RPM mapping")]
        [Tooltip("Engine RPM at the START of the playhead range.")]
        public float rpmAtRangeStart = 1000f;
        [Tooltip("Engine RPM at the END of the playhead range. For a rev-DOWN recording set this " +
                 "lower than Rpm At Range Start - the mapping flips automatically.")]
        public float rpmAtRangeEnd = 8000f;
        [Tooltip("Shape of the RPM ramp inside the recording. x = position in the range, " +
                 "y = normalised RPM. Real recordings never ramp linearly, so either draw this " +
                 "or let Detect RPM analyse the clip and build it for you.")]
        public AnimationCurve positionToRpm = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Tooltip("Use the analysed RPM profile (from Detect RPM) instead of the curve above.")]
        public bool useDetectedProfile = true;

        [Header("Trim")]
        [Range(-24f, 12f)] public float gainDb = 0f;
        [Range(-1200f, 1200f)] public float pitchTrimCents = 0f;
        [Tooltip("Stereo width of this layer. 0 = mono fold-down, 1 = as recorded, above 1 = widened.")]
        [Range(0f, 2f)] public float stereoWidth = 1f;
        [Tooltip("Fold the clip to mono on load. Halves the memory and the per-sample read cost.")]
        public bool forceMono = false;

        // ---- filled in by the editor-side analyser, serialised with the component ----
        [HideInInspector] public float[] detectedRpm;        // RPM sampled evenly across the WHOLE clip
        [HideInInspector] public float detectedMinRpm;
        [HideInInspector] public float detectedMaxRpm;
        [HideInInspector] public int detectedCylinders;
        [HideInInspector] public bool hasProfile;

        public bool IsAssigned { get { return enabled && clip != null; } }
    }
}

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RetroFX
{
    /// <summary>
    /// Emulates image persistence on old analogue camera tubes (vidicon
    /// etc): very bright spots (headlights, taillights, muzzle flashes)
    /// leave a fading trail across several FRAMES, not a same-frame blur.
    /// This means it's only visible on things that MOVE relative to the
    /// screen - a static bright light won't show a trail, same as the
    /// real thing. Great for racing/rally footage-style light trails.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/RetroFX/Phosphor Trail")]
    public class PhosphorTrailVolume : VolumeComponent, IPostProcessComponent
    {
        [Tooltip("Master enable for the effect.")]
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Threshold")]
        [Tooltip("Brightness level (linear HDR) above which pixels start leaving a trail.")]
        public ClampedFloatParameter threshold = new ClampedFloatParameter(1.3f, 0f, 5f);

        [Tooltip("Softens the threshold cutoff.")]
        public ClampedFloatParameter knee = new ClampedFloatParameter(0.3f, 0f, 2f);

        [Tooltip("Clamps extracted bright pixels so a single very bright pixel doesn't dominate the trail.")]
        public ClampedFloatParameter clamp = new ClampedFloatParameter(15f, 0f, 100f);

        [Tooltip("Widens each frame's fresh bright pixels before they're written into the trail. At high speeds a light can move further per frame than a single stamp covers, leaving gaps that read as choppy separate blobs instead of one continuous streak - raise this until the gaps close up. 0 disables it (matches the original behavior).")]
        public ClampedFloatParameter stampSpread = new ClampedFloatParameter(1.5f, 0f, 6f);

        [Tooltip("Connects this frame's stamp to roughly where it was last frame by smearing it along its own motion vector - this is what actually turns a choppy string of blobs into one continuous line at high speed, rather than just fattening each blob in place like Stamp Spread does. Also picks up camera motion, so panning past a static light smears it too. 0 disables it.")]
        public MinFloatParameter smearAmount = new MinFloatParameter(1f, 0f);

        [Tooltip("How many samples are taken along the motion vector when smearing. More = smoother line at the cost of a bit more GPU time. Rarely needs to go above 8-10.")]
        public ClampedIntParameter smearSteps = new ClampedIntParameter(8, 1, 16);

        [Header("Persistence")]
        [Tooltip("How much of the trail survives from one frame to the next, roughly normalized to 60fps. Close to 1 = very long, slow-fading trails (classic tube 'ghosting'). Lower = short, quick trails.")]
        public ClampedFloatParameter persistence = new ClampedFloatParameter(0.88f, 0f, 0.99f);

        [Tooltip("Resolution divisor for the trail buffer. Higher = cheaper and a bit softer/chunkier - low-res tube cameras were genuinely quite soft, so this can help sell the look.")]
        public ClampedIntParameter downsample = new ClampedIntParameter(2, 1, 8);

        [Tooltip("A small blur applied to the trail only for display each frame (not fed back), so it reads as a soft phosphor glow rather than sharp ghost pixels.")]
        public ClampedIntParameter softness = new ClampedIntParameter(2, 0, 6);

        [Header("Look")]
        [Tooltip("Overall trail strength.")]
        public MinFloatParameter intensity = new MinFloatParameter(1.2f, 0f);

        [Tooltip("Tints the trail/phosphor color. Old tube cameras commonly skewed slightly green or amber depending on the phosphor type - try that instead of pure white for extra character.")]
        public ColorParameter tint = new ColorParameter(Color.white, true, false, true);

        public bool IsActive() => enabled.value && intensity.value > 0f;

        public bool IsTileCompatible() => false;
    }
}

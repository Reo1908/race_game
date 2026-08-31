using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RetroFX
{
    /// <summary>
    /// Volume override for "Camera Streak" - emulates the vertical smear /
    /// anamorphic streak old tube cameras and cheap CRT-era optics produced
    /// when a bright highlight overexposed the sensor.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/RetroFX/Camera Streak")]
    public class CameraStreakVolume : VolumeComponent, IPostProcessComponent
    {
        [Tooltip("Master enable for the effect.")]
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Threshold")]
        [Tooltip("Brightness level (linear HDR) above which pixels start streaking.")]
        public ClampedFloatParameter threshold = new ClampedFloatParameter(1.2f, 0f, 5f);

        [Tooltip("Softens the threshold cutoff.")]
        public ClampedFloatParameter knee = new ClampedFloatParameter(0.3f, 0f, 2f);

        [Tooltip("Clamps extracted bright pixels before streaking so fireflies don't dominate.")]
        public ClampedFloatParameter clamp = new ClampedFloatParameter(15f, 0f, 100f);

        [Header("Streak Shape")]
        [Tooltip("Direction of the streak in degrees. 90 = vertical (classic vidicon tube smear), 0 = horizontal (anamorphic lens flare).")]
        public ClampedFloatParameter angle = new ClampedFloatParameter(90f, 0f, 180f);

        [Tooltip("Base sample distance per iteration, in texels. Bigger = the streak reaches further per pass.")]
        public ClampedFloatParameter length = new ClampedFloatParameter(6f, 0.5f, 32f);

        [Tooltip("Number of accumulation passes. More passes = longer streak (cost scales with this).")]
        public ClampedIntParameter iterations = new ClampedIntParameter(5, 1, 10);

        [Tooltip("How much each successive pass fades. Lower = streak dies out quickly near the source; higher = a long, slowly-fading streak.")]
        public ClampedFloatParameter attenuation = new ClampedFloatParameter(0.85f, 0.1f, 0.99f);

        [Tooltip("Renders the streak buffer at a fraction of screen resolution. Higher = cheaper and slightly softer/chunkier.")]
        public ClampedIntParameter downsample = new ClampedIntParameter(2, 1, 8);

        [Header("Look")]
        [Tooltip("Overall streak strength.")]
        public MinFloatParameter intensity = new MinFloatParameter(1.0f, 0f);

        [Tooltip("Tints the streak color. Alpha is ignored.")]
        public ColorParameter tint = new ColorParameter(Color.white, true, false, true);

        [Tooltip("Separates the streak's R/G/B slightly along the streak direction, like cheap old lens/tube chromatic fringing.")]
        public ClampedFloatParameter chromaticFringe = new ClampedFloatParameter(0.4f, 0f, 4f);

        public bool IsActive() => enabled.value && intensity.value > 0f;

        public bool IsTileCompatible() => false;
    }
}

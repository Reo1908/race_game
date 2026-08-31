using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RetroFX
{
    /// <summary>
    /// Volume override for the "Anime Bloom" effect - a thick, chunky bloom
    /// reminiscent of 80s/90s anime (Patlabor, Bubblegum Crisis, etc).
    /// Add this as an override on a Volume Profile, then add
    /// AnimeBloomRendererFeature to your URP Renderer asset.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/RetroFX/Anime Bloom")]
    public class AnimeBloomVolume : VolumeComponent, IPostProcessComponent
    {
        [Tooltip("Master enable for the effect.")]
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Threshold")]
        [Tooltip("Brightness level (in linear HDR units) above which pixels start contributing to bloom.")]
        public ClampedFloatParameter threshold = new ClampedFloatParameter(1.0f, 0f, 5f);

        [Tooltip("Softens the threshold cutoff so the transition isn't a hard edge. 0 = hard cutoff.")]
        public ClampedFloatParameter knee = new ClampedFloatParameter(0.5f, 0f, 2f);

        [Tooltip("Clamps extracted bright pixels so single very bright pixels (fireflies) don't blow the bloom out.")]
        public ClampedFloatParameter clamp = new ClampedFloatParameter(12f, 0f, 100f);

        [Header("Shape / Thickness")]
        [Tooltip("Renders the bloom at a fraction of screen resolution. Higher values = chunkier, thicker, more 'retro' bloom blobs. 1 = full res, 8 = very blocky.")]
        public ClampedIntParameter downsample = new ClampedIntParameter(4, 1, 8);

        [Tooltip("Number of blur passes. More passes = softer/wider spread of the bloom mass.")]
        public ClampedIntParameter iterations = new ClampedIntParameter(3, 1, 8);

        [Tooltip("Distance (in texels) each blur pass samples. Bigger = wider, thicker bloom.")]
        public ClampedFloatParameter blurSpread = new ClampedFloatParameter(1.5f, 0.1f, 6f);

        [Tooltip("Stretches the bloom horizontally/vertically. X>1 widens bloom sideways, Y>1 stretches it vertically - handy for that streaky anime highlight look.")]
        public Vector2Parameter stretch = new Vector2Parameter(new Vector2(1f, 1f));

        [Header("Look")]
        [Tooltip("Overall bloom strength.")]
        public MinFloatParameter intensity = new MinFloatParameter(1.5f, 0f);

        [Tooltip("Tints the bloom color. Alpha is ignored.")]
        public ColorParameter tint = new ColorParameter(Color.white, true, false, true);

        [Tooltip("Saturation of the bloom itself, independent of the scene. 0 = monochrome bloom (very old-anime), 1 = natural, >1 = oversaturated neon.")]
        public ClampedFloatParameter saturation = new ClampedFloatParameter(1.1f, 0f, 3f);

        [Tooltip("Blends between adding bloom on top (0) and a soft 'glow wash' that also lightens midtones slightly (1), like an optical diffusion filter.")]
        [Range(0f, 1f)]
        public ClampedFloatParameter diffusion = new ClampedFloatParameter(0f, 0f, 1f);

        public bool IsActive() => enabled.value && intensity.value > 0f;

        public bool IsTileCompatible() => false;
    }
}

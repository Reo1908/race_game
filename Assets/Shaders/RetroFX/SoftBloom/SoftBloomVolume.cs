using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RetroFX
{
    /// <summary>How the Diffusion layer's blurred copy blends with the sharp frame.</summary>
    public enum SoftBloomBlendMode
    {
        /// <summary>Standard cross-fade between sharp and blurred - a true "double exposure" blend.</summary>
        Normal,
        /// <summary>Adds the blurred copy on top - brighter/glowier, can blow out highlights fast.</summary>
        Additive,
        /// <summary>Screen blend - brightens without fully blowing out, a softer glow than Additive.</summary>
        Screen
    }

    [Serializable]
    public sealed class SoftBloomBlendModeParameter : VolumeParameter<SoftBloomBlendMode>
    {
        public SoftBloomBlendModeParameter(SoftBloomBlendMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    /// <summary>
    /// Two-layer "soft focus" bloom:
    ///   1. Diffusion - blurs the WHOLE image (not just bright spots) and
    ///      blends it with the sharp frame, like a double-exposed,
    ///      deliberately-defocused second shot on old film. This is what
    ///      gives that hazy, soft old-anime look rather than a modern
    ///      thresholded bloom.
    ///   2. Highlight - a separate, normal thresholded bloom layered on
    ///      top, so your brightest lights/emissives still pop instead of
    ///      getting lost in the soft haze.
    /// Both layers use a proper downsample/upsample blur pyramid (not a
    /// handful of blur taps), which is what avoids the "doubled image"
    /// ghosting look.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/RetroFX/Soft Bloom")]
    public class SoftBloomVolume : VolumeComponent, IPostProcessComponent
    {
        [Tooltip("Master enable for the effect.")]
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Diffusion (whole-image defocus)")]
        [Tooltip("Brightness level (linear HDR) below which this layer barely contributes. Default 0 means the whole image blurs, same as a plain double exposure - raise it if the effect feels too strong/washes out darks and midtones too.")]
        public ClampedFloatParameter diffusionThreshold = new ClampedFloatParameter(0f, 0f, 3f);

        [Tooltip("Softens the diffusion threshold cutoff (only matters if Diffusion Threshold is above 0).")]
        public ClampedFloatParameter diffusionKnee = new ClampedFloatParameter(0.3f, 0f, 2f);

        [Tooltip("Caps how bright a single pixel can get before entering the diffusion blur, so a single very bright spot doesn't wash out the whole frame.")]
        public ClampedFloatParameter diffusionClamp = new ClampedFloatParameter(20f, 0f, 100f);

        [Tooltip("How much of the blurred/defocused copy to blend in. 0.5 matches a true 50/50 double exposure. Higher = hazier/softer overall image.")]
        public ClampedFloatParameter diffusionOpacity = new ClampedFloatParameter(0.5f, 0f, 1f);

        [Tooltip("Saturation of the blurred copy itself, independent of the sharp image underneath. 0 = monochrome haze, 1 = natural, >1 = oversaturated.")]
        public ClampedFloatParameter diffusionSaturation = new ClampedFloatParameter(1f, 0f, 3f);

        [Tooltip("How the blurred copy combines with the sharp frame. Normal = a true cross-fade (closest to real double exposure). Additive/Screen brighten the image more and can look glowier or blown out.")]
        public SoftBloomBlendModeParameter diffusionBlendMode = new SoftBloomBlendModeParameter(SoftBloomBlendMode.Normal);

        [Tooltip("Renders the diffusion blur starting at 1/Nth resolution before entering the blur pyramid. Bigger = cheaper and a bit softer.")]
        public ClampedIntParameter diffusionDownsample = new ClampedIntParameter(2, 1, 8);

        [Tooltip("Depth of the blur pyramid. More = a wider, softer, more 'out of focus' spread across the whole frame. Try 5-7 for a strong defocus look.")]
        public ClampedIntParameter diffusionIterations = new ClampedIntParameter(6, 2, 8);

        [Tooltip("Continuously scales how wide each blur step reaches, on top of Iterations - use this for fine 'how diffused does it look' tuning without changing the number of blur passes. 1 = normal, higher = softer/wider, lower = tighter.")]
        public ClampedFloatParameter diffusionSpread = new ClampedFloatParameter(1f, 0.25f, 3f);

        [Header("Highlight Bloom (thresholded, separate layer)")]
        [Tooltip("Brightness level (linear HDR) above which pixels count as a 'highlight' for this second layer.")]
        public ClampedFloatParameter threshold = new ClampedFloatParameter(1.2f, 0f, 5f);

        [Tooltip("Softens the threshold cutoff.")]
        public ClampedFloatParameter knee = new ClampedFloatParameter(0.4f, 0f, 2f);

        [Tooltip("Clamps extracted bright pixels before blurring so single fireflies don't dominate.")]
        public ClampedFloatParameter clamp = new ClampedFloatParameter(12f, 0f, 100f);

        [Tooltip("Resolution divisor before entering the highlight blur pyramid.")]
        public ClampedIntParameter highlightDownsample = new ClampedIntParameter(2, 1, 8);

        [Tooltip("Depth of the highlight blur pyramid. Lower than Diffusion Iterations usually looks right - a tighter, more defined glow rather than a full-screen haze.")]
        public ClampedIntParameter highlightIterations = new ClampedIntParameter(4, 2, 8);

        [Tooltip("Continuously scales how wide each blur step reaches, on top of Iterations - fine 'how diffused' tuning for the highlight glow specifically.")]
        public ClampedFloatParameter highlightSpread = new ClampedFloatParameter(1f, 0.25f, 3f);

        [Tooltip("Strength of the highlight glow, added on top of the diffusion layer.")]
        public MinFloatParameter highlightIntensity = new MinFloatParameter(2f, 0f);

        [Tooltip("Tints the highlight glow. Alpha is ignored.")]
        public ColorParameter highlightTint = new ColorParameter(Color.white, true, false, true);

        [Tooltip("Saturation of the highlight glow itself. 0 = monochrome glow, 1 = natural, >1 = neon.")]
        public ClampedFloatParameter highlightSaturation = new ClampedFloatParameter(1.1f, 0f, 3f);

        public bool IsActive() => enabled.value && (diffusionOpacity.value > 0f || highlightIntensity.value > 0f);

        public bool IsTileCompatible() => false;
    }
}

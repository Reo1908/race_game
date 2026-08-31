# RetroFX — retro post-process shaders (URP / Unity 6)

Post-process effects built as URP **Renderer Features**, controlled through
normal **Volume** sliders. Written against Unity 6's Render Graph API (the
default rendering path in Unity 6 URP), not Shader Graph — so everything is
plain, editable HLSL/C#.

## What's in here

```
RetroFX/
  SoftBloom/                        <- USE THIS for the anime/Patlabor look
    SoftBloomVolume.cs
    SoftBloomRendererFeature.cs
    SoftBloom.shader
  PhosphorTrail/                    <- USE THIS for tube-camera light trails
    PhosphorTrailVolume.cs
    PhosphorTrailRendererFeature.cs
    PhosphorTrail.shader
  AnimeBloom/                       <- older, more "videogame" style bloom
    AnimeBloomVolume.cs             (kept in case you want it after all)
    AnimeBloomRendererFeature.cs
    AnimeBloom.shader
  CameraStreak/                     <- older, lens-flare style streak
    CameraStreakVolume.cs           (a same-frame directional blur, not
    CameraStreakRendererFeature.cs   a real cross-frame trail)
    CameraStreak.shader
  TestTint/                         <- minimal always-on test shader, no
    TestTintRendererFeature.cs         Volume needed. Good for sanity-
    TestTint.shader                    checking your renderer setup.
```

**SoftBloom** and **PhosphorTrail** are the current versions matching the
"double-exposure defocus" and "tube camera image persistence" references.
AnimeBloom and CameraStreak are the earlier attempts, left in for reference/
comparison — you can ignore or delete their folders if you don't want them.

## Install

1. Copy the whole `RetroFX` folder into your project's `Assets/` folder.
2. Select your **URP Renderer asset** → `Add Renderer Feature` →
   **Soft Bloom Renderer Feature** and/or **Phosphor Trail Renderer Feature**.
3. Leave the `Shader` field empty — it auto-finds the shader by name — or
   drag the matching `.shader` file in manually if it doesn't resolve.
4. On your scene's **Volume**, `Add Override` → `Post-processing > RetroFX >
   Soft Bloom` and/or `Phosphor Trail`. Tick **Enabled**.
5. **Important**: for every override field you want active, make sure BOTH
   checkboxes next to it are ticked — the small one on the far left (which
   says "this Volume overrides this value") AND the value itself. It's easy
   to tick one and miss the other, which makes the effect silently do
   nothing with no errors.
6. Make sure your **URP Asset** has **HDR** turned on and your camera has
   **Post Processing** enabled — both effects work on HDR linear values
   above 1.0, so without HDR nothing will ever cross the threshold.

If you're already using URP's built-in Bloom, either disable it or push its
threshold high so you're not looking at two blooms stacked and can't tell
which is which.

## Soft Bloom — what each slider does

This is two independent layers blended together:

**Diffusion** (the actual "double exposure" effect) — blurs the *entire*
image, not just bright spots, and blends that blurred copy back with the
sharp frame. This is what gives the soft, hazy, old-film look, rather than
a modern thresholded bloom.
- **Diffusion Amount** — blend between sharp and blurred. 0.5 matches a true
  50/50 double exposure. Push higher for a hazier, dreamier frame.
- **Diffusion Downsample** — starting resolution divisor before the blur.
  Mostly a performance knob.
- **Diffusion Iterations** — depth of the blur pyramid = how wide/soft the
  defocus spreads. 5–7 gives a strong "lens racked out of focus" look.

**Highlight Bloom** (separate, thresholded layer on top) — a normal bloom
that only picks out your brightest lights/emissives, so they still read as
distinctly bright instead of getting lost in the soft haze.
- **Threshold / Knee / Clamp** — brightness cutoff, cutoff softness, and a
  cap so single fireflies don't dominate.
- **Highlight Downsample / Iterations** — same idea as the diffusion layer,
  independent settings. Usually wants fewer iterations than Diffusion for a
  tighter, more defined glow rather than a full-screen wash.
- **Highlight Intensity / Tint / Saturation** — strength, color, and how
  monochrome (0) vs neon (>1) the glow itself reads.

Both layers use a proper downsample/upsample blur pyramid (the same
technique modern engine blooms use), not a handful of blur taps — that's
specifically what avoids the "doubled ghost image" look.

## Phosphor Trail — what each slider does

Unlike Soft Bloom, this reads a small buffer that **persists across
frames** — so it only shows a visible trail on things that *move* relative
to the screen (headlights, taillights, muzzle flashes sweeping past). A
static bright light won't show a trail, same as the real vidicon-tube
effect it's emulating.

- **Threshold / Knee / Clamp** — same idea as bloom: what counts as bright
  enough to leave a trail.
- **Persistence** — how much of the trail survives frame to frame, roughly
  normalized to 60fps so it won't change drastically at other framerates.
  Close to 1 = long, slow-fading trails. Lower = short, quick trails.
- **Downsample** — resolution divisor for the trail buffer. Higher also
  helps sell the "cheap tube camera" look, not just performance.
- **Softness** — a small blur applied to the trail only for *display* each
  frame — it's not fed back into the history buffer, so it won't compound
  into runaway blur over time. Raise this if the trail looks too sharp/
  pixelated for your taste.
- **Intensity / Tint** — strength and color. Real tube cameras often skewed
  slightly green or amber depending on phosphor type — worth trying instead
  of pure white for extra character.

A static bright light won't add any extra glow on top of itself, no matter
how high Intensity is set — the effect only ever shows the leftover trail
*after* a light has moved away from a spot, so it can't compound into
looking like a second bloom layer. If you want a bright static light to
also glow, that's what Soft Bloom's Highlight layer is for.

## Ordering multiple features

If you use more than one of these together, put **Phosphor Trail** and/or
**Camera Streak** above **Soft Bloom** / **Anime Bloom** in the Renderer
Features list, so the bloom pass also catches some of that energy instead
of it sitting sharply on top of an already-bloomed image. Try both orders —
it's a taste call.

## Notes / caveats

- This is written for **Unity 6's Render Graph** path (the default). If your
  project has **Compatibility Mode (Render Graph disabled)** turned on in
  the URP Renderer settings, these passes won't run — either switch that
  off, or ask for a compatibility-mode version using the older
  `Execute`/`RTHandle` API.
- All effects run in HDR linear space **before** URP's own post-processing
  stack (`BeforeRenderingPostProcessing`), so your color grading/tonemapper
  still runs on top and will affect the final look.
- Phosphor Trail keeps one small persistent buffer per camera instance
  (shared across the feature, not per-camera-indexed) — if you're doing
  split-screen or multiple simultaneous cameras with this feature active on
  more than one, mention it and the buffer handling needs to be made
  per-camera.
- I wrote and reasoned through all of this carefully against the Unity 6 URP
  RenderGraph API, but I can't compile it in this environment (no Unity
  install / no network access here) — so treat it as a strong first pass.
  If the compiler flags a mismatched method signature on your exact Unity 6
  patch version, paste the error and it can be fixed fast.

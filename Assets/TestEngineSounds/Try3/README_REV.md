# REV granular engine (Unity 6.5)

Granular engine audio in the style of Crankcase REV: the four recordings are **scrubbed by
RPM** instead of being pitch-shifted, so the recorded pitch profile - and with it the
character and the perceived power of the engine - survives intact.

## Files

| File | What it is |
|---|---|
| `RevGranularEngine.cs` | the component you put on the car. All settings, the public API, the directional mix |
| `RevEngineCore.cs` | the DSP. No MonoBehaviour, no allocations, no locks. Used live and for baking |
| `RevParams.cs` | the parameter snapshot handed from the game thread to the audio thread |
| `RevSampleLayer.cs` / `RevLayerRuntime.cs` | one recording: playhead range, RPM mapping, decoded samples |
| `RevGrainWindow.cs` | grain envelope shapes, baked into a lookup table |
| `RevModulation.cs` | modulation matrix, sources, targets, LFOs |
| `RevFilters.cs` | biquads, DC blocker, noise gate |
| `Editor/RevGranularEngineEditor.cs` | inspector, live preview, meters |
| `Editor/RevSampleAnalyser.cs` | RPM detection (autocorrelation pitch tracking) |
| `Editor/RevBaker.cs` | offline render to WAV |
| `Editor/RevMinMaxRangeDrawer.cs` | the range sliders |

## Setup

1. Put `RevGranularEngine` on the car (it adds an `AudioSource` itself).
2. Assign the four clips: **engine up**, **engine down**, **exhaust up**, **exhaust down**.
   Set each clip's importer to **Decompress On Load** - the engine reads raw samples once
   at load and cannot read compressed-in-memory or streaming clips.
3. Set **RPM group → Rpm Range** to your idle and redline, and the **cylinder count**.
4. Press **Detect RPM in all assigned clips**. That analyses each recording, works out the
   RPM at every position, and fills in the mapping. Check the search range covers your
   engine (default 500 - 9500).
5. Drag each layer's **Playhead Range** so the playhead stays out of the junk at the head
   and tail of the recording. The sample itself is *not* trimmed: grains may still read
   past the range, so nothing is chopped off mid-grain.
6. Hit **Play** in the Live preview box and drag the RPM slider, or turn on **Auto sweep**.

## Driving it from the car

```csharp
[SerializeField] RevAudio.RevGranularEngine engineAudio;

void Update()
{
    engineAudio.SetEngineState(currentRpm, throttle01, load01);
    // or: engineAudio.rpm = currentRpm;  engineAudio.NormalisedRpm = 0..1;
    // rev limiter cut:  engineAudio.ignition = limiterActive ? 0.2f : 1f;
}
```

Write the values at whatever rate you like. The DSP re-evaluates RPM, modulation, grain
rate and grain size every **32 samples** (about 1.5 kHz at 48 kHz - adjustable via
*Control Block Samples*), and places grain onsets at sample accuracy, so a fast sweep stays
smooth instead of stepping once per rendered frame. Big jumps (gearshifts, respawns) skip
the ramp entirely - see *Rpm Snap Delta*.

## The parts that matter

**Anti-phasing.** Overlapping grains taken from almost the same spot are near-copies of each
other, and summing them comb-filters the sound. With anti-phasing on, every read offset and
(optionally) every grain length is quantised to a whole **firing cycle**
(`firing Hz = RPM * cylinders / 120`, four-stroke), so overlapping grains superimpose in
phase instead. This needs the RPM group, which is where the cylinder count lives.

**RPM difference resampling.** The playhead lands on the closest recorded RPM; resampling
covers whatever is left over, and extends the recording below idle and past the redline
(clamped by *Max Resample Semitones*).

**Directional mix.** Front/back is `dot(car forward, direction to listener)`. It crossfades
engine against exhaust with an equal-power law, never below *Min Side Level*. On top of that
sits *Both Layer Level*: a fixed amount of both sides that is always audible regardless of
angle. If one side has no clips assigned, its share is folded into the other side.

**Baking.** *Sweep* renders one continuous idle-to-redline (and back) WAV. *Steady state set*
renders one seamless loop per RPM step, with the loop length rounded to a whole number of
firing cycles and the tail wrapped onto the head - for a cheap sample-player fallback on
low-end targets or for distant cars.

## Performance

- Grain rendering is grain-major: each grain writes its own span of the block, so the cost
  is `active grains x grain length`, not `pool size x block`.
- Nothing allocates on the audio thread. Parameters cross over as one atomic reference swap
  through three rotating buffers.
- The window, the modulation curves and the RPM tables are all baked to lookup tables on
  the main thread; the audio thread never touches an `AnimationCurve`.
- Watch the **DSP load** meter in the inspector. If it climbs: lower *Overlap Range*, turn
  off *Hermite Interpolation*, or raise *Control Block Samples*.
- Memory: each clip is decoded to float once (`length x rate x channels x 4` bytes).
  *Force Mono* halves it, and halves the per-sample read cost too.

## Testing

**Menu: `Tools → REV → Create test scene`.** It builds `REV_TestScene.unity` next to these
scripts: a car body (blue nose = engine side / +Z, orange tailpipe = exhaust side / -Z), a
ground plane, and the listener on an orbit rig. It matches the four clips out of the project
by file name, guesses the cylinder count from the name, and offers to prepare them (Load Type
→ Decompress On Load, then RPM detection on all four, and the RPM range set from what it
found).

Press Play and you get two panels:

- **left** - drive mode (free rev / gearbox / manual RPM), throttle, gear, and live toggles
  for anti-phasing, RPM difference resampling, gate, lowpass and the both-sides layer, plus
  DSP meters (load, peak, grain count, rate, size, overlap, playhead per layer).
- **right** - listener angle, distance, and an auto-orbit toggle.

Keyboard (when the old input backend is enabled, which it is here): `W`/`Space` throttle,
`S` off throttle, `Q`/`E` shift, `M` mode, `Tab` anti-phasing on/off.

What to listen for:

| Test | How |
|---|---|
| Does the RPM mapping fit? | Manual RPM mode, sweep slowly. The pitch should rise evenly, no jumps or plateaus. If it lurches, the detected profile or the playhead range is off. |
| Anti-phasing | Hold a steady RPM and toggle `Tab`. Off = hollow, flanged, comb-filtered. On = solid. |
| Fast RPM changes | Gearbox mode with auto shift. Every shift is a hard RPM drop plus an ignition cut - it must stay clean. |
| Accel vs decel | Free rev: full throttle up, then release. The down-sweep recordings should fade in on overrun. |
| Directional mix | Auto-orbit on. Front should be engine-heavy, rear exhaust-heavy. Drag *Both layer* to 0 to hear the crossfade on its own. |
| Cost | Watch the DSP load meter while sweeping - it peaks where the overlap peaks. |

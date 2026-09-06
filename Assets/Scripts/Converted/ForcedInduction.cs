using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simulates one or more forced-induction units (turbochargers and/or superchargers)
/// bolted onto a VehicleEngine. Reads RPM and throttle straight from VehicleEngine every
/// FixedUpdate (no separate input wiring needed), drives each module's own internal spool
/// state, and reports each module's torque contribution back to the engine via
/// VehicleEngine.RegisterForcedInductionTorqueProvider — see that method's comment for why
/// it's pull-based rather than the other way around.
///
/// Two module types, both configured in the same expandable list:
///   - Supercharger: belt/gear-driven directly off the engine, so its own "RPM" tracks
///     engine RPM immediately, no lag.
///   - Turbo: exhaust-driven, so it only starts building boost once engine RPM clears
///     MinEngineRPM, and then ramps its own RPM up/down over SpoolUpTime/SpoolDownTime —
///     that ramp is the lag/spool-up feel.
///
/// Each module has its own whine/spin sound (pitch and volume both follow the module's
/// own normalized RPM through one shared response curve, then get mapped into your min/max
/// pitch and volume ranges) and its own blow-off/dump-valve sound, which fires with a
/// randomized pitch (range you set) and a fixed adjustable volume whenever throttle is
/// lifted hard while the module is under boost.
/// </summary>
[RequireComponent(typeof(VehicleEngine))]
public class ForcedInduction : MonoBehaviour
{
    public enum InductionType { Turbocharger, Supercharger }

    [Serializable]
    public class ForcedInductionModule
    {
        [Header("Identity")]
        public string moduleName = "New Module";
        public InductionType type = InductionType.Turbocharger;
        public bool isEnabled = true;

        [Header("Turbo Only — spool behaviour")]
        [Tooltip("Engine RPM has to be at or above this before the turbo builds any boost at all — exhaust flow below this is assumed too weak to spin the turbine.")]
        public float minEngineRPM = 2500f;
        [Tooltip("Throttle has to be at or above this for the turbo to be getting exhaust flow at all. Without this, coasting or engine-braking at high RPM with the throttle closed would still read as 'spooled' — RPM alone isn't exhaust flow, the throttle plate has to actually be open.")]
        [Range(0f, 1f)] public float minThrottleForSpool = 0.05f;
        [Tooltip("Seconds for this turbo to go from 0 to full spool once conditions allow — this is the lag you feel as boost builds.")]
        public float spoolUpTime = 1.2f;
        [Tooltip("Seconds for spool to fall back to 0 once engine RPM drops back under MinEngineRPM, throttle closes, or ignition cuts. Turbines keep spinning a moment after exhaust flow drops, so this is usually shorter than SpoolUpTime — set it low (even near 0) if you want the sound to cut almost instantly on lift.")]
        public float spoolDownTime = 0.6f;

        [Header("Supercharger Only — drive ratio")]
        [Tooltip("How many times faster the supercharger spins than the engine (belt/gear driven). Only used to compute the display RPM below — normalized boost still follows engine RPM / redline directly, with no lag, per the brief.")]
        public float drivePulleyRatio = 2.5f;

        [Header("Boost & Torque")]
        [Tooltip("Purely cosmetic ceiling for CurrentModuleRPM (e.g. a turbo's turbine genuinely spins into six figures) — everything that actually drives torque/sound uses the 0-1 normalized value, this just gives you a realistic number for a boost gauge / UI.")]
        public float moduleMaxRPM = 120000f;
        [Tooltip("Torque multiplier vs. this module's own normalized RPM (0 = no spool, 1 = fully spooled). Typical shape: flat near zero at low spool, rising sharply once it's spun up.")]
        public AnimationCurve torqueCurve = BuildDefaultTorqueCurve();
        [Tooltip("Torque this module contributes to VehicleEngine at full spool and full throttle (same units as VehicleEngine's Engine Max Torque).")]
        public float maxTorque = 40f;

        [Header("Spin / Whine Sound")]
        [Tooltip("Looping whine/spin sound for this module. Left empty = no spin sound for this module.")]
        public AudioSource spinAudioSource;
        [Tooltip("Shapes how pitch AND volume ramp in together against this module's own normalized RPM (0-1 in, 0-1 out) — the single curve the brief asked for. The 0-1 output is then mapped into the pitch and volume ranges below.")]
        public AnimationCurve spinResponseCurve = BuildDefaultResponseCurve();
        public float spinMinPitch = 0.8f;
        public float spinMaxPitch = 2.2f;
        [Range(0f, 1f)] public float spinMinVolume = 0f;
        [Range(0f, 1f)] public float spinMaxVolume = 1f;

        [Header("Blow-Off / Dump Valve Sound")]
        [Tooltip("One-shot clip played on a hard throttle lift while this module is under boost.")]
        public AudioClip blowoffClip;
        [Tooltip("AudioSource used to fire the one-shot (PlayOneShot won't fight the spin sound if this is a separate source).")]
        public AudioSource blowoffAudioSource;
        [Tooltip("Randomized pitch range — a new random pitch in this range is picked every time the valve fires.")]
        public float blowoffMinPitch = 0.9f;
        public float blowoffMaxPitch = 1.1f;
        [Range(0f, 1f)] public float blowoffVolume = 1f;
        [Tooltip("How much throttle has to drop within one FixedUpdate step to count as a 'lift' that can trigger the valve.")]
        [Range(0f, 1f)] public float blowoffThrottleDropThreshold = 0.35f;
        [Tooltip("Minimum normalized spool (0-1) required for a throttle lift to actually trigger the valve — no point venting a boost pressure that isn't there.")]
        [Range(0f, 1f)] public float blowoffMinBoostToTrigger = 0.3f;
        [Tooltip("Minimum seconds between valve triggers, so a jittery throttle can't machine-gun the sound.")]
        public float blowoffCooldown = 0.4f;

        // ---- Runtime state (not shown/edited in the inspector) ----
        [NonSerialized] public float CurrentModuleRPM01;   // 0-1 normalized spool
        [NonSerialized] public float CurrentModuleRPM;     // cosmetic units, = CurrentModuleRPM01 * moduleMaxRPM
        [NonSerialized] public float CurrentTorque;
        private float previousThrottle = -1f;
        private float blowoffCooldownRemaining;

        /// <summary>Advances this module's spool state by dt, derives its torque
        /// contribution, and drives its two sound slots. Called once per VehicleEngine
        /// FixedUpdate — see ForcedInduction.GetCombinedTorque.</summary>
        public void Tick(float engineRPM, float throttle, float engineRedline, bool ignitionOn, float dt)
        {
            if (!isEnabled || !ignitionOn)
            {
                // Ignition cut (mid-shift, rev limiter) means no combustion, which means
                // no exhaust flow and no belt drive from a spinning crank either — so both
                // module types fall back to their normal "no exhaust flow" / "no drive"
                // path below rather than a separate branch, keeping the decay behaviour
                // (spoolDownTime, sound fade) identical to lifting off the throttle.
                CurrentModuleRPM01 = Mathf.MoveTowards(CurrentModuleRPM01, 0f, dt / Mathf.Max(0.01f, spoolDownTime));
                CurrentModuleRPM = CurrentModuleRPM01 * moduleMaxRPM;
                CurrentTorque = 0f;
                ApplySpinSound(CurrentModuleRPM01);
                UpdateBlowoff(0f, dt);
                previousThrottle = 0f;
                return;
            }

            if (type == InductionType.Supercharger)
            {
                // Mechanically linked to the engine, so it's instantaneous — "scales with
                // engine RPM normally" per the brief, no ramp.
                CurrentModuleRPM01 = Mathf.Clamp01(engineRPM / Mathf.Max(1f, engineRedline));
            }
            else // Turbocharger
            {
                // Exhaust flow needs actual combustion, not just RPM — a closed throttle
                // at high RPM (coasting, engine braking) makes no exhaust gas even though
                // the engine's still spinning fast. Without the throttle check here, lifting
                // off at high RPM wouldn't start the spool-down until RPM itself fell, which
                // is why the boost sound used to hang on well past when you lifted.
                bool exhaustFlowSufficient = engineRPM >= minEngineRPM && throttle >= minThrottleForSpool;
                float target = exhaustFlowSufficient ? 1f : 0f;
                float rampTime = target > CurrentModuleRPM01 ? spoolUpTime : spoolDownTime;
                float rate = 1f / Mathf.Max(0.01f, rampTime);
                CurrentModuleRPM01 = Mathf.MoveTowards(CurrentModuleRPM01, target, rate * dt);
            }

            CurrentModuleRPM = CurrentModuleRPM01 * moduleMaxRPM;

            // Boost pressure only turns into power with the throttle plate open — the
            // module can be fully spooled and silent on power delivery at 0 throttle,
            // same as a real boost gauge reading full boost with your foot off the pedal
            // on a trailing-throttle overrun.
            CurrentTorque = maxTorque * torqueCurve.Evaluate(CurrentModuleRPM01) * throttle;

            ApplySpinSound(CurrentModuleRPM01);
            UpdateBlowoff(throttle, dt);
            previousThrottle = throttle;
        }

        private void ApplySpinSound(float normalized)
        {
            if (spinAudioSource == null) return;
            float response = Mathf.Clamp01(spinResponseCurve.Evaluate(Mathf.Clamp01(normalized)));
            spinAudioSource.pitch = Mathf.Lerp(spinMinPitch, spinMaxPitch, response);
            spinAudioSource.volume = Mathf.Lerp(spinMinVolume, spinMaxVolume, response);
            if (!spinAudioSource.isPlaying)
                spinAudioSource.Play();
        }

        private void UpdateBlowoff(float throttle, float dt)
        {
            blowoffCooldownRemaining = Mathf.Max(0f, blowoffCooldownRemaining - dt);

            if (previousThrottle < 0f)
                return; // first frame — nothing to compare against yet

            float throttleDrop = previousThrottle - throttle;
            bool liftedHard = throttleDrop >= blowoffThrottleDropThreshold;
            bool underBoost = CurrentModuleRPM01 >= blowoffMinBoostToTrigger;

            if (liftedHard && underBoost && blowoffCooldownRemaining <= 0f)
            {
                if (blowoffAudioSource != null && blowoffClip != null)
                {
                    blowoffAudioSource.pitch = UnityEngine.Random.Range(blowoffMinPitch, blowoffMaxPitch);
                    blowoffAudioSource.PlayOneShot(blowoffClip, blowoffVolume);
                }
                blowoffCooldownRemaining = blowoffCooldown;
            }
        }

        private static AnimationCurve BuildDefaultTorqueCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.3f, 0.05f),
                new Keyframe(0.6f, 0.35f),
                new Keyframe(0.85f, 0.8f),
                new Keyframe(1f, 1f)
            );
        }

        private static AnimationCurve BuildDefaultResponseCurve()
        {
            return AnimationCurve.Linear(0f, 0f, 1f, 1f);
        }
    }

    [Header("Modules")]
    [Tooltip("Expandable — add as many turbos and/or superchargers as you want, in any mix. Each is fully independent (own spool behaviour, own sound, own torque).")]
    [SerializeField] private List<ForcedInductionModule> modules = new List<ForcedInductionModule>();

    [Header("Audio Setup")]
    [Tooltip("When you add a new module to the list above (in the editor), automatically create its Spin and Blowoff AudioSources for you instead of requiring you to wire them up by hand. Only fires for slots that are still empty, and only in the editor when a module is added — never at runtime, never just from entering Play Mode.")]
    [SerializeField] private bool autoCreateAudioSources = true;
    [Tooltip("Name of the child GameObject all auto-created audio sources are grouped under, so they don't clutter the root object's hierarchy.")]
    [SerializeField] private string audioFolderName = "Forced Induction Audio";

    [Header("Debug")]
    [Tooltip("Shown on the left side of the screen in Play Mode (VehicleEngine's own overlay uses the right side).")]
    [SerializeField] private bool showDebugOverlay = false;

    // Tracks how many modules existed last time OnValidate ran, purely so we can tell
    // "a module was just added" apart from "a field on an existing module changed" —
    // serialized so it survives domain reloads and still reads correctly after a script
    // recompile. Not meant to be touched by hand.
    [SerializeField, HideInInspector] private int lastKnownModuleCount;

    /// <summary>Read-only view of the configured modules, e.g. for a boost-gauge UI.</summary>
    public IReadOnlyList<ForcedInductionModule> Modules => modules;

    private VehicleEngine engine;
    private Transform audioFolder;

    private void Awake()
    {
        engine = GetComponent<VehicleEngine>();
    }

#if UNITY_EDITOR
    // Editor-only: fires whenever this component's serialized data changes in the
    // inspector — including list-size changes, which is how we detect "a module was
    // added" without needing a custom PropertyDrawer. Deliberately does nothing at
    // runtime (guarded by Application.isPlaying) — hierarchy edits here are an
    // edit-time convenience, not something that should happen on hitting Play or in a
    // build.
    private void OnValidate()
    {
        if (Application.isPlaying || !autoCreateAudioSources)
        {
            lastKnownModuleCount = modules.Count;
            return;
        }

        if (modules.Count > lastKnownModuleCount)
        {
            int firstNewIndex = lastKnownModuleCount;
            int newModuleCount = modules.Count;
            // OnValidate runs mid-serialization, so creating/parenting GameObjects here
            // directly can warn or get silently dropped by Unity. Defer one editor tick
            // and do the actual hierarchy work then.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null) return; // component/object may be gone by the time this runs
                for (int i = firstNewIndex; i < newModuleCount && i < modules.Count; i++)
                    EnsureAudioSourcesForModule(i);
            };
        }

        lastKnownModuleCount = modules.Count;
    }
#endif

    /// <summary>
    /// Creates an AudioSource for the given module's Spin and/or Blowoff slot if it's
    /// still unassigned, parented under a single "Forced Induction Audio" child (created
    /// on first use) so they're grouped together instead of scattered loose children on
    /// the vehicle root. A slot you've already assigned by hand is left completely alone.
    /// </summary>
    private void EnsureAudioSourcesForModule(int index)
    {
        ForcedInductionModule module = modules[index];
        if (module == null) return;

        string label = string.IsNullOrEmpty(module.moduleName) ? $"Module {index}" : module.moduleName;

        if (module.spinAudioSource == null)
            module.spinAudioSource = CreateModuleAudioSource($"{label} - Spin", loop: true);
        if (module.blowoffAudioSource == null)
            module.blowoffAudioSource = CreateModuleAudioSource($"{label} - Blowoff", loop: false);

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    private AudioSource CreateModuleAudioSource(string childName, bool loop)
    {
        if (audioFolder == null)
        {
            // Reuse an existing folder if one's already sitting under this object (e.g.
            // from an earlier module) instead of stacking duplicates.
            Transform existing = transform.Find(audioFolderName);
            audioFolder = existing != null ? existing : new GameObject(audioFolderName).transform;
            audioFolder.SetParent(transform, false);
#if UNITY_EDITOR
            UnityEditor.Undo.RegisterCreatedObjectUndo(audioFolder.gameObject, "Create Forced Induction Audio Folder");
#endif
        }

        GameObject sourceObject = new GameObject(childName);
        sourceObject.transform.SetParent(audioFolder, false);

        AudioSource source = sourceObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = loop;

#if UNITY_EDITOR
        UnityEditor.Undo.RegisterCreatedObjectUndo(sourceObject, "Create Forced Induction Audio Source");
#endif
        return source;
    }


    private void OnEnable()
    {
        // See VehicleEngine.RegisterForcedInductionTorqueProvider — this hands the engine
        // a callback rather than pushing a value, so there's no dependency on whether this
        // component's Awake/FixedUpdate has run yet relative to the engine's.
        engine?.RegisterForcedInductionTorqueProvider(GetCombinedTorque);
    }

    private void OnDisable()
    {
        engine?.UnregisterForcedInductionTorqueProvider(GetCombinedTorque);
    }

    /// <summary>
    /// Called by VehicleEngine once per its own FixedUpdate. Advances every module's spool
    /// state and sound by exactly one fixed timestep, then returns the summed torque
    /// contribution. Doing the actual simulation here — inside the callback the engine
    /// invokes — rather than in this script's own FixedUpdate is what makes the whole
    /// thing execution-order-safe: this always runs precisely once per engine step,
    /// whichever component's FixedUpdate Unity happens to call first.
    /// </summary>
    private float GetCombinedTorque()
    {
        if (engine == null) return 0f;

        float dt = Time.fixedDeltaTime;
        float engineRPM = engine.CurrentRPM;
        float throttle = engine.Throttle;
        float redline = engine.EngineRedline;
        bool ignitionOn = engine.IgnitionOn;

        float total = 0f;
        for (int i = 0; i < modules.Count; i++)
        {
            ForcedInductionModule module = modules[i];
            if (module == null) continue;
            module.Tick(engineRPM, throttle, redline, ignitionOn, dt);
            total += module.CurrentTorque;
        }
        return total;
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || !Application.isPlaying) return;

        float x = 10f;
        float y = 10f;
        const float lineHeight = 18f;

        void Line(string text)
        {
            GUI.Label(new Rect(x, y, 340, 20), text);
            y += lineHeight;
        }

        Line("Forced Induction");
        for (int i = 0; i < modules.Count; i++)
        {
            ForcedInductionModule m = modules[i];
            if (m == null) continue;
            string state = m.isEnabled ? "" : " (disabled)";
            Line($"  {m.moduleName} [{m.type}]{state}: Spool {m.CurrentModuleRPM01:P0}  RPM {m.CurrentModuleRPM:F0}  Torque {m.CurrentTorque:F2}");
        }
    }
}
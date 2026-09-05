using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Converted from the "Engine &amp; Forward Movement" visual scripting graph. Only the
/// tail end of that graph actually existed there (DriveForce -> DriveForceMultiplier ->
/// Drivetrain Direction -> drift-based direction split -> AddForce/AddRelativeForce) —
/// everything upstream of "DriveForce" (RPM, torque, wheel RPM, connect/disconnect
/// logic) is a new simulation built from your written spec, since the graph didn't
/// contain it. That part is flagged clearly below wherever I made a judgment call, and
/// I fully expect some of those to need retuning once you drive it.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VehicleEngine : MonoBehaviour
{
    public enum DrivetrainType { RWD = 0, FWD = 1, AWD = 2 }

    [Header("Input")]
    [Tooltip("Wasn't specified anywhere — the graph only had the force-output tail end, nothing upstream reads a throttle. Added this so the engine has something to run on.")]
    [SerializeField] private InputActionReference throttleAction;

    [Header("Drivetrain")]
    [SerializeField] private DrivetrainType drivetrain = DrivetrainType.RWD;
    [Tooltip("Engine_Transmission_Final_Drive in your preset.")]
    [SerializeField] private float finalDrive = 3.7f;
    [Tooltip("How fast the automatic standstill clutch smooths its engagement toward the RPM-based target, in engagement-fraction per second (e.g. 4 = roughly a quarter-second to go from fully open to fully closed). Only affects that automatic launch clutch — shifting still engages/disengages via Clutch/ClutchHardness as before, untouched by this.")]
    [SerializeField] private float autoClutchEngageRate = 4f;

    [Header("Engine")]
    [Tooltip("Torque multiplier vs. normalized RPM (RPM / Redline). Pre-filled from your Variables.preset.")]
    [SerializeField] private AnimationCurve engineTorqueCurve = BuildDefaultTorqueCurve();
    [SerializeField] private float engineIdleRPM = 800f;
    [SerializeField] private float engineRedline = 8000f;
    [SerializeField] private float engineMaxTorque = 60f;
    [Tooltip("How fast the engine revs when NOT connected to the wheels (clutched or airborne) — exactly as you described it.")]
    [SerializeField] private float engineChangeRate = 1000f;
    [Tooltip("Drives throttle response lag: torque output chases its target at this rate.")]
    [SerializeField] private float engineResponse = 2200f;
    [Tooltip("Used together with Friction/Inertia below to work out engine braking force when connected and off-throttle.")]
    [SerializeField] private float engineEngineBraking = 1f;
    [SerializeField] private float engineFriction = 600f;
    [SerializeField] private float engineInertia = 150f;
    [Tooltip("Wasn't in your list, but it's in the preset and clutch-related, so I used it as the coupling strength between engine RPM and wheel-implied RPM once connected — see the comment on how it's used below. Please double-check the feel.")]
    [SerializeField] private float clutchHardness = 25f;

    [Header("Force Output (from the graph)")]
    [SerializeField] private float driveForceMultiplier = 0.0001f;
    [Tooltip("How much of the drift-portion of the force gets redirected into the world velocity direction instead of the car's relative forward.")]
    [SerializeField] private float directionalForceMultiplier = 1f;
    [SerializeField] private float transitionSpeed = 5f;
    [Tooltip("The graph only had this properly wired into one of the two AddForce calls — per your instruction, both now share this single setting.")]
    [SerializeField] private ForceMode forceMode = ForceMode.Acceleration;
    [Tooltip("The direction-blend curve baked into the graph's node (not from Variables.preset — this one lived directly in the graph itself). Pre-filled exactly as found.")]
    [SerializeField] private AnimationCurve directionBlendCurve = BuildDefaultDirectionBlendCurve();

    [Header("Drivetrain Chatter (your scale control)")]
    [Tooltip("0 = off. Everything about the chatter effect itself (a simple sine wobble on RPM) is my own addition since nothing in the graph covers this — frequency and shape are guesses, easy to change.")]
    [SerializeField] private float drivetrainChatterScale = 0f;
    [SerializeField] private float drivetrainChatterFrequency = 15f;

    [Header("Downshift Blip (optional — the \"daring\" feature)")]
    [Tooltip("While disconnected during a downshift, nudges the free-rev target up to roughly match the new gear, instead of just sagging toward idle. Turn off if it misbehaves.")]
    [SerializeField] private bool enableDownshiftBlip = true;

    [Header("Debug")]
    [Tooltip("Shown on the right side of the screen in Play Mode, so it doesn't collide with the other three scripts' overlays on the left.")]
    [SerializeField] private bool showDebugOverlay = false;
    [SerializeField] private AudioSource debugAudioSource;
    [SerializeField] private float debugMinPitch = 0.8f;
    [SerializeField] private float debugMaxPitch = 2.5f;

    /// <summary>0-1. Current RPM's position on the torque curve — how much of its
    /// available power the engine is making right now, independent of throttle.</summary>
    public float EngineOutput { get; private set; }
    public float CurrentRPM { get; private set; }

    /// <summary>RPM the rev limiter cuts at. Read-only for HUD/UI.</summary>
    public float EngineRedline { get { return engineRedline; } }

    private Rigidbody rb;
    private VehicleSuspension suspension;
    private VehicleGearbox gearbox;
    private VehicleBrakes brakes;
    private DetermineDrift determineDrift;

    private float smoothedTorque;
    private float smoothedDrift;
    private float smoothedAutoClutchFactor;
    private float previousGearRatio;
    private bool pendingDownshiftBlip;

    // Kept at class scope purely so OnGUI can show them without recomputing.
    private float debugThrottle, debugDriveForce, debugConnectionFactor, debugGroundContact;
    private float debugHandbrakeApplied, debugWheelRPM, debugWheelImpliedRPM;
    private int debugClutch;
    private bool debugIgnitionOn;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        suspension = GetComponent<VehicleSuspension>();
        gearbox = GetComponent<VehicleGearbox>();
        brakes = GetComponent<VehicleBrakes>();
        determineDrift = GetComponent<DetermineDrift>();
        CurrentRPM = engineIdleRPM;
        smoothedAutoClutchFactor = 0f;
    }

    private void OnEnable()
    {
        throttleAction?.action?.Enable();
    }

    private void OnDisable()
    {
        throttleAction?.action?.Disable();
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 velocity = rb.linearVelocity;
        Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);

        // ---- Which wheels matter for ground contact & wheel RPM ----
        float relevantCompression;
        float relevantWheelDiameter;
        switch (drivetrain)
        {
            case DrivetrainType.FWD:
                relevantCompression = suspension != null ? suspension.FrontAxleCompression : 0f;
                relevantWheelDiameter = suspension != null ? suspension.WheelDiameterFront : 0.55f;
                break;
            case DrivetrainType.AWD:
                relevantCompression = suspension != null ? suspension.AverageCompression : 0f;
                relevantWheelDiameter = suspension != null
                    ? (suspension.WheelDiameterFront + suspension.WheelDiameterRear) * 0.5f
                    : 0.55f;
                break;
            default: // RWD
                relevantCompression = suspension != null ? suspension.RearAxleCompression : 0f;
                relevantWheelDiameter = suspension != null ? suspension.WheelDiameterRear : 0.55f;
                break;
        }
        float groundContact = Mathf.Clamp01(relevantCompression);

        // ---- Simulated wheel RPM (no real wheels) ----
        // Flattened + magnitude, per your note, so drifting's lateral velocity can't
        // produce a nonsense negative/signed speed here.
        float flatSpeed = flatVelocity.magnitude;
        float wheelRPM = (flatSpeed * 60f) / (Mathf.PI * Mathf.Max(0.0001f, relevantWheelDiameter));

        float currentGearRatio = gearbox != null ? gearbox.CurrentGearRatio : 1f;
        float wheelImpliedRPM = wheelRPM * currentGearRatio * finalDrive;

        // ---- Clutch / handbrake / ground: how connected is the engine right now ----
        int clutch = gearbox != null ? gearbox.Clutch : 1;
        int gearboxIgnition = gearbox != null ? gearbox.Ignition : 1;
        // Handbrake is exposed inverted (1 = released) — un-invert it, same as VehicleBrakes does internally.
        float handbrakeApplied = brakes != null ? 1f - brakes.Handbrake : 0f;
        float connectionFactor = clutch * groundContact * (1f - handbrakeApplied);

        // Auto-clutch: without this, sitting in gear at a standstill lerps CurrentRPM
        // straight toward WheelImpliedRPM (~0), stalling it below idle. This gates on
        // the engine's OWN RPM against EngineIdleRPM, not on wheel speed — wheel speed
        // can't rise before the clutch is already transmitting some force, so gating on
        // it left the clutch unable to ever close. Target ramps from fully open right
        // at idle to fully closed at 2x idle (a reasonable guess, not from your spec);
        // AutoClutchEngageRate then smooths the actual engagement toward that target so
        // it doesn't snap instantly with every small RPM change. This only touches the
        // automatic standstill clutch — shifting still goes through Clutch/ClutchHardness
        // exactly as before.
        float autoClutchTarget = Mathf.Clamp01((CurrentRPM - engineIdleRPM) / Mathf.Max(1f, engineIdleRPM));
        smoothedAutoClutchFactor = Mathf.MoveTowards(smoothedAutoClutchFactor, autoClutchTarget, autoClutchEngageRate * dt);
        connectionFactor *= smoothedAutoClutchFactor;

        // Rev limiter folded into ignition: once at redline, ignition cuts, RPM falls
        // off friction/braking alone until it's back under redline, then it resumes —
        // gives a self-regulating bounce right at the limiter without extra timers.
        bool ignitionOn = gearboxIgnition == 1 && CurrentRPM < engineRedline;

        float throttle = throttleAction != null && throttleAction.action != null
            ? Mathf.Clamp01(throttleAction.action.ReadValue<float>())
            : 0f;
        float effectiveThrottle = ignitionOn ? throttle : 0f;

        // ---- Optional downshift blip ----
        if (enableDownshiftBlip)
        {
            if (currentGearRatio > previousGearRatio + 0.0001f)
                pendingDownshiftBlip = true;
            if (connectionFactor > 0.5f)
                pendingDownshiftBlip = false;
        }
        previousGearRatio = currentGearRatio;

        // ---- Free-rev vs. drivetrain-coupled RPM ----
        // Torque curve sampled off last frame's RPM, since this frame's new RPM is what
        // we're about to solve for. Reused below for the driveForce torque too, instead
        // of resampling the curve a second time.
        float normalizedRPM = Mathf.Clamp01(CurrentRPM / engineRedline);
        float torqueMultiplier = engineTorqueCurve.Evaluate(normalizedRPM);
        EngineOutput = torqueMultiplier;

        // Real torque/inertia model for free-revving: EngineInertia now genuinely
        // resists any change in RPM (bigger flywheel = slower to rev up AND slower to
        // fall off, exactly like added mass resists acceleration), and EngineFriction
        // is a real constant torque always opposing rotation, not a separate scalar.
        // EngineChangeRate becomes the torque-to-RPM responsiveness scale (still yours
        // to tune, still the same knob you've been running at 10000).
        // HEADS UP: with your current defaults, EngineFriction (600) dwarfs
        // EngineMaxTorque (60), so net torque is always negative and the engine will
        // sit dead at idle and never rev up at all. These two now need to be in the
        // same ballpark for the engine to move — try something like Friction ≈ 5-15%
        // of MaxTorque as a starting point and retune by feel from there.
        float driveTorque = ignitionOn ? engineMaxTorque * torqueMultiplier * throttle : 0f;
        float frictionTorque = engineFriction;
        float netFreeTorque = driveTorque - frictionTorque;
        float freeAccelRPMPerSec = (netFreeTorque / Mathf.Max(1f, engineInertia)) * engineChangeRate;
        float freeRPM = Mathf.Max(0f, CurrentRPM + freeAccelRPMPerSec * dt);
        // Idle floor kept as a simple clamp, same guarantee the old Lerp-target had —
        // holding idle via a proper idle-air-control governor isn't modeled here.
        if (ignitionOn)
            freeRPM = Mathf.Max(freeRPM, engineIdleRPM);
        if (enableDownshiftBlip && pendingDownshiftBlip)
            freeRPM = Mathf.Max(freeRPM, wheelImpliedRPM);

        float coupledRPM = Mathf.Lerp(CurrentRPM, wheelImpliedRPM, Mathf.Clamp01(clutchHardness * dt));
        float newRPM = Mathf.Lerp(freeRPM, coupledRPM, connectionFactor);

        // Drivetrain chatter — simple sine wobble, only felt while actually connected.
        if (drivetrainChatterScale > 0f)
        {
            float chatter = Mathf.Sin(Time.time * drivetrainChatterFrequency * Mathf.PI * 2f)
                             * drivetrainChatterScale * connectionFactor;
            newRPM += chatter;
        }

        CurrentRPM = Mathf.Clamp(newRPM, 0f, engineRedline);

        // ---- Torque -> DriveForce ----
        // normalizedRPM/torqueMultiplier/driveTorque already computed above for the
        // free-rev accel; driveTorque doubles as rawTorque here.
        smoothedTorque = Mathf.MoveTowards(smoothedTorque, driveTorque, engineResponse * dt);

        float engineBrakingForce = (engineEngineBraking * engineFriction * normalizedRPM * (1f - throttle)) / Mathf.Max(1f, engineInertia);
        float netTorque = smoothedTorque - engineBrakingForce;

        float driveForce = netTorque * currentGearRatio * finalDrive * connectionFactor;

        // ---- From here down: the graph's actual force-output logic, unchanged ----
        float drivetrainDirection = gearbox != null ? gearbox.DrivetrainDirection : 1f;
        float baseForce = driveForce * driveForceMultiplier * drivetrainDirection;

        float grip0Drift1 = determineDrift != null ? determineDrift.Grip0Drift1 : 0f;
        smoothedDrift = Mathf.Lerp(smoothedDrift, grip0Drift1, transitionSpeed * dt);
        float smoothedGrip = 1f - smoothedDrift;

        float relativeForwardMag = baseForce * (smoothedGrip + smoothedDrift * (1f - directionalForceMultiplier));
        rb.AddRelativeForce(new Vector3(0f, 0f, relativeForwardMag), forceMode);

        float aoa = determineDrift != null ? determineDrift.AOA : 0f;
        float curveInput = aoa * smoothedDrift * directionalForceMultiplier;
        float curveOutput = directionBlendCurve.Evaluate(curveInput);
        float worldMag = curveOutput * baseForce * smoothedDrift;

        Vector3 velocityDir = flatVelocity.normalized;
        Vector3 worldForce = new Vector3(worldMag * velocityDir.x, 0f, worldMag * velocityDir.z);
        rb.AddForce(worldForce, forceMode);

        // ---- Debug audio ----
        if (debugAudioSource != null)
        {
            float pitchNormalized = Mathf.Clamp01((CurrentRPM + engineIdleRPM) / (engineRedline + engineIdleRPM));
            debugAudioSource.pitch = Mathf.Lerp(debugMinPitch, debugMaxPitch, pitchNormalized);
        }

        // ---- Stash for OnGUI ----
        debugThrottle = throttle;
        debugDriveForce = driveForce;
        debugConnectionFactor = connectionFactor;
        debugGroundContact = groundContact;
        debugHandbrakeApplied = handbrakeApplied;
        debugWheelRPM = wheelRPM;
        debugWheelImpliedRPM = wheelImpliedRPM;
        debugClutch = clutch;
        debugIgnitionOn = ignitionOn;
    }

    private static AnimationCurve BuildDefaultTorqueCurve()
    {
        return new AnimationCurve(
            new Keyframe(-0.008876801f, 0.2901917f),
            new Keyframe(0.01620896f, 0.2921655f),
            new Keyframe(0.08788354f, 0.2985441f),
            new Keyframe(0.1829785f, 0.3682941f),
            new Keyframe(0.2856327f, 0.6073344f),
            new Keyframe(0.3369174f, 0.7263213f),
            new Keyframe(0.3924502f, 0.8609757f),
            new Keyframe(0.5081984f, 0.9459312f),
            new Keyframe(0.6605506f, 1.0f),
            new Keyframe(0.6723515f, 0.9995553f),
            new Keyframe(0.951171f, 0.900406f),
            new Keyframe(1.0f, 0.0f)
        );
    }

    private static AnimationCurve BuildDefaultDirectionBlendCurve()
    {
        AnimationCurve curve = new AnimationCurve(
            new Keyframe(-180f, -1f, 0f, 0f),
            new Keyframe(-95f, -1f, 0f, 0f),
            new Keyframe(-90f, 0f, 0.2f, 0.2f),
            new Keyframe(-85f, 1f, 0f, 0f),
            new Keyframe(0f, 1f, 0f, 0f),
            new Keyframe(85f, 1f, 0f, 0f),
            new Keyframe(90f, 0f, -0.2f, -0.2f),
            new Keyframe(95f, -1f, 0f, 0f),
            new Keyframe(180f, -1f, 0f, 0f)
        );
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
        return curve;
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || !Application.isPlaying) return;

        float x = Screen.width - 340f;
        float y = 10f;
        const float lineHeight = 18f;

        void Line(string text)
        {
            GUI.Label(new Rect(x, y, 330, 20), text);
            y += lineHeight;
        }

        Line($"RPM: {CurrentRPM:F0} / {engineRedline:F0}   (Idle {engineIdleRPM:F0})");
        Line($"EngineOutput: {EngineOutput:F3}");
        Line($"Throttle: {debugThrottle:F2}   Ignition: {(debugIgnitionOn ? 1 : 0)}");
        Line($"Drive Force: {debugDriveForce:F2}");
        Line($"Connection: {debugConnectionFactor:F2}  (Clutch {debugClutch}, Ground {debugGroundContact:F2}, Handbrake {debugHandbrakeApplied:F2})");
        Line($"Gear Ratio: {(gearbox != null ? gearbox.CurrentGearRatio : 0f):F2}   Final Drive: {finalDrive:F2}");
        Line($"Wheel RPM: {debugWheelRPM:F0}   Wheel-Implied RPM: {debugWheelImpliedRPM:F0}");
        Line($"Drivetrain: {drivetrain}   Smoothed Drift: {smoothedDrift:F2}");
    }
}

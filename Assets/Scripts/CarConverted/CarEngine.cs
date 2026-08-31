using UnityEngine;

/// <summary>
/// Full engine/transmission simulation for the car. This replaces the earlier
/// placeholder — it's new work built to spec (Physics.asset's own reference
/// to an "Engine" macro was empty; nothing here is decompiled from it).
///
/// Design, per your spec:
///  - Auto clutch (no clutch pedal): engages as RPM rises past a threshold,
///    disengages near idle, and is FORCED disengaged while the handbrake
///    is held (so revs are free and grounded traction doesn't stall the
///    engine mid-drift) or while a shift is in progress.
///  - Manual shifting: player presses shift up / shift down. Ignition is
///    cut for the shift's duration (real CDI/flat-shift behaviour) — no
///    torque, no clutch coupling — then the new gear is applied.
///  - RPM limiter: same ignition-cut behaviour at redline, with hysteresis
///    so it doesn't chatter exactly at the cut point.
///  - Engine inertia + response: RPM change is rate-limited by engine
///    inertia rather than snapping to a target.
///  - Engine braking: with the throttle closed, the engine drags RPM back
///    toward idle instead of just coasting.
///  - Idle: a small governor torque holds RPM near idle instead of letting
///    it sag to zero and stall.
///  - "Fake" wheel speed: instead of real wheel colliders, the car's own
///    flat-plane forward speed is run back through the gear ratio to get
///    a "road RPM", which the clutch pulls the engine RPM toward — scaled
///    by how much of the drivetrain's wheels are actually touching the
///    ground (FrontCompression/RearCompression/average, per Drivetrain).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarEngine : MonoBehaviour
{
    private Rigidbody rb;

    [Header("Torque curve")]
    [Tooltip("X = RPM (0..RedlineRpm), Y = normalized torque (0..1) at wide-open throttle.")]
    [SerializeField]
    private AnimationCurve torqueCurve = new AnimationCurve(
        new Keyframe(0f, 0.2f), new Keyframe(1000f, 0.45f), new Keyframe(3000f, 0.85f),
        new Keyframe(4500f, 1.0f), new Keyframe(6500f, 0.8f), new Keyframe(7200f, 0.3f));

    [Header("RPM range")]
    [SerializeField] private float idleRpm = 900f;
    [SerializeField] private float redlineRpm = 7200f;
    [Tooltip("Hysteresis below redline before the limiter lets ignition back in (avoids stutter/chatter right at the cut point).")]
    [SerializeField] private float limiterRecoverRpm = 6900f;

    [Header("Engine feel")]
    [Tooltip("Higher = slower to change RPM (more flywheel mass). This is what was making revs climb too slowly if set too high.")]
    [SerializeField] private float engineInertia = 1f;
    [Tooltip("RPM/sec the engine can gain at full net torque with EngineInertia = 1.")]
    [SerializeField] private float maxRpmChangeRate = 9000f;
    [Tooltip("How hard the engine drags RPM down toward idle when the throttle is closed (engine braking).")]
    [SerializeField] private float engineBrakingStrength = 2500f;
    [Tooltip("How strongly a small idle-governor torque holds RPM near idle so it doesn't stall.")]
    [SerializeField] private float idleGovernorStrength = 4000f;
    [Tooltip("Throttle floor even at 0 input, so the engine can still hold idle against engine braking.")]
    [SerializeField, Range(0f, 0.3f)] private float idleThrottleFloor = 0.08f;

    [Header("Auto clutch")]
    [Tooltip("RPM at which the auto-clutch starts to bite.")]
    [SerializeField] private float clutchEngageStartRpm = 1400f;
    [Tooltip("RPM at which the auto-clutch is fully locked.")]
    [SerializeField] private float clutchEngageFullRpm = 2600f;
    [Tooltip("How strongly (0..1 per second, roughly) the clutch pulls engine RPM toward road RPM once engaged.")]
    [SerializeField, Range(0f, 1f)] private float clutchLockStrength = 0.85f;

    [Header("Gearbox")]
    [Tooltip("Forward gear ratios, gear 1 first. Gear index 0 = Neutral, -1 = Reverse (see ReverseGearRatio).")]
    [SerializeField] private float[] gearRatios = { 3.6f, 2.4f, 1.7f, 1.25f, 1.0f, 0.82f };
    [SerializeField] private float reverseGearRatio = 3.2f;
    [SerializeField] private float finalDriveRatio = 3.7f;
    [Tooltip("How long (seconds) ignition is cut while a manual shift is in progress.")]
    [SerializeField] private float shiftTime = 0.25f;

    [Header("Fake wheel speed")]
    [Tooltip("Effective driven-wheel radius, used to convert car speed <-> wheel/engine RPM.")]
    [SerializeField] private float drivenWheelRadius = 0.32f;

    // ----- Runtime state -----
    public float CurrentRpm { get; private set; }
    public int CurrentGear { get; private set; } = 0; // 0 = Neutral, -1 = Reverse, 1..N = forward
    public bool IsShifting { get; private set; }
    public float ClutchEngagement { get; private set; }
    public bool IgnitionCut { get; private set; }

    /// <summary>Normalized (0..1) delivered engine output. Drops to 0 during ignition cut / shifting.
    /// This is what CarPhysics/Steering read as "EngineOutput".</summary>
    public float EngineOutput { get; private set; }

    /// <summary>Current gear ratio * final drive, read by Steering as its GearRatio parameter.</summary>
    public float CurrentGearRatio { get; private set; } = 1f;

    private bool rpmLimiterActive;
    private float shiftTimer;
    private int pendingGear;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        CurrentRpm = idleRpm;
    }

    /// <summary>
    /// Call once per FixedUpdate from CarPhysics, after Suspension has updated compression
    /// and Handbrake for this frame is known.
    /// </summary>
    /// <param name="frontCompression">Suspension's front compression average (0..1).</param>
    /// <param name="rearCompression">Suspension's rear compression average (0..1).</param>
    /// <param name="drivetrain">0 = RWD, 1 = FWD, 2 = AWD — selects which compression(s) couple the engine to the ground.</param>
    /// <param name="handbrakeEngaged">True while the handbrake is held — forces the clutch open.</param>
    public void UpdateEngine(float frontCompression, float rearCompression, int drivetrain, bool handbrakeEngaged)
    {
        float dt = Time.fixedDeltaTime;
        float throttle = Mathf.Clamp01(ReadThrottleInputAxis()); // TODO: wire to Input System throttle axis

        HandleShiftInput(); // TODO: wire ReadShiftUpPressed/ReadShiftDownPressed to Input System

        // --- Shift state machine: ignition is cut for the whole shift, then the gear applies ---
        if (IsShifting)
        {
            shiftTimer -= dt;
            if (shiftTimer <= 0f)
            {
                CurrentGear = pendingGear;
                IsShifting = false;
            }
        }

        // --- Drivetrain ground-contact coupling: which compression(s) "connect" the engine to the road ---
        float drivetrainCompression = drivetrain switch
        {
            0 => rearCompression,                       // RWD
            1 => frontCompression,                       // FWD
            _ => (frontCompression + rearCompression) * 0.5f, // AWD
        };

        // --- RPM limiter (real CDI-style cut with hysteresis) ---
        if (!rpmLimiterActive && CurrentRpm >= redlineRpm) rpmLimiterActive = true;
        else if (rpmLimiterActive && CurrentRpm <= limiterRecoverRpm) rpmLimiterActive = false;

        IgnitionCut = rpmLimiterActive || IsShifting;

        // --- Torque-driven RPM delta (throttle, engine braking, idle governor) ---
        float wotTorque = torqueCurve.Evaluate(CurrentRpm);
        float effectiveThrottle = Mathf.Max(throttle, idleThrottleFloor);
        float driveTorque = IgnitionCut ? 0f : wotTorque * effectiveThrottle;

        float engineBraking = (1f - effectiveThrottle) * engineBrakingStrength * (CurrentRpm / redlineRpm);
        float idleGovernor = CurrentRpm < idleRpm ? (idleRpm - CurrentRpm) / idleRpm * idleGovernorStrength : 0f;

        float netNormalizedTorque = driveTorque - (engineBraking / maxRpmChangeRate) + (idleGovernor / maxRpmChangeRate);
        float freeRpmDelta = netNormalizedTorque * maxRpmChangeRate / Mathf.Max(engineInertia, 0.01f) * dt;

        float rpmAfterFree = Mathf.Max(0f, CurrentRpm + freeRpmDelta);

        // --- Auto clutch: engagement amount, forced open on handbrake / shifting / neutral ---
        float targetClutch = Mathf.InverseLerp(clutchEngageStartRpm, clutchEngageFullRpm, rpmAfterFree);
        if (handbrakeEngaged || IsShifting || CurrentGear == 0) targetClutch = 0f;
        // Clutch itself isn't instant either — small smoothing avoids an abrupt lock/unlock "thunk".
        ClutchEngagement = Mathf.MoveTowards(ClutchEngagement, targetClutch, dt * 4f);

        // --- Road RPM from the car's own flat-plane forward speed (the "fake wheel rotation") ---
        Vector3 flatVelocity = Vector3.ProjectOnPlane(rb.linearVelocity, Vector3.up);
        float flatForwardSpeed = Mathf.Abs(Vector3.Dot(flatVelocity, transform.forward)); // "absolute flat plane velocity"
        float wheelRpm = (flatForwardSpeed / (2f * Mathf.PI * drivenWheelRadius)) * 60f;
        float gearRatioMagnitude = Mathf.Abs(GetGearRatio(CurrentGear));
        float roadRpm = wheelRpm * gearRatioMagnitude * finalDriveRatio;

        // --- Blend engine RPM toward road RPM by how locked the clutch is AND how much ground contact there is ---
        float coupling = Mathf.Clamp01(ClutchEngagement * Mathf.Clamp01(drivetrainCompression) * clutchLockStrength * dt * 10f);
        CurrentRpm = Mathf.Lerp(rpmAfterFree, roadRpm, coupling);
        CurrentRpm = Mathf.Clamp(CurrentRpm, 0f, redlineRpm * 1.05f);

        CurrentGearRatio = GetGearRatio(CurrentGear) * finalDriveRatio;
        EngineOutput = IgnitionCut ? 0f : Mathf.Clamp01(wotTorque * effectiveThrottle);
    }

    private float GetGearRatio(int gear)
    {
        if (gear == 0) return 0f; // Neutral
        if (gear < 0) return -reverseGearRatio;
        int idx = gear - 1;
        return (idx >= 0 && idx < gearRatios.Length) ? gearRatios[idx] : gearRatios[gearRatios.Length - 1];
    }

    private void HandleShiftInput()
    {
        if (IsShifting) return;

        // TODO: wire these to Input System (e.g. shift paddles / sequential shifter buttons).
        bool shiftUp = ReadShiftUpPressed();
        bool shiftDown = ReadShiftDownPressed();

        int maxGear = gearRatios.Length;
        if (shiftUp && CurrentGear < maxGear)
        {
            pendingGear = CurrentGear + 1;
            BeginShift();
        }
        else if (shiftDown && CurrentGear > -1)
        {
            pendingGear = CurrentGear - 1;
            BeginShift();
        }
    }

    private void BeginShift()
    {
        IsShifting = true;
        shiftTimer = shiftTime;
    }

    // ----- TODO: wire these to your input system -----
    private float ReadThrottleInputAxis() => 0f;
    private bool ReadShiftUpPressed() => false;
    private bool ReadShiftDownPressed() => false;
}

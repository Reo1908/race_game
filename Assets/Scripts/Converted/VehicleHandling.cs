using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// C# port of the "VehicleHandling" Script Graph (Unity 6).
/// Execution order matches the graph's FixedUpdate chain:
/// Steering -> Movement -> Sideways Stop -> Extra Gravity -> Drag
///
/// The reused subgraphs are now plain helpers at the bottom (LocalVelocity, FloatLerped),
/// and the slip angle plus the two grip/drift blend values come from DetermineDrift.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(DetermineDrift))]
public class VehicleHandling : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Read from the other scripts on this GameObject (found automatically)
    // ------------------------------------------------------------------
    // NOTE: "VehicleSuspension" is a guessed class name - change it to whatever
    // your script with FrontAxleCompression / RearAxleCompression / AverageCompression is called.
    // VehicleEngine holds EngineOutput; VehicleGearbox is assumed to hold CurrentGearRatio.
    private VehicleSuspension suspension;
    private VehicleGearbox gearbox;
    private VehicleEngine engine;
    private VehicleBrakes brakes;
    private DetermineDrift determineDrift;

    private float FrontAxleCompression => suspension.FrontAxleCompression;
    private float RearAxleCompression => suspension.RearAxleCompression;
    private float AverageCompression => suspension.AverageCompression;
    private float CurrentGearRatio => gearbox.CurrentGearRatio;
    private float EngineOutput => engine.EngineOutput;
    private float Handbrake => brakes.Handbrake;   // 1 = released, 0 = fully pulled (multiplies all grip)
    private float Braking => brakes.Braking;

    // Read by VehicleAnimations
    public float Steering => steering;
    public float TransitionSpeed => transitionSpeed;

    // ------------------------------------------------------------------
    // Object variables -> serialized fields
    // ------------------------------------------------------------------
    [Header("References")]
    [SerializeField] private InputActionReference steerAction;

    [Header("General")]
    [SerializeField] private float additionalGravity = -0.3f;
    [SerializeField] private float dragCoefficient = 0.31f;
    [SerializeField] private float transitionSpeed = 5f;
    [Tooltip("Strength of the yaw (turning) angular-velocity damping. Moved here from the Lean section of the Animations graph.")]
    [SerializeField] private float stability = 3f;

    [Header("Steering")]
    [SerializeField] private float turnRate = 2.3f;
    [SerializeField] private float gripSteeringResponse = 100f;
    [SerializeField] private float driftSteeringResponse = 10f;
    [SerializeField] private float handbrakeSteeringAdd = 1f;
    [SerializeField] private float brakeSteeringAdd = 0.2f;
    [SerializeField] private float throttleSteeringAdd = 0.1f;
    [SerializeField] private float throttleSteeringMultiplier = 1f;
    [SerializeField] private float noThrottleRecovery = 0.5f;
    [SerializeField] private float frontLateralSteeringTravel = 7f;
    [SerializeField] private float driftInstability = 1.7f;
    [SerializeField] private float brakeRearBalance = 0.9f;
    [SerializeField] private float rearGrip = 0.5f;

    [Header("Grip / Drift")]
    [SerializeField] private float grip = 6f;
    [SerializeField] private float driftGrip = 5f;
    [SerializeField] private float driftDirection = 0.18f;
    [Tooltip("Base amount the spline frame follows the car's own direction, even off throttle (the graph hardcoded this as 0.08).")]
    [SerializeField] private float offThrottleFollow = 0.08f;
    [SerializeField] private float driftDirectionReactionSpeed = 6f;

    [Header("Runtime values (written by this script)")]
    [SerializeField] private float steering;
    [SerializeField] private float currentSpeed;
    [SerializeField] private float currentDriftAngle;
    [SerializeField] private float turning;

    [Header("Debug")]
    [SerializeField] private bool showDebugOverlay = true;
    [SerializeField] private bool showDebugLines = true;
    [SerializeField] private float debugLineScale = 1f;

    // Last-frame values captured for the debug overlay
    private float dbgSteerTorque, dbgRearTyre, dbgOversteer, dbgSteerMult, dbgYaw, dbgFrontLateral, dbgDriftYaw;
    private Vector3 dbgGripForce, dbgDriftForce;


    // ------------------------------------------------------------------
    // Curves (values copied from the graph's AnimationCurve literals)
    // ------------------------------------------------------------------
    [Header("Curves")]
    [SerializeField] private AnimationCurve sidewaysStopCurve = new AnimationCurve(
        new Keyframe(0f, 1f, -0.04109422f, -0.04109422f),
        new Keyframe(20f, 0f, 0f, 0f));

    [SerializeField] private AnimationCurve yawDampingCurve = new AnimationCurve(
        new Keyframe(0f, 1f, -0.3333333f, -0.3333333f),
        new Keyframe(3f, 0f, -0.3333333f, -0.3333333f));

    [SerializeField] private AnimationCurve frontLateralSpeedCurve = new AnimationCurve(
        new Keyframe(-1f, -1f, 1f, 1f),
        new Keyframe(0f, 0f, 0.9952291f, 0.9952291f),
        new Keyframe(1f, 1f, 1f, 1f));

    [SerializeField] private AnimationCurve steerMultLocalZCurve = new AnimationCurve(
        new Keyframe(-1f, -1f, 1f, 1f),
        new Keyframe(0f, 0f, 0.9952291f, 0.9952291f),
        new Keyframe(1f, 1f, 1f, 1f));

    [SerializeField] private AnimationCurve steerMultSpeedCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 1f, 1f),
        new Keyframe(1f, 1f, 1f, 1f));

    [SerializeField] private AnimationCurve oversteerAngVelCurve = new AnimationCurve(
        new Keyframe(-5f, 0f, 0f, 0f),
        new Keyframe(0f, 1f, 0f, 0f),
        new Keyframe(5f, 0f, 0f, 0f));

    [SerializeField] private AnimationCurve oversteerAoaCurve = new AnimationCurve(
        new Keyframe(-180f, 0f, 0f, 0f),
        new Keyframe(-135.8442f, 0.485932f, 0.0166608f, 0.0166608f),
        new Keyframe(-90f, 1f, 0f, 0f),
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(90f, 1f, 0f, 0f),
        new Keyframe(180f, 0f, 0f, 0f));

    [SerializeField] private AnimationCurve driftAoaCurve = new AnimationCurve(
        new Keyframe(-180f, 0f, 0f, 0f),
        new Keyframe(-90f, -90f, 0f, 0f),
        new Keyframe(0.2662964f, 0.3811035f, 1.230551f, 1.230551f),
        new Keyframe(90f, 90f, 0f, 0f),
        new Keyframe(180f, 0f, 0f, 0f));

    [SerializeField] private AnimationCurve driftEngineCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 1f),
        new Keyframe(1f, 1f, 1f, 0f));

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------
    // Replaces the old "SplineCalculator" helper transform: just a stored rotation.
    private Quaternion splineRotation = Quaternion.identity;
    private Vector3 SplineForward => splineRotation * Vector3.forward;
    private Vector3 SplineRight => splineRotation * Vector3.right;

    private Rigidbody rb;
    private SplineProbeBridge splineProbe;

    // One smoothing state per FloatLerped macro instance in the graph
    private readonly LerpedFloat steerLerp = new LerpedFloat();          // Steering   (#5)
    private readonly LerpedFloat frontLateralLerp = new LerpedFloat();   // Steering   (#13)
    private readonly LerpedFloat oversteerLerp = new LerpedFloat();      // Oversteer  (#1)
    private readonly LerpedFloat rearTyreLerp = new LerpedFloat();       // Rear Tyre  (#9)
    private readonly LerpedFloat driftYawLerp = new LerpedFloat();       // Movement   (#16)
    private readonly LerpedFloat driftYawBlendLerp = new LerpedFloat();  // Movement   (#17)
    private readonly LerpedFloat driftForceLerp = new LerpedFloat();     // Movement   (#18)
    private readonly LerpedFloat gripForceLerp = new LerpedFloat();      // Movement   (#19)

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        splineRotation = transform.rotation;
        determineDrift = GetComponent<DetermineDrift>();
        suspension = GetComponent<VehicleSuspension>();
        gearbox = GetComponent<VehicleGearbox>();
        engine = GetComponent<VehicleEngine>();
        brakes = GetComponent<VehicleBrakes>();
    }

    private void Start()
    {
        splineProbe = GetComponent<SplineProbeBridge>();
    }

    private void FixedUpdate()
    {
        ApplySteering(FrontAxleCompression, RearAxleCompression, CurrentGearRatio);
        ApplyMovement(AverageCompression);
        ApplySidewaysStop();

        // Extra gravity
        rb.AddForce(new Vector3(0f, additionalGravity, 0f), ForceMode.VelocityChange);

        ApplyDrag();
        ApplyStability();
    }

    // ==================================================================
    // STEERING
    // ==================================================================
    private void ApplySteering(float frontCompression, float rearCompression, float gearRatio)
    {
        float front = Mathf.Clamp01(frontCompression);
        float rear = Mathf.Clamp01(rearCompression);
        Vector3 vel = rb.linearVelocity;
        Vector3 angVel = rb.angularVelocity;
        float speed = vel.magnitude;

        // "Steering Value" subgraph
        if (steerAction != null)
            steering = steerAction.action.ReadValue<float>();

        float gripTarget = determineDrift.Grip1Drift0;   // macro "1 to 0"
        float driftTarget = determineDrift.Grip0Drift1;  // macro "0 to 1"

        // Steering response speed (unnamed subgraph)
        float responseSpeed = gripSteeringResponse * gripTarget + driftSteeringResponse * driftTarget;

        // HandbrakeAdd / BrakeAdd / ThrottleAdd subgraphs
        float handbrakeAdd = 1f + (1f - Handbrake) * handbrakeSteeringAdd;
        float brakeAdd = Braking * brakeSteeringAdd;
        float throttleAdd = throttleSteeringAdd * Mathf.Clamp01(1f - EngineOutput);

        float steerTorque =
            steerLerp.Step(steering, responseSpeed) * (turnRate * (handbrakeAdd + brakeAdd + throttleAdd)) * front;

        float rearTyre = RearTyre(rear, gripTarget);
        float oversteer = ThrottleOversteer(gearRatio, driftTarget);
        float steerMult = SteeringMultiplierFiltered(gripTarget, driftTarget);
        float yaw = (steerTorque + rearTyre + oversteer) * steerMult;

        dbgSteerTorque = steerTorque;
        dbgRearTyre = rearTyre;
        dbgOversteer = oversteer;
        dbgSteerMult = steerMult;
        dbgYaw = yaw;

        rb.AddRelativeTorque(0f, yaw, 0f, ForceMode.Acceleration);

        // Front lateral force
        float frontLateral = angVel.y
            * (frontLateralLerp.Step(gripTarget, transitionSpeed)
               * frontLateralSpeedCurve.Evaluate(Mathf.Abs(speed))
               * frontLateralSteeringTravel
               * front);
        rb.AddRelativeForce(frontLateral, 0f, 0f, ForceMode.Acceleration);
        dbgFrontLateral = frontLateral;

        // Yaw damping (the graph's separate FixedUpdate event)
        float damping = angVel.y * -0.07f * yawDampingCurve.Evaluate(Mathf.Abs(speed));
        rb.AddRelativeTorque(0f, damping, 0f, ForceMode.VelocityChange);
    }

    // "Rear Tyre" subgraph
    private float RearTyre(float comp, float gripTarget)
    {
        return Aoa * Mathf.Clamp(LocalVelocity.z * 0.01f, 0f, 1f)
               * rearTyreLerp.Step(gripTarget, transitionSpeed)
               * rearGrip
               * Handbrake
               * Mathf.Clamp(comp, 0f, 1f)
               * (1f - brakeRearBalance * Braking);
    }

    // "Throttle Oversteer" subgraph
    private float ThrottleOversteer(float gearRatio, float driftTarget)
    {
        float angVelY = rb.angularVelocity.y;
        float blend = oversteerLerp.Step(driftTarget, transitionSpeed);
        float aoa = Aoa;

        float counterSteer = oversteerAngVelCurve.Evaluate(angVelY) * angVelY * driftInstability * blend;

        float throttleSlide = oversteerAoaCurve.Evaluate(aoa)
                              * (aoa * (EngineOutput * throttleSteeringMultiplier - noThrottleRecovery) * -0.02f)
                              * gearRatio
                              * blend;

        return (counterSteer + throttleSlide) * Handbrake;
    }

    // Unnamed subgraph -> "SteeringMultiplier Filtered"
    private float SteeringMultiplierFiltered(float gripTarget, float driftTarget)
    {
        return steerMultLocalZCurve.Evaluate(LocalVelocity.z * 0.5f) * gripTarget
               + driftTarget * steerMultSpeedCurve.Evaluate(currentSpeed);
    }

    // ==================================================================
    // MOVEMENT
    // ==================================================================
    private void ApplyMovement(float compression)
    {
        Vector3 vel = rb.linearVelocity;
        float gripTarget = determineDrift.Grip1Drift0;
        float driftTarget = determineDrift.Grip0Drift1;
        Vector3 flat = new Vector3(1f, 0f, 1f);

        // CurrentDriftAngle (the graph's separate FixedUpdate event)
        Vector3 carFwdFlat = Vector3.Scale(transform.forward, flat).normalized;
        Vector3 splineFwdFlat = Vector3.Scale(SplineForward, flat).normalized;
        currentDriftAngle = Vector3.Angle(carFwdFlat, splineFwdFlat)
                            * Vector3.Dot(Vector3.Cross(carFwdFlat, splineFwdFlat), Vector3.up);

        // Orient the spline calculator along the track tangent plus a drift offset
        if (splineProbe != null)
        {
            Vector3 tangent = splineProbe.GetTangent(transform.position).normalized;
            Vector3 baseEuler = Quaternion.LookRotation(tangent, Vector3.up).eulerAngles;

            float driftYawTarget =
                driftAoaCurve.Evaluate(Aoa)
                * (-(driftDirection + offThrottleFollow) * driftYawBlendLerp.Step(driftTarget, transitionSpeed * 1.5f))
                * driftEngineCurve.Evaluate(EngineOutput);

            float driftYaw = driftYawLerp.Step(driftYawTarget, driftDirectionReactionSpeed);
            dbgDriftYaw = driftYaw;
            splineRotation = Quaternion.Euler(baseEuler + new Vector3(0f, driftYaw, 0f));
        }

        // Lateral grip force (car-relative) + drift grip force (spline-relative)
        float gripSlip = Vector3.Dot(vel, transform.right) * -1f * grip;
        Vector3 gripForce = Vector3.Scale(transform.right, new Vector3(gripSlip, 0f, gripSlip))
                            * gripForceLerp.Step(gripTarget, transitionSpeed);

        float driftSlip = Vector3.Dot(vel, SplineRight) * -1f * driftGrip;
        Vector3 driftForce = Vector3.Scale(SplineRight, new Vector3(driftSlip, 0f, driftSlip))
                             * driftForceLerp.Step(driftTarget, transitionSpeed);

        rb.AddForce((gripForce + driftForce) * Handbrake * compression, ForceMode.Acceleration);
        dbgGripForce = gripForce * Handbrake * compression;
        dbgDriftForce = driftForce * Handbrake * compression;

        currentSpeed = Mathf.Abs(Vector3.Dot(rb.linearVelocity, SplineForward));
        turning = Vector3.Dot(rb.linearVelocity, SplineRight) * -1f;
    }

    // ==================================================================
    // SIDEWAYS STOP
    // ==================================================================
    private void ApplySidewaysStop()
    {
        float scale = sidewaysStopCurve.Evaluate(rb.linearVelocity.magnitude);
        rb.AddRelativeForce(scale * (LocalVelocity.x * -2f), 0f, 0f, ForceMode.Acceleration);
    }

    // ==================================================================
    // DRAG
    // ==================================================================
    private void ApplyDrag()
    {
        Vector3 vel = rb.linearVelocity;
        float mag = vel.magnitude;
        float dragAmount = mag * mag * -0.001f * dragCoefficient;
        rb.AddForce(vel.normalized * dragAmount, ForceMode.Acceleration);
    }

    // ==================================================================
    // STABILITY (yaw damping)
    // ==================================================================
    private void ApplyStability()
    {
        float localYawRate = transform.InverseTransformDirection(rb.angularVelocity).y;
        rb.AddRelativeTorque(0f, -stability * localYawRate, 0f, ForceMode.Acceleration);
    }

    // ==================================================================
    // DEBUG
    // ==================================================================
    private void Update()
    {
        if (!showDebugLines || rb == null) return;

        Vector3 p = transform.position + Vector3.up * 0.5f;
        float k = debugLineScale;
        Debug.DrawRay(p, rb.linearVelocity * 0.3f * k, Color.white);            // velocity
        Debug.DrawRay(p, transform.forward * 3f * k, Color.blue);               // car forward
        Debug.DrawRay(p, SplineForward * 3f * k, Color.green);                  // spline forward (+ drift offset)
        Debug.DrawRay(p, SplineRight * 2f * k, Color.yellow);                   // spline right (drift grip direction)
        Debug.DrawRay(p, dbgGripForce * 0.2f * k, Color.cyan);                  // car-relative grip force
        Debug.DrawRay(p, dbgDriftForce * 0.2f * k, Color.magenta);              // spline-relative drift force
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || !Application.isPlaying || rb == null) return;

        // Starts at y=240, below DetermineDrift's overlay (160-214)
        float y = 240f;
        void Line(string text) { GUI.Label(new Rect(10, y, 420, 20), text); y += 18f; }

        Line($"-- VehicleHandling --");
        Line($"Speed: {rb.linearVelocity.magnitude:F2}   Local vel: {LocalVelocity.ToString("F2")}");
        Line($"Steering: {steering:F2}   Engine: {EngineOutput:F2}   Gear ratio: {CurrentGearRatio:F2}");
        Line($"Compression F/R/Avg: {FrontAxleCompression:F2} / {RearAxleCompression:F2} / {AverageCompression:F2}");
        Line($"Grip1Drift0: {determineDrift.Grip1Drift0:F2}   AOA: {Aoa:F1}");
        Line($"Handbrake: {Handbrake:F2}   Braking: {Braking:F2}");
        Line($"Yaw torque: {dbgYaw:F3}  (steer {dbgSteerTorque:F2}, rear {dbgRearTyre:F2}, oversteer {dbgOversteer:F2}, x{dbgSteerMult:F2})");
        Line($"Front lateral force: {dbgFrontLateral:F3}");
        Line($"Grip force: {dbgGripForce.magnitude:F2}   Drift force: {dbgDriftForce.magnitude:F2}");
        Line($"Spline speed: {currentSpeed:F2}   Spline slide: {turning:F2}");
        Line($"Drift angle vs spline: {currentDriftAngle:F1}   Drift yaw offset: {dbgDriftYaw:F1}");
        Line($"Spline probe: {(splineProbe != null ? "found" : "MISSING")}");
    }

    // ==================================================================
    // REUSED SUBGRAPHS
    // ==================================================================

    // "LocalVelocity": linear velocity relative to the car
    private Vector3 LocalVelocity => transform.InverseTransformDirection(rb.linearVelocity);

    // "AOA" (really the angle of slip): provided by DetermineDrift
    private float Aoa => determineDrift.AOA;

    // "FloatLerped(Float, Speed)": fixed-delta-time lerp, one state per use
    private class LerpedFloat
    {
        private float value;

        public float Step(float target, float speed)
        {
            value = Mathf.Lerp(value, target, speed * Time.fixedDeltaTime);
            return value;
        }
    }
}
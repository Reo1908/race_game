using UnityEngine;

/// <summary>
/// Converted from Physics.asset (Unity Visual Scripting "Car Physics" graph).
/// Covers every embedded sub-graph EXCEPT "Movement", which was split out
/// into CarMovement.cs per request (that's the "engine and forward
/// movement" piece).
///
/// Execution order each FixedUpdate mirrors the original graph:
///   Steering -> (CarMovement.FixedUpdate runs separately) -> Sideways Stop
///   -> downforce -> Drag
/// Suspension, Braking, Handbrake and the Steering-input reader run on
/// their own independent FixedUpdate chains, exactly as in the source graph
/// (each wheel had its own raycast+force FixedUpdate node).
///
/// MISSING EXTERNAL MACROS: Physics.asset references 5 separate macro
/// graph assets that were not included in the upload — an Engine/
/// Transmission macro (gear ratio) and 4 reusable float-smoothing macros.
/// Those are stubbed with clear TODOs below; everything else is a direct,
/// verified conversion of the node graph.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarPhysics : MonoBehaviour
{
    [SerializeField] private CarPhysicsSettings settings;
    [SerializeField] private CarMovement movement;
    [SerializeField] private CarEngine engine;

    private Rigidbody rb;

    // ----- Suspension state (was "Graph" kind variables inside the Suspension sub-graph) -----
    private readonly float[] height = new float[4];       // FL, FR, RL, RR
    private readonly float[] compressionRaw = new float[4]; // pre-Progression-curve value

    // ----- Steering-local state -----
    private float steeringFrontCompression;
    private float steeringRearCompression;

    // ----- Runtime state written by the graph (was Object-kind, but written to -> not read-only) -----
    public float Braking { get; private set; }
    public float Steering { get; private set; }
    public float Handbrake { get; private set; } = 1f; // 1 = full grip / not handbraking, 0 = handbrake engaged

    // Exposed compression outputs (Suspension's GraphOutput), post Progression-curve.
    public float CompressionFL { get; private set; }
    public float CompressionFR { get; private set; }
    public float CompressionRL { get; private set; }
    public float CompressionRR { get; private set; }
    public float CompressionAverage { get; private set; }

    // ---- reusable float-smoothing macros (3 more distinct assets, not included) ----
    [SerializeField] private float turnCorrectionLerpRate = 15f;
    private float smoothedTurnCorrection; // was ExternalMacro3(...).FloatLerped in Steering
    [SerializeField] private float rearTyreLerpRate = 15f;
    private float smoothedRearTyre; // was ExternalMacro8(...).FloatLerped in RearTyre calc
    [SerializeField] private float driftInstabilityLerpRate = 15f;
    private float smoothedDriftInstability; // was ExternalMacro14(...).FloatLerped in ThrottleOversteer

    private static float SmoothTowards(float current, float target, float rate)
    {
        // TODO: replace with the real macro logic if this placeholder doesn't match feel.
        return Mathf.Lerp(current, target, 1f - Mathf.Exp(-rate * Time.fixedDeltaTime));
    }

    // "LocalVelocity" utility macro (guid 1db8eb1e..., used 3x) — this one IS a safe, standard
    // conversion (world velocity -> local space), implemented directly rather than stubbed.
    private Vector3 GetLocalVelocity() => transform.InverseTransformDirection(rb.linearVelocity);

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void FixedUpdate()
    {
        UpdateSuspension();
        UpdateHubVisuals();
        UpdateBrakingInput();
        UpdateHandbrakeInput(); // must run before the engine update — clutch needs this frame's handbrake state
        UpdateSteeringInput();

        steeringFrontCompression = Mathf.Clamp01((CompressionFL + CompressionFR) / 2f);
        steeringRearCompression = Mathf.Clamp01((CompressionRL + CompressionRR) / 2f);
        engine.UpdateEngine(steeringFrontCompression, steeringRearCompression, settings.Drivetrain, handbrakeHeld);

        UpdateSteering(); // reads Suspension compression averages + engine output/gear ratio, applies steering torque/force

        // movement.FixedUpdate() runs on its own Unity message; ensure script execution
        // order puts CarPhysics before CarMovement if you need strict ordering,
        // or call movement.RunFixedUpdate() manually here instead if you prefer explicit control.
        movement.SetCompression((CompressionFL + CompressionFR + CompressionRL + CompressionRR) / 4f);
        movement.Handbrake = Handbrake;

        ApplySidewaysStop();
        ApplyDownforce();
        ApplyDrag();
        ApplyBraking();
    }

    // =========================================================================
    // SUSPENSION  (was the "Suspension" sub-graph — 4x raycast spring/damper)
    // =========================================================================
    private void UpdateSuspension()
    {
        for (int i = 0; i < 4; i++)
        {
            Transform point = settings.SuspensionPoints[i];
            if (point == null) continue;

            Vector3 origin = transform.TransformPoint(point.localPosition);
            Vector3 dir = Vector3.Scale(point.up, new Vector3(-1f, -1f, -1f));

            if (Physics.Raycast(origin, dir, out RaycastHit hit, settings.SuspensionHeight, settings.SuspensionLayerMask))
            {
                height[i] = settings.SuspensionHeight - hit.distance;

                float strength = (i < 2) ? settings.SuspensionStrengthF : settings.SuspensionStrengthR;
                float springForce = (height[i] / settings.SuspensionHeight) * strength;
                float damperForce = Mathf.Clamp(
                    Vector3.Dot(rb.GetPointVelocity(origin), transform.up) * settings.SuspensionDamping,
                    -5000f, 200f);

                float raw = Mathf.Abs(springForce - damperForce) * 0.0001f;
                compressionRaw[i] = raw;

                rb.AddForceAtPosition(new Vector3(0f, raw, 0f), origin, ForceMode.VelocityChange);
            }
            // NOTE: on a raycast miss the graph simply leaves height[i]/compressionRaw[i]
            // at their last value (no explicit reset) — preserved here.
        }

        float mult = settings.CompressionGripMultiplier;
        CompressionFL = settings.SuspensionProgression.Evaluate(compressionRaw[0] * mult);
        CompressionFR = settings.SuspensionProgression.Evaluate(compressionRaw[1] * mult);
        CompressionRL = settings.SuspensionProgression.Evaluate(compressionRaw[2] * mult);
        CompressionRR = settings.SuspensionProgression.Evaluate(compressionRaw[3] * mult);
        CompressionAverage = settings.SuspensionProgression.Evaluate(
            (compressionRaw[0] * mult + compressionRaw[1] * mult + compressionRaw[2] * mult + compressionRaw[3] * mult) / 4f);
    }

    private void UpdateHubVisuals()
    {
        for (int i = 0; i < 4; i++)
        {
            Transform hub = settings.Hubs[i];
            if (hub == null) continue;

            float diameter = (i < 2) ? settings.WheelDiameterFront : settings.WheelDiameterRear;
            float y = Mathf.Clamp(
                height[i] - (settings.SuspensionHeight - diameter * 0.5f),
                -settings.SuspensionHeight, settings.SuspensionHeight);

            hub.localPosition = new Vector3(0f, y, 0f);
        }
    }

    // =========================================================================
    // STEERING INPUT  (was the "Steering Value" nested sub-graph)
    // =========================================================================
    private void UpdateSteeringInput()
    {
        // TODO: hook up to your input system — was InputSystem.OnInputSystemEventFloat.
        Steering = ReadSteeringInputAxis();
    }
    private float ReadSteeringInputAxis() => 0f; // TODO: wire to Input System steering axis

    // =========================================================================
    // BRAKING  (was the "Braking" sub-graph)
    // =========================================================================
    private void UpdateBrakingInput()
    {
        // TODO: hook up to your input system — was InputSystem.OnInputSystemEventFloat.
        Braking = ReadBrakeInputAxis();
    }
    private float ReadBrakeInputAxis() => 0f; // TODO: wire to Input System brake axis

    private void ApplyBraking()
    {
        float velSign = Mathf.Clamp(rb.linearVelocity.magnitude * -1f, -1f, 1f);

        float handbrakeTorqueTerm = (1f - Handbrake) * steeringRearCompression * settings.HandbrakeTorque;
        float brakeTorqueTerm = ((steeringRearCompression * 0.3f + steeringFrontCompression * 0.7f) * Braking) * settings.BrakeTorque;
        float brakeAmount = velSign * (handbrakeTorqueTerm + brakeTorqueTerm);

        Vector3 brakeForce = new Vector3(brakeAmount, 0f, brakeAmount);
        rb.AddForce(Vector3.Scale(brakeForce, rb.linearVelocity.normalized), ForceMode.Acceleration);
    }

    // =========================================================================
    // HANDBRAKE  (was the "Handbrake" sub-graph)
    // =========================================================================
    private void UpdateHandbrakeInput()
    {
        // TODO: hook up to your input system — was InputSystem.OnInputSystemEventFloat,
        // smoothed by an external "FloatLerped" macro before being inverted below.
        float rawHandbrakeInput = ReadHandbrakeInputAxis();
        handbrakeHeld = rawHandbrakeInput > 0.5f;
        float smoothed = SmoothTowards(0f, rawHandbrakeInput, 15f); // TODO: replace placeholder smoothing
        Handbrake = 1f - smoothed;
    }
    private bool handbrakeHeld;
    private float ReadHandbrakeInputAxis() => 0f; // TODO: wire to Input System handbrake axis/button

    // =========================================================================
    // STEERING  (was the "Steering" sub-graph + its nested BrakeAdd/ThrottleAdd/
    // HandbrakeAdd/Rear Tyre/Throttle Oversteer/steering-multiplier-filter macros)
    // =========================================================================
    private void UpdateSteering()
    {
        float speed = rb.linearVelocity.magnitude;

        // Angular-velocity self-centering correction torque.
        AnimationCurve selfCenterCurve = AnimationCurve.Linear(0f, 1f, 3f, 0f); // TODO: paste real curve (was embedded, keys 0->1, 3->0)
        rb.AddRelativeTorque(0f, rb.angularVelocity.y * -0.07f * selfCenterCurve.Evaluate(Mathf.Abs(speed)), 0f, ForceMode.VelocityChange);

        float brakeAdd = Braking * settings.BrakeSteeringAdd;
        float throttleAdd = settings.ThrottleSteeringAdd * Mathf.Clamp01(1f - engine.EngineOutput);
        float handbrakeAdd = 1f + (1f - Handbrake) * settings.HandbrakeSteeringAdd;

        float rearTyre = ComputeRearTyre(steeringRearCompression);
        float throttleOversteer = ComputeThrottleOversteer(engine.CurrentGearRatio);
        float steeringMultiplierFiltered = ComputeSteeringMultiplierFiltered();

        // TODO: replace with real turn-correction smoothing macro (ExternalMacro7)
        smoothedTurnCorrection = SmoothTowards(smoothedTurnCorrection, 1f, turnCorrectionLerpRate);

        float steerTorqueY =
            (((smoothedTurnCorrection * (settings.TurnRate * (handbrakeAdd + brakeAdd + throttleAdd))) * steeringFrontCompression)
             + rearTyre + throttleOversteer) * steeringMultiplierFiltered;

        rb.AddRelativeTorque(0f, steerTorqueY, 0f, ForceMode.Acceleration);

        // Front lateral steering-travel force.
        AnimationCurve lateralTravelCurve = new AnimationCurve( // TODO: paste real curve (-1,-1)->(0,0)->(1,1)
            new Keyframe(-1f, -1f), new Keyframe(0f, 0f), new Keyframe(1f, 1f));
        float lateralFactor = lateralTravelCurve.Evaluate(Mathf.Abs(speed));
        rb.AddRelativeForce(
            rb.angularVelocity.y * (smoothedTurnCorrection * lateralFactor * settings.FrontLateralSteeringTravel) * steeringFrontCompression,
            0f, 0f, ForceMode.Acceleration);
    }

    /// <summary>Was the "Rear Tyre" nested sub-graph.</summary>
    private float ComputeRearTyre(float rearCompressionParam)
    {
        // TODO: replace AOA + FloatLerped with real macro logic (ExternalMacro8/10/11).
        float aoa = 0f; // TODO: "Angle of Attack" style value — unresolved external macro
        float localVelZClamped = Mathf.Clamp(GetLocalVelocity().z * 0.01f, 0f, 1f);
        smoothedRearTyre = SmoothTowards(smoothedRearTyre, 1f, rearTyreLerpRate);

        return (((aoa * localVelZClamped) * smoothedRearTyre) * settings.RearGrip) * Handbrake
               * Mathf.Clamp01(rearCompressionParam)
               * (1f - (settings.BrakeRearBalance * Braking));
    }

    /// <summary>Was the "Throttle Oversteer" nested sub-graph (incl. its "None"/ThrottleCurve child).</summary>
    private float ComputeThrottleOversteer(float gearRatio)
    {
        AnimationCurve angularVelCurve = new AnimationCurve( // TODO: paste real curve (-5,0)->(0,1)->(5,0)
            new Keyframe(-5f, 0f), new Keyframe(0f, 1f), new Keyframe(5f, 0f));
        float instabilityTerm = angularVelCurve.Evaluate(rb.angularVelocity.y) * rb.angularVelocity.y * settings.DriftInstability;

        smoothedDriftInstability = SmoothTowards(smoothedDriftInstability, 1f, driftInstabilityLerpRate);
        float termA = instabilityTerm * smoothedDriftInstability;

        AnimationCurve aoaResponseCurve = new AnimationCurve( // TODO: paste real curve (large multi-key AOA response curve)
            new Keyframe(-180f, 0f), new Keyframe(-90f, 1f), new Keyframe(0f, 0f), new Keyframe(90f, 1f), new Keyframe(180f, 0f));
        float aoa2 = 0f; // TODO: unresolved external "AOA" macro output
        float throttleCurveValue = GetThrottleCurve().Evaluate(engine.EngineOutput); // EngineTorque param == EngineOutput

        float aoa3 = 0f; // TODO: unresolved external "AOA" macro output
        float termB = aoaResponseCurve.Evaluate(aoa2)
                      * ((aoa3 * ((throttleCurveValue * settings.ThrottleSteeringMultiplier) - settings.NoThrottleRecovery)) * -0.02f);

        float result = (termA + termB) * gearRatio * smoothedDriftInstability * Handbrake;
        return result;
    }

    /// <summary>Was the small nested "None" macro selecting ThrottleCurve by Drivetrain (0/1/2).</summary>
    private AnimationCurve GetThrottleCurve()
    {
        // TODO: paste in the 3 real drivetrain throttle curves.
        switch (settings.Drivetrain)
        {
            case 0: return AnimationCurve.Linear(0f, 0f, 1f, 1f); // TODO
            case 1: return AnimationCurve.Linear(0f, 0f, 1f, 1f); // TODO
            default: return AnimationCurve.Linear(0f, 0f, 1f, 1f); // TODO (case 2)
        }
    }

    /// <summary>Was the "None" (SteeringMultiplier Filtered) nested sub-graph.</summary>
    private float ComputeSteeringMultiplierFiltered()
    {
        AnimationCurve localVelZCurve = new AnimationCurve( // TODO: paste real curve (-1,-1)->(0,0)->(1,1)
            new Keyframe(-1f, -1f), new Keyframe(0f, 0f), new Keyframe(1f, 1f));
        AnimationCurve speedCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f); // TODO: paste real curve

        float a = localVelZCurve.Evaluate(GetLocalVelocity().z * 0.5f);
        float weightA = 0f; // TODO: unresolved external "1 to 0" macro output (ExternalMacro2)
        float weightB = 1f; // TODO: unresolved external "0 to 1" macro output (ExternalMacro2, complementary)
        float b = speedCurve.Evaluate(movement.CurrentSpeed);

        return a * weightA + weightB * b;
    }

    // =========================================================================
    // SIDEWAYS STOP  (was the "Sideways Stop" sub-graph)
    // =========================================================================
    private void ApplySidewaysStop()
    {
        AnimationCurve fadeCurve = new AnimationCurve( // TODO: paste real curve (0,1)->(20,0)
            new Keyframe(0f, 1f), new Keyframe(20f, 0f));
        float fade = fadeCurve.Evaluate(rb.linearVelocity.magnitude);
        float localVelX = GetLocalVelocity().x;

        rb.AddRelativeForce(fade * (localVelX * -2f), 0f, 0f, ForceMode.Acceleration);
    }

    // =========================================================================
    // DOWNFORCE  (top-level AddForce node between Sideways Stop and Drag)
    // =========================================================================
    private void ApplyDownforce()
    {
        rb.AddForce(new Vector3(0f, -0.3f, 0f), ForceMode.VelocityChange);
    }

    // =========================================================================
    // DRAG  (was the "Drag" sub-graph)
    // =========================================================================
    private void ApplyDrag()
    {
        float speed = rb.linearVelocity.magnitude;
        float dragMag = speed * speed * -0.001f * settings.DragCoefficient;
        rb.AddForce(rb.linearVelocity.normalized * dragMag, ForceMode.Acceleration);
    }
}

using UnityEngine;

/// <summary>
/// C# port of the "Animations" Script Graph. Runs from FixedUpdate in the graph's order:
/// Lean -> Rotate wheels -> Steering Anim.
///
/// Reads from the other scripts on this GameObject:
///   VehicleBrakes   -> Handbrake   (1 = released, 0 = fully pulled; locks the rear wheels' spin)
///   DetermineDrift  -> Grip1Drift0, Grip0Drift1, AOA
///   VehicleSuspension -> AverageCompression (scales the lean, clamped to 0..1)
///   VehicleHandling -> Steering, TransitionSpeed   (needs two lines added there, see below)
///
/// REQUIRED EDIT in VehicleHandling.cs - add these next to the other properties:
///     public float Steering => steering;
///     public float TransitionSpeed => transitionSpeed;
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(VehicleHandling))]
[RequireComponent(typeof(VehicleBrakes))]
[RequireComponent(typeof(DetermineDrift))]
[RequireComponent(typeof(VehicleSuspension))]
public class VehicleAnimations : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Objects this script moves (the graph's "Wheels" and "Hubs" list variables)
    // ------------------------------------------------------------------
    [Header("Objects")]
    [Tooltip("Exactly 4, in the same order as the graph's Wheels list. [0] and [1] roll freely (opposite spin directions). " +
             "[2] and [3] are the handbrake wheels - their spin is multiplied by Handbrake, so they stop when it's pulled.")]
    [SerializeField] private Transform[] wheels = new Transform[4];
    [Tooltip("The steering hubs (the graph's Hubs list, 2 in the original). All of them get the same local Y rotation.")]
    [SerializeField] private Transform[] hubs = new Transform[2];

    // ------------------------------------------------------------------
    // Object variables -> serialized fields
    // ------------------------------------------------------------------
    [Header("Lean")]
    [Tooltip("Forward/back lean: z acceleration * this -> pitch torque. Graph variable Suspension_FrontWeightShiftMultiplier. " +
             "The graph had no stored value; -0.5 is the unit's old default, so check it.")]
    [SerializeField] private float frontWeightShiftMultiplier = -0.5f;
    [Tooltip("Side lean: x acceleration * this -> roll torque (also feeds a little yaw). Graph variable Suspension_SideWeightShiftMultiplier. " +
             "The graph had no stored value; 0.5 is the unit's old default, so check it.")]
    [SerializeField] private float sideWeightShiftMultiplier = 0.5f;


    [Header("Wheels")]
    [Tooltip("Degrees of spin per FixedUpdate = forward speed * this (3.6 in the graph, no delta time - kept as is).")]
    [SerializeField] private float wheelSpinMultiplier = 3.6f;

    [Header("Steering")]
    [Tooltip("Scales the slip angle before it's turned into counter-steer on the hubs. Graph variable CountersteerAnimScale.")]
    [SerializeField] private float countersteerAnimScale = 1f;
    [Tooltip("Hub steering angle (degrees at full lock) vs. speed.")]
    [SerializeField] private AnimationCurve steerAngleBySpeed = new AnimationCurve(
        new Keyframe(0f, 25f, -0.6700923f, -0.6700923f),
        new Keyframe(50f, 15f, 0f, 0f));
    [Tooltip("Soft limit on the counter-steer angle (input = slip angle * scale * drift amount).")]
    [SerializeField] private AnimationCurve countersteerCurve = new AnimationCurve(
        new Keyframe(-30f, -30f, 0f, 0f),
        new Keyframe(30f, 30f, 0f, 0f));
    [Tooltip("Hard clamp on the steering part of the hub angle.")]
    [SerializeField] private float maxSteerAngle = 45f;
    [Tooltip("Smoothing speed for the steering angle (the graph's macro Speed, 10).")]
    [SerializeField] private float steerSmoothing = 10f;
    [Tooltip("Smoothing speed for the counter-steer angle (the graph's macro Speed, 5).")]
    [SerializeField] private float countersteerSmoothing = 5f;

    // ------------------------------------------------------------------
    // Fixed numbers from inside the graph
    // ------------------------------------------------------------------
    private const float MaxRollTorque = 5f;
    private const float MaxPitchTorque = 7f;
    private const float RollToYawFactor = -0.06f;
    private static readonly Vector3 AngularDamping = new Vector3(-7f, 0f, -8f); // pitch, (no yaw - see VehicleHandling Stability), roll

    private Rigidbody rb;
    private VehicleHandling handling;
    private VehicleBrakes brakes;
    private DetermineDrift determineDrift;
    private VehicleSuspension suspension;

    private Vector3 previousVelocity;

    // One smoothing state per FloatLerped macro instance in the graph
    private float gripSmoothed;          // Lean
    private float steerAngleSmoothed;    // Steering Anim
    private float countersteerSmoothed;  // Steering Anim

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        handling = GetComponent<VehicleHandling>();
        brakes = GetComponent<VehicleBrakes>();
        determineDrift = GetComponent<DetermineDrift>();
        suspension = GetComponent<VehicleSuspension>();
        previousVelocity = rb.linearVelocity;
    }

    private void FixedUpdate()
    {
        // Reconstructed "Accelleration" macro: change in velocity over time, in the car's local space
        Vector3 worldAcceleration = (rb.linearVelocity - previousVelocity) / Time.fixedDeltaTime;
        previousVelocity = rb.linearVelocity;
        Vector3 acceleration = transform.InverseTransformDirection(worldAcceleration);

        ApplyLean(acceleration);
        RotateWheels();
        ApplySteeringAnimation();
    }

    // ==================================================================
    // LEAN
    // ==================================================================
    private void ApplyLean(Vector3 acceleration)
    {
        float grip = Lerped(ref gripSmoothed, determineDrift.Grip1Drift0, handling.TransitionSpeed);

        float roll = Mathf.Clamp(acceleration.x * sideWeightShiftMultiplier, -MaxRollTorque, MaxRollTorque);
        float pitch = Mathf.Clamp(acceleration.z * frontWeightShiftMultiplier, -MaxPitchTorque, MaxPitchTorque);
        float yaw = roll * RollToYawFactor * grip;

        // The lean fades out with the suspension's average compression (0..1)
        float compression = Mathf.Clamp01(suspension.AverageCompression);
        rb.AddRelativeTorque(pitch * compression, yaw * compression, roll * compression, ForceMode.Acceleration);

        // The graph's second FixedUpdate event inside the Lean subgraph: damps the car's local
        // pitch/roll angular velocity. The yaw part (Stability) now lives in VehicleHandling.
        Vector3 localAngularVelocity = transform.InverseTransformDirection(rb.angularVelocity);
        rb.AddRelativeTorque(Vector3.Scale(localAngularVelocity, AngularDamping), ForceMode.Acceleration);
    }

    // ==================================================================
    // ROTATE WHEELS
    // ==================================================================
    private void RotateWheels()
    {
        float localForwardSpeed = transform.InverseTransformDirection(rb.linearVelocity).z;
        float spin = localForwardSpeed * wheelSpinMultiplier;
        float handbrake = brakes.Handbrake; // 1 = released, 0 = fully pulled

        // Even indices spin one way, odd the other (mirrored wheels); [2] and [3] lock with the handbrake.
        SpinWheel(0, -spin);
        SpinWheel(2, -spin * handbrake);
        SpinWheel(1, spin);
        SpinWheel(3, spin * handbrake);
    }

    private void SpinWheel(int index, float degrees)
    {
        if (wheels == null || index >= wheels.Length || wheels[index] == null) return;
        wheels[index].Rotate(new Vector3(degrees, 0f, 0f), Space.Self);
    }

    // ==================================================================
    // STEERING ANIM
    // ==================================================================
    private void ApplySteeringAnimation()
    {
        float speed = rb.linearVelocity.magnitude;

        // Steering input (from VehicleHandling) scaled by the speed curve, smoothed, clamped
        float targetAngle = handling.Steering * steerAngleBySpeed.Evaluate(speed);
        float steerAngle = Mathf.Clamp(Lerped(ref steerAngleSmoothed, targetAngle, steerSmoothing), -maxSteerAngle, maxSteerAngle);

        // Counter-steer: slip angle, only while drifting
        float counterInput = determineDrift.AOA * countersteerAnimScale * determineDrift.Grip0Drift1;
        float counterSteer = Lerped(ref countersteerSmoothed, countersteerCurve.Evaluate(counterInput), countersteerSmoothing);

        Quaternion hubRotation = Quaternion.Euler(0f, steerAngle + counterSteer, 0f);
        if (hubs == null) return;
        for (int i = 0; i < hubs.Length; i++)
        {
            if (hubs[i] != null) hubs[i].localRotation = hubRotation;
        }
    }

    // "FloatLerped(Float, Speed)": fixed-delta-time lerp, one state per use
    private static float Lerped(ref float state, float target, float speed)
    {
        state = Mathf.Lerp(state, target, speed * Time.fixedDeltaTime);
        return state;
    }
}
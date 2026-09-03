using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Converted from the "Braking" visual scripting graph. Pulls FrontCompression/
/// RearCompression from VehicleSuspension (the graph originally took these as its own
/// input ports, meant to be wired from the Suspension graph's matching output ports),
/// blends front/rear brake force by BrakeRearBalance, adds a rear-only handbrake force
/// scaled by rear compression, and applies it all as a single AddForce opposing the
/// car's current velocity direction.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VehicleBrakes : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("The regular brake input action (used raw, no smoothing — matches the original).")]
    [SerializeField] private InputActionReference brakeAction;
    [Tooltip("The handbrake input action (smoothed before use — see Handbrake Smoothing below).")]
    [SerializeField] private InputActionReference handbrakeAction;

    [Header("Brake Settings")]
    [Tooltip("0 = all braking on the front axle, 1 = all on the rear. Only affects the regular brake, not the handbrake (which is always rear-only).")]
    [SerializeField, Range(0f, 1f)] private float brakeRearBalance = 0.9f;
    [Tooltip("Regular brake torque.")]
    [SerializeField] private float brakeTorque = 10f;

    [Header("Handbrake (wasn't in your list — please double-check)")]
    [Tooltip("Handbrake torque. The graph reads this from an Object variable with no stored default, so I couldn't recover the real value — defaulted to match BrakeTorque. Set this to whatever you actually had.")]
    [SerializeField] private float handbrakeTorque = 10f;
    [Tooltip("How fast the handbrake input smooths toward its target (the graph's subgraph node took this as a \"Speed\" input, default 5).")]
    [SerializeField] private float handbrakeSmoothSpeed = 5f;

    // The graph fed the handbrake input through a macro subgraph (a separate .asset I
    // wasn't given) with inputs "Float" and "Speed" and an output called "FloatLerped".
    // I couldn't see what's inside it, so this is a reconstruction — a straightforward
    // Lerp toward the target at handbrakeSmoothSpeed, which matches the "Lerped" naming.
    // If the feel is off, send me the macro asset (or describe it) and I'll match it exactly.
    private float handbrakeSmoothed;

    // Only Handbrake is meant to be read by other scripts, per your note — Braking is
    // internal to this graph (fed straight from the raw brake input, unsmoothed).
    private float braking;

    /// <summary>
    /// Inverted on purpose, matching the original: 1 = handbrake released, 0 = fully applied.
    /// Other scripts should read it in this inverted form, same as the visual graph.
    /// </summary>
    public float Handbrake { get; private set; }

    private Rigidbody rb;
    private VehicleSuspension suspension;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        suspension = GetComponent<VehicleSuspension>();
    }

    private void OnEnable()
    {
        brakeAction?.action?.Enable();
        handbrakeAction?.action?.Enable();
    }

    private void OnDisable()
    {
        brakeAction?.action?.Disable();
        handbrakeAction?.action?.Disable();
    }

    private void FixedUpdate()
    {
        float rawHandbrakeInput = handbrakeAction != null && handbrakeAction.action != null
            ? handbrakeAction.action.ReadValue<float>()
            : 0f;
        handbrakeSmoothed = Mathf.Lerp(handbrakeSmoothed, rawHandbrakeInput, handbrakeSmoothSpeed * Time.fixedDeltaTime);
        Handbrake = 1f - handbrakeSmoothed;

        braking = brakeAction != null && brakeAction.action != null
            ? brakeAction.action.ReadValue<float>()
            : 0f;

        // Un-invert Handbrake back to a normal 0..1 "how applied" value for our own use,
        // same round trip the original graph does.
        float handbrakeApplied = 1f - Handbrake;

        float frontCompression = suspension != null ? suspension.FrontAxleCompression : 0f;
        float rearCompression = suspension != null ? suspension.RearAxleCompression : 0f;

        float frontBias = 1f - brakeRearBalance;
        float weightedCompression = (rearCompression * brakeRearBalance) + (frontCompression * frontBias);

        float brakeForce = weightedCompression * braking * brakeTorque;
        float handbrakeForce = handbrakeApplied * rearCompression * handbrakeTorque;
        float totalForce = handbrakeForce + brakeForce;

        // Softens the force to zero as the car nears a stop, instead of yanking it
        // backward once velocity reaches zero.
        float speed = rb.linearVelocity.magnitude;
        float speedFactor = Mathf.Clamp(-speed, -1f, 1f);
        float scalar = speedFactor * totalForce;

        Vector3 velocityDir = rb.linearVelocity.normalized;
        Vector3 force = new Vector3(scalar * velocityDir.x, 0f, scalar * velocityDir.z);

        rb.AddForce(force, ForceMode.Acceleration);
    }
}

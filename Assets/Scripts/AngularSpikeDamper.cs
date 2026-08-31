using UnityEngine;

/// <summary>
/// Damps sudden yaw (Y-axis) angular velocity spikes exponentially, while
/// leaving normal steering-range rotation untouched.
///
/// Unlike the collision-based scripts, this doesn't care WHY the angular
/// velocity spiked (a wall tail-clip, another car, a physics quirk) — it
/// just watches rb.angularVelocity.y every FixedUpdate and reacts purely
/// based on magnitude. This also sidesteps the inertia-tensor issues from
/// counter-torquing collision impulses, since it directly reads/writes
/// angularVelocity instead of trying to predict what a torque impulse would do.
///
/// Behavior:
///   |angularVelocity.y| <= steeringThreshold  -> untouched (normal steering)
///   |angularVelocity.y| >  steeringThreshold  -> exponentially damped back
///                                                 down, faster the further
///                                                 over the threshold it is
///
/// Intended to be used ALONGSIDE FakeWallCollision (or standalone) as a
/// safety net against any spin spike that sneaks through, from any source.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class AngularSpikeDamper : MonoBehaviour
{
    [Header("Threshold")]
    [Tooltip("Yaw angular velocity (degrees/sec) below which nothing is touched. Set this comfortably above your fastest normal steering rate.")]
    [SerializeField] private float steeringThresholdDegPerSec = 180f;

    [Header("Damping Response")]
    [Tooltip("How quickly the damping ramps to full strength as excess speed grows. Higher = sharper cutoff (steering feels untouched right up to the threshold, then impacts get crushed hard). Lower = softer, more gradual damping.")]
    [SerializeField] private float sharpness = 0.05f;

    [Tooltip("Overall strength multiplier on the correction applied per second.")]
    [SerializeField] private float dampingStrength = 15f;

    [Header("Safety Clamp (optional)")]
    [Tooltip("Absolute hard cap on yaw angular velocity, degrees/sec. Set to 0 to disable.")]
    [SerializeField] private float maxAngularVelocityDegPerSec = 720f;

    [Header("Debug")]
    [SerializeField] private bool debugMode = false;

    private Rigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    private void FixedUpdate()
    {
        Vector3 angVel = _rb.angularVelocity;
        float yDeg = angVel.y * Mathf.Rad2Deg;
        float absY = Mathf.Abs(yDeg);

        float thresholdRad = steeringThresholdDegPerSec;
        if (absY <= thresholdRad)
        {
            // Within normal steering range — leave untouched.
            ApplyHardClampIfNeeded(ref angVel);
            return;
        }

        float excess = absY - thresholdRad;

        // 0..1, approaches 1 quickly as excess grows — this is what gives the
        // "steering barely touched, impacts crushed fast" exponential feel.
        float reductionFactor = 1f - Mathf.Exp(-excess * sharpness);

        // Per-step correction amount, scaled by dt so it's a smooth rate of
        // decay rather than an instant snap.
        float correction = excess * reductionFactor * dampingStrength * Time.fixedDeltaTime;

        // Never remove more than the excess itself — guarantees we can't
        // overshoot past the threshold or flip the sign (no oscillation).
        correction = Mathf.Min(correction, excess);

        float newYDeg = (absY - correction) * Mathf.Sign(yDeg);
        angVel.y = newYDeg * Mathf.Deg2Rad;

        ApplyHardClampIfNeeded(ref angVel);

        _rb.angularVelocity = angVel;

        if (debugMode)
        {
            Debug.Log($"[AngularSpikeDamper] '{gameObject.name}' yaw {yDeg:F1} deg/s, excess {excess:F1}, removed {correction:F1} deg/s -> {newYDeg:F1} deg/s", this);
        }
    }

    private void ApplyHardClampIfNeeded(ref Vector3 angVel)
    {
        if (maxAngularVelocityDegPerSec <= 0f) return;

        float yDeg = angVel.y * Mathf.Rad2Deg;
        float clamped = Mathf.Clamp(yDeg, -maxAngularVelocityDegPerSec, maxAngularVelocityDegPerSec);

        if (!Mathf.Approximately(clamped, yDeg))
        {
            angVel.y = clamped * Mathf.Deg2Rad;
            _rb.angularVelocity = angVel;
        }
    }
}

using UnityEngine;

/// <summary>
/// Converted from the "Determine Drift" visual scripting graph. Computes a grip value
/// (via GripCurve, keyed on slip angle) and exposes it as two complementary flags for
/// other scripts to read. Grip is forced to 1 (no drift) while in reverse.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DetermineDrift : MonoBehaviour
{
    [Header("Tire Curve")]
    [Tooltip("Grip vs. absolute slip angle (degrees). Pre-filled from your Variables.preset, so this should already match.")]
    [SerializeField] private AnimationCurve gripCurve = BuildDefaultGripCurve();

    [Header("Slip Scale (graph-internal defaults, both 0.02 in the original)")]
    [Tooltip("Scales horizontal speed before it gates the curve lookup (speed * this, clamped 0-1).")]
    [SerializeField] private float minimumSpeedForSlipScale = 0.02f;
    [Tooltip("Scales horizontal acceleration into a multiplier (1 + accel * this) applied to the slip angle — harder acceleration reads as a larger effective slip angle.")]
    [SerializeField] private float minimumAccelerationForSlipScale = 0.02f;

    [Header("Debug")]
    [Tooltip("Show Grip / Grip1Drift0 / Grip0Drift1 / AOA top-left of the screen in Play Mode. Drawn below VehicleGearbox's overlay.")]
    [SerializeField] private bool showDebugOverlay = false;

    /// <summary>Grip means 1, Drifting means 0. One of the two outputs other scripts should read.</summary>
    public float Grip1Drift0 { get; private set; }
    /// <summary>Grip means 0, Drifting means 1. The other of the two outputs other scripts should read.</summary>
    public float Grip0Drift1 { get; private set; }
    /// <summary>
    /// Angle of attack / slip angle in degrees, signed (positive/negative = which way the
    /// car is sliding). Exposed publicly so future scripts that need slip angle can just
    /// read this instead of re-deriving it — see the reconstruction note below.
    /// </summary>
    public float AOA { get; private set; }

    private Rigidbody rb;
    private VehicleGearbox gearbox;
    private Vector3 previousVelocity;
    private float grip;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        gearbox = GetComponent<VehicleGearbox>();
    }

    private void FixedUpdate()
    {
        Vector3 velocity = rb.linearVelocity;

        // --- Reconstructed "Accelleration" subgraph ---
        // The graph called out to an external macro (not included in what you gave me)
        // that outputs an "Accelleration" vector with no visible inputs. A plain
        // velocity-delta-over-time is the standard way to get this, so that's what I
        // used. If that's not what the macro actually does, let me know and I'll fix it.
        Vector3 acceleration = (velocity - previousVelocity) / Time.fixedDeltaTime;
        previousVelocity = velocity;

        // --- Reconstructed "AOA" (angle of slip) subgraph ---
        // Same situation — this is a separate macro with no visible inputs. Signed angle
        // between forward and flattened velocity is the standard slip-angle formula, and
        // matches the Abs() applied right after it reads this value. You mentioned future
        // scripts will reuse this same macro — once you show me its actual contents I'll
        // correct this (and this script) to match exactly.
        Vector3 flatVelocity = new Vector3(velocity.x, 0f, velocity.z);
        AOA = Vector3.SignedAngle(transform.forward, flatVelocity, transform.up);

        // --- From here down, this is the graph's actual math, unchanged ---
        int drivetrainDirection = gearbox != null ? gearbox.DrivetrainDirection : 1;
        bool reversing = Mathf.Clamp01(drivetrainDirection) < 0.5f;

        if (reversing)
        {
            grip = 1f;
        }
        else
        {
            float horizontalSpeed = flatVelocity.magnitude;
            float speedTerm = Mathf.Clamp01(horizontalSpeed * minimumSpeedForSlipScale);

            Vector3 flatAcceleration = new Vector3(acceleration.x, 0f, acceleration.z);
            float accelTerm = 1f + flatAcceleration.magnitude * minimumAccelerationForSlipScale;

            float curveInput = Mathf.Abs(AOA) * accelTerm * speedTerm;
            grip = gripCurve.Evaluate(curveInput);
        }

        Grip1Drift0 = grip;
        Grip0Drift1 = 1f - grip;
    }

    private static AnimationCurve BuildDefaultGripCurve()
    {
        AnimationCurve curve = new AnimationCurve(
            new Keyframe(0f, 1f, 0f, 0f),
            new Keyframe(4f, 1f, 0f, 0f),
            new Keyframe(8f, 0f, -0.6569455f, 0f),
            new Keyframe(180f, 0f, 0f, 0f)
        );
        curve.preWrapMode = WrapMode.ClampForever;
        curve.postWrapMode = WrapMode.ClampForever;
        return curve;
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || !Application.isPlaying) return;

        // Starts at y=160, below VehicleGearbox's overlay (10/28/46 for Suspension, 70-142 for Gearbox).
        GUI.Label(new Rect(10, 160, 320, 20), $"Grip: {grip:F3}");
        GUI.Label(new Rect(10, 178, 320, 20), $"Grip1Drift0: {Grip1Drift0:F3}");
        GUI.Label(new Rect(10, 196, 320, 20), $"Grip0Drift1: {Grip0Drift1:F3}");
        GUI.Label(new Rect(10, 214, 320, 20), $"AOA (slip angle): {AOA:F2}");
    }
}

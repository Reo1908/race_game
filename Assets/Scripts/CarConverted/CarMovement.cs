using UnityEngine;

/// <summary>
/// Converted from the "Movement" sub-graph inside Physics.asset.
/// This is the piece that actually drives the car: it keeps the rigidbody
/// tracking the spline (SplineCalculator), works out the current drift
/// angle, and applies the grip/drift-grip/handbrake force each FixedUpdate.
///
/// Split into its own script (as requested) since this is the part you'll
/// be iterating on — throttle/engine response lives here via EngineOutput.
///
/// NOTE ON MISSING PIECES: 4 external "float-lerp" smoothing macros referenced
/// by the original graph weren't included in the uploaded Physics.asset.
/// Those are stubbed below with TODO markers — plug your real curves/lerp
/// rates back in there. SplineProbeBridge is your real implementation.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarMovement : MonoBehaviour
{
    [SerializeField] private CarPhysicsSettings settings;

    private Rigidbody rb;
    private SplineProbeBridge splineProbe;

    // Runtime state the original graph wrote to Object variables for
    // (kept as normal fields here — see CarPhysicsSettings for why).
    public float CurrentDriftAngle { get; private set; }
    public float CurrentSpeed { get; private set; }

    // Local graph-scope state (was a "Graph" kind variable, not Object).
    private float turning;

    // ---- TODO: external smoothing macros (not included in Physics.asset) ----
    // Each of these replaced a small reusable "float lerp/smoothing" macro
    // graph. Wire in your actual smoothing/response curves here; these are
    // placeholder exponential smoothers so the rest of the code compiles
    // and runs sensibly in the meantime.
    [Header("TODO: paste your smoothing curves/rates in here")]
    [SerializeField] private float gripLerpRate = 10f;
    [SerializeField] private float driftGripLerpRate = 10f;
    [SerializeField] private float laneOffsetLerpRate = 10f;

    private float smoothedGripForceX;      // was ExternalMacro18(...).FloatLerped
    private float smoothedDriftGripForceX; // was ExternalMacro23(...).FloatLerped
    private float smoothedLaneOffsetY;     // was ExternalMacro24(...).FloatLerped

    private static float SmoothTowards(float current, float target, float rate)
    {
        // TODO: replace with the real smoothing/curve logic from the
        // original macro if this simple exponential smoothing isn't a match.
        return Mathf.Lerp(current, target, 1f - Mathf.Exp(-rate * Time.fixedDeltaTime));
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        // was: InvokeMember GetComponent("SplineProbeBridge") -> SplineProbe (Graph var)
        splineProbe = GetComponent<SplineProbeBridge>();
    }

    private void FixedUpdate()
    {
        if (splineProbe == null || settings.SplineCalculator == null) return;

        Transform spline = settings.SplineCalculator;

        // --- Follow the spline's tangent direction with the reference transform ---
        Vector3 tangent = splineProbe.GetTangent(spline.position).normalized;
        Quaternion lookRotation = Quaternion.LookRotation(tangent, Vector3.up);

        // TODO: replace with the real "lane offset" smoothing macro (ExternalMacro24)
        smoothedLaneOffsetY = SmoothTowards(smoothedLaneOffsetY, 0f, laneOffsetLerpRate);
        Vector3 laneOffsetEuler = new Vector3(0f, smoothedLaneOffsetY, 0f);

        spline.rotation = Quaternion.Euler(lookRotation.eulerAngles + laneOffsetEuler);

        // --- Drift angle: signed angle between car forward and spline forward, flattened ---
        Vector3 carForwardFlat = Vector3.Scale(transform.forward, new Vector3(1f, 0f, 1f)).normalized;
        Vector3 splineForwardFlat = Vector3.Scale(spline.forward, new Vector3(1f, 0f, 1f)).normalized;
        float angle = Vector3.Angle(carForwardFlat, splineForwardFlat);
        // (graph multiplies the raw angle by the dot product value itself, not just its sign —
        //  preserved exactly below to match original behaviour)
        float crossDot = Vector3.Dot(Vector3.Cross(carForwardFlat, splineForwardFlat), transform.up);
        CurrentDriftAngle = angle * crossDot;

        // --- Grip / drift-grip / handbrake combined lateral force ---
        float velRightDot = Vector3.Dot(rb.linearVelocity, transform.right);
        Vector3 gripCounterForce = new Vector3(velRightDot * -1f * settings.Grip, 0f, velRightDot * -1f * settings.Grip);

        float velSplineRightDot = Vector3.Dot(rb.linearVelocity, spline.right);
        Vector3 driftGripCounterForce = new Vector3(velSplineRightDot * -1f * settings.DriftGrip, 0f, velSplineRightDot * -1f * settings.DriftGrip);

        // TODO: replace with real smoothing macros (ExternalMacro18 / ExternalMacro23)
        smoothedGripForceX = SmoothTowards(smoothedGripForceX, 1f, gripLerpRate);
        smoothedDriftGripForceX = SmoothTowards(smoothedDriftGripForceX, 1f, driftGripLerpRate);
        Vector3 gripSmoothVec = new Vector3(smoothedGripForceX, smoothedGripForceX, smoothedGripForceX);
        Vector3 driftGripSmoothVec = new Vector3(smoothedDriftGripForceX, smoothedDriftGripForceX, smoothedDriftGripForceX);

        Vector3 handbrakeVec = new Vector3(Handbrake, Handbrake, Handbrake);
        Vector3 compressionVec = new Vector3(compression, compression, compression);

        Vector3 gripComponent = Vector3.Scale(Vector3.Scale(transform.right, gripCounterForce), gripSmoothVec);
        Vector3 driftGripComponent = Vector3.Scale(Vector3.Scale(spline.right, driftGripCounterForce), driftGripSmoothVec);
        Vector3 combined = Vector3.Scale(gripComponent + driftGripComponent, handbrakeVec);
        combined = Vector3.Scale(combined, compressionVec);

        rb.AddForce(combined, ForceMode.Acceleration);

        // --- Speed / turning read-outs ---
        CurrentSpeed = Mathf.Abs(Vector3.Dot(rb.linearVelocity, spline.forward));
        turning = Vector3.Dot(rb.linearVelocity, spline.right) * -1f;
    }

    // ---- Values the rest of the system (Suspension) feeds in each frame ----
    private float compression;
    public void SetCompression(float value) => compression = value;

    // Written by CarPhysics' Handbrake sub-system; exposed here since Movement reads it.
    public float Handbrake { get; set; } = 1f;
}
using UnityEngine;

/// <summary>
/// Raycast spring/damper suspension for a 4-wheeled vehicle. Converted 1:1 from the
/// "Suspension" visual scripting graph (class renamed to VehicleSuspension to avoid a name clash): each corner raycasts down from its suspension
/// point, applies a spring+damper force at that point, and moves its hub transform to
/// match how compressed the suspension is. Front/Rear/AverageCompression are exposed
/// for other systems (tire grip, camera shake, etc.) to read.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VehicleSuspension : MonoBehaviour
{
    // One Transform per corner, always in Front Left / Front Right / Rear Left / Rear Right order.
    [System.Serializable]
    public struct CornerTransforms
    {
        public Transform frontLeft;
        public Transform frontRight;
        public Transform rearLeft;
        public Transform rearRight;
    }

    [Header("Wheels")]
    [Tooltip("Wheel diameter, front axle (m). Used to offset the hub so the wheel bottom sits on the ground.")]
    [SerializeField] private float wheelDiameterFront = 0.55f;
    [Tooltip("Wheel diameter, rear axle (m).")]
    [SerializeField] private float wheelDiameterRear = 0.55f;

    [Header("Suspension")]
    [Tooltip("Full suspension travel and raycast length (m).")]
    [SerializeField] private float suspensionHeight = 0.2f;
    [Tooltip("Spring strength, front axle.")]
    [SerializeField] private float suspensionStrengthF = 3000f;
    [Tooltip("Spring strength, rear axle.")]
    [SerializeField] private float suspensionStrengthR = 3000f;
    [Tooltip("Damping coefficient, shared by both axles.")]
    [SerializeField] private float suspensionDamping = 820f;
    [Tooltip("Upper clamp on the damping term.")]
    [SerializeField] private float dampingMaxPositive = 200f;
    [Tooltip("Lower clamp on the damping term.")]
    [SerializeField] private float dampingMaxNegative = -5000f;

    [Header("Raycast / Grip")]
    [Tooltip("Wasn't in your list, but the graph used it (a graph-level variable defaulting to 512) to filter the suspension raycasts. Double-check this matches your ground layer.")]
    [SerializeField] private LayerMask groundLayerMask = 512;
    [Tooltip("Wasn't in your list either. The graph multiplies each corner's compression by this before averaging it into Front/Rear/AverageCompression, but its value lives on the GameObject's Variables component, not in the graph asset, so I couldn't recover it. Defaulted to 1 (no-op) — set this to whatever you actually had.")]
    [SerializeField] private float compressionGripMultiplier = 1f;

    [Header("Suspension Points (order: Front Left, Front Right, Rear Left, Rear Right)")]
    [SerializeField] private CornerTransforms suspensionPoints;

    [Header("Hubs (order: Front Left, Front Right, Rear Left, Rear Right)")]
    [SerializeField] private CornerTransforms hubs;

    [Header("Debug")]
    [Tooltip("Show FrontAxleCompression / RearAxleCompression / AverageCompression top-left of the screen in Play Mode.")]
    [SerializeField] private bool showDebugOverlay = false;

    // Results for other scripts to read. Not used internally yet, but always kept up to date.
    public float FrontAxleCompression { get; private set; }
    public float RearAxleCompression { get; private set; }
    public float AverageCompression { get; private set; }

    private Rigidbody rb;
    private Transform[] points; // cached corner order: 0=FL, 1=FR, 2=RL, 3=RR
    private Transform[] hubArr; // same order

    // Per-corner state that persists between frames — matches HeightFL..HeightRR and
    // CompressionFL..CompressionRR in the graph. If a corner's raycast doesn't hit
    // (wheel off the ground beyond suspensionHeight), these simply hold their last
    // value rather than resetting, same as the original.
    private readonly float[] cornerHeight = new float[4];
    private readonly float[] cornerCompression = new float[4];

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        points = new[] { suspensionPoints.frontLeft, suspensionPoints.frontRight, suspensionPoints.rearLeft, suspensionPoints.rearRight };
        hubArr = new[] { hubs.frontLeft, hubs.frontRight, hubs.rearLeft, hubs.rearRight };
    }

    private void FixedUpdate()
    {
        for (int i = 0; i < 4; i++)
        {
            float strength = i < 2 ? suspensionStrengthF : suspensionStrengthR;
            ProcessCorner(i, strength);
        }

        UpdateAxleCompression();
        UpdateHubPositions();
    }

    private void ProcessCorner(int index, float strength)
    {
        Transform point = points[index];
        if (point == null) return;

        // Origin is the suspension point's local offset re-applied through the car
        // body's own transform (not point.position) — so only the body's orientation
        // affects the mount position, while the mount's own rotation only steers the
        // raycast direction below.
        Vector3 origin = transform.TransformPoint(point.localPosition);
        Vector3 direction = -point.up;

        if (Physics.Raycast(origin, direction, out RaycastHit hitInfo, suspensionHeight, groundLayerMask))
        {
            float height = suspensionHeight - hitInfo.distance; // how compressed this corner is
            cornerHeight[index] = height;

            float springForce = (height / suspensionHeight) * strength;

            // Damping is projected onto the car body's up vector (not the suspension
            // point's up) — this is a deliberate distinction in the original graph.
            float relativeVelocity = Vector3.Dot(rb.GetPointVelocity(origin), transform.up);
            float dampingForce = Mathf.Clamp(relativeVelocity * suspensionDamping, dampingMaxNegative, dampingMaxPositive);

            // Abs() means this always pushes up, never pulls down — kept as-is from the original.
            float appliedForce = Mathf.Abs(springForce - dampingForce) * 0.0001f;

            rb.AddForceAtPosition(new Vector3(0f, appliedForce, 0f), origin, ForceMode.VelocityChange);

            cornerCompression[index] = appliedForce;
        }
    }

    private void UpdateAxleCompression()
    {
        float fl = cornerCompression[0] * compressionGripMultiplier;
        float fr = cornerCompression[1] * compressionGripMultiplier;
        float rl = cornerCompression[2] * compressionGripMultiplier;
        float rr = cornerCompression[3] * compressionGripMultiplier;

        FrontAxleCompression = (fl + fr) * 0.5f;
        RearAxleCompression = (rl + rr) * 0.5f;
        AverageCompression = Mathf.Clamp01((fl + fr + rl + rr) * 0.25f);
    }

    private void UpdateHubPositions()
    {
        for (int i = 0; i < 4; i++)
        {
            Transform hub = hubArr[i];
            if (hub == null) continue;

            float wheelDiameter = i < 2 ? wheelDiameterFront : wheelDiameterRear;
            float y = Mathf.Clamp(cornerHeight[i] - (suspensionHeight - wheelDiameter * 0.5f), -suspensionHeight, suspensionHeight);
            hub.localPosition = new Vector3(0f, y, 0f);
        }
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || !Application.isPlaying) return;

        GUI.Label(new Rect(10, 10, 320, 20), $"Front Axle Compression: {FrontAxleCompression:F4}");
        GUI.Label(new Rect(10, 28, 320, 20), $"Rear Axle Compression: {RearAxleCompression:F4}");
        GUI.Label(new Rect(10, 46, 320, 20), $"Average Compression: {AverageCompression:F4}");
    }
}

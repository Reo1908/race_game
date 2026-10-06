using UnityEngine;

/// <summary>
/// C# port of the "DynamicCam" Script Graph (chase camera), with the helper transforms
/// (the graph's "Camera" list) replaced by plain math. Put this on the car.
///
/// The graph drove a chain of helper objects:
///   car -> Camera[0] (pivot) -> Camera[1] (roll joint) -> Camera[2] (the camera)
/// plus Camera[4], a point on the car used as the origin/orientation of the ground and wall rays.
/// Everything the graph wrote to Camera[0], Camera[1] and Camera[2] was overwritten every tick,
/// so those three helpers are now just math in FixedUpdate and only the camera itself is moved.
/// The only values that were NOT overwritten (and so have to be kept) are:
///   - Camera[1]'s local position  -> "Camera Offset"
///   - Camera[4]'s position/rotation -> "Sensor Offset" / "Sensor Euler Angles"
/// Fill them by hand, or use "Capture Offsets From Old Helpers" (right-click the component header).
///
/// Reads from DetermineDrift: AOA and Grip0Drift1 (the graph's two drift subgraphs).
/// Assumes the helper chain has no scale (all scale 1), like the graph effectively did.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(DetermineDrift))]
public class VehicleCamera : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Points (replace the helper objects)
    // ------------------------------------------------------------------
    [Header("Camera")]
    [Tooltip("The object that used to be Camera[2] - the camera (or its mount). It's moved in world space every FixedUpdate, " +
             "so take it out of the old helper chain (e.g. put it under the car or at the scene root) before deleting the helpers.")]
    [SerializeField] private Transform cameraTransform;
    [Tooltip("Where the camera sits relative to the pivot, in the pivot's rotated space. This is Camera[1]'s old local position.")]
    [SerializeField] private Vector3 cameraOffset;

    [Header("Sensor point (was Camera[4])")]
    [Tooltip("Origin of the ground and wall rays, in the car's local space. Camera[4]'s old local position.")]
    [SerializeField] private Vector3 sensorOffset;
    [Tooltip("Camera[4]'s rotation relative to the car (its up/right set the ray directions).")]
    [SerializeField] private Vector3 sensorEulerAngles;
    [Tooltip("Layers the downward ground ray hits (512 = layer 9 in the graph).")]
    [SerializeField] private LayerMask groundMask = 512;
    [SerializeField] private float groundRayLength = 3f;
    [Tooltip("Layers the left/right wall rays hit (1280 = layers 8 and 10 in the graph).")]
    [SerializeField] private LayerMask wallMask = 1280;
    [Tooltip("Max length of the wall rays; also the value used when a ray hits nothing.")]
    [SerializeField] private float wallRayLength = 2f;

    // ------------------------------------------------------------------
    // Graph variables -> serialized fields (defaults are the values stored in the graph)
    // ------------------------------------------------------------------
    [Header("Multipliers")]
    [Tooltip("How far the slip swings the pivot: x/y/z = XmoveMult / YMoveMult / ZMoveMult.")]
    [SerializeField] private Vector3 slipMoveMultiplier = new Vector3(0.7f, 0.3f, 0.8f);
    [Tooltip("Camera roll from the slip angle. Graph variable RotMult.")]
    [SerializeField] private float slipRollMultiplier = 8f;
    [Tooltip("Camera roll from being closer to one wall than the other. Graph variable CloseRotationMult.")]
    [SerializeField] private float closeRotationMultiplier = -3f;
    [Tooltip("Scales the smoothed forward acceleration before it goes into the curves. Graph variable AccellMultiplier.")]
    [SerializeField] private float accelMultiplier = 0.1f;
    [Tooltip("Camera moves back/forward along its z axis with acceleration. Graph variable ForwardAccellMoveMult.")]
    [SerializeField] private float forwardAccelMoveMultiplier = -0.4f;
    [Tooltip("Camera pitch from acceleration (clamped to 0..10 degrees). Graph variable ForwardAccellRotMult.")]
    [SerializeField] private float forwardAccelRotMultiplier = -4f;

    [Header("Smoothing")]
    [Tooltip("This graph's own TransitionSpeed variable (10) - used for the slip roll and the slip offset. Separate from VehicleHandling's.")]
    [SerializeField] private float transitionSpeed = 10f;
    [Tooltip("How fast the pivot's rotation follows the car's rotation (the graph passed 3 into its rotation-smoothing macro).")]
    [SerializeField] private float rotationFollowSpeed = 3f;
    [SerializeField] private float accelSmoothing = 2f;
    [SerializeField] private float groundSmoothing = 6f;
    [SerializeField] private float wallSmoothing = 2f;

    // ------------------------------------------------------------------
    // Curves (copied from the graph's AnimationCurve literals)
    // ------------------------------------------------------------------
    [Header("Curves")]
    [Tooltip("Pivot height vs. smoothed ground distance (-1..1).")]
    [SerializeField] private AnimationCurve groundHeightCurve = new AnimationCurve(
        new Keyframe(-1f, -0.5f, 0f, 0f),
        new Keyframe(-0.3153479f, -0.303295f, 0.6245428f, 0.6245428f),
        new Keyframe(0f, 0f, 0.9808894f, 0.9808894f),
        new Keyframe(0.7224823f, 0.7224822f, 0.6396627f, 0.6396627f),
        new Keyframe(1f, 0.8f, 0f, 0f));

    [Tooltip("Camera move along z vs. scaled acceleration.")]
    [SerializeField] private AnimationCurve forwardAccelCurve = new AnimationCurve(
        new Keyframe(-1f, -1f, 0f, 0f),
        new Keyframe(0f, 0f, 1.575279f, 1.575279f),
        new Keyframe(1f, 1f, 0f, 0f));

    [Tooltip("Camera pitch vs. scaled acceleration.")]
    [SerializeField] private AnimationCurve forwardAccelRotCurve = new AnimationCurve(
        new Keyframe(-1f, -1f, 0f, 0f),
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(1f, 1f, 0f, 0f));

    [Tooltip("Slip (AOA * Grip0Drift1 / 180) -> sideways pivot offset.")]
    [SerializeField] private AnimationCurve slipSideCurve = new AnimationCurve(
        new Keyframe(-1f, 0f, 0f, 0f),
        new Keyframe(-0.5f, 1f, 0f, 0f),
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(0.5f, -1f, 0f, 0f),
        new Keyframe(1.001465f, 0f, 0f, 0f));

    [Tooltip("Slip -> vertical pivot offset.")]
    [SerializeField] private AnimationCurve slipHeightCurve = new AnimationCurve(
        new Keyframe(-1f, 0f, 0f, 0f),
        new Keyframe(-0.5f, -1f, 0f, 0f),
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(0.5f, -1f, 0f, 0f),
        new Keyframe(1.001465f, 0f, 0f, 0f));

    [Tooltip("Slip -> forward/back pivot offset.")]
    [SerializeField] private AnimationCurve slipDepthCurve = new AnimationCurve(
        new Keyframe(-1f, 1f, 0f, 0f),
        new Keyframe(-0.5f, 1f, 0f, 0f),
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(0.5f, 1f, 0f, 0f),
        new Keyframe(1f, 1f, 0f, 0f));

    [Tooltip("Slip -> camera roll.")]
    [SerializeField] private AnimationCurve slipRollCurve = new AnimationCurve(
        new Keyframe(-1f, 0f, 0f, 0f),
        new Keyframe(-0.5f, 1f, 0f, 0f),
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(0.5f, -1f, 0f, 0f),
        new Keyframe(1.001465f, 0f, 0f, 0f));

    // ------------------------------------------------------------------
    // One-time helper for moving off the old hierarchy
    // ------------------------------------------------------------------
    [Header("One-time capture (optional)")]
    [Tooltip("Drag the old helper objects in here in the same order as the graph's Camera list (needs at least 5 entries), " +
             "then right-click this component's header > 'Capture Offsets From Old Helpers'. Afterwards you can clear this and delete the helpers.")]
    [SerializeField] private Transform[] oldCameraList;

    [ContextMenu("Capture Offsets From Old Helpers")]
    private void CaptureOffsetsFromOldHelpers()
    {
        if (oldCameraList == null || oldCameraList.Length < 5 || oldCameraList[1] == null || oldCameraList[4] == null)
        {
            Debug.LogWarning("VehicleCamera: fill 'Old Camera List' with the old helpers (at least 5 entries, [1] and [4] must be set).", this);
            return;
        }

#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Capture camera offsets");
#endif
        cameraOffset = oldCameraList[1].localPosition;
        sensorOffset = oldCameraList[4].localPosition;
        sensorEulerAngles = (Quaternion.Inverse(transform.rotation) * oldCameraList[4].rotation).eulerAngles;
        if (cameraTransform == null) cameraTransform = oldCameraList[2];
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------
    private const float MaxPitch = 10f;

    private Rigidbody rb;
    private DetermineDrift determineDrift;

    private Vector3 previousVelocity;

    // One smoothing state per smoothing macro/node instance in the graph
    private float accelSmoothed;       // FloatLerped, forward acceleration
    private float groundSmoothed;      // FloatLerped, ground distance
    private float wallSmoothed;        // FloatLerped, wall difference
    private float slipRollSmoothed;    // FloatLerped, slip roll
    private Vector3 slipOffsetSmoothed;      // Vector3 smoothing macro (start value 0,0,0)
    private Quaternion pivotRotationSmoothed; // Quaternion smoothing macro

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        determineDrift = GetComponent<DetermineDrift>();
        previousVelocity = rb.linearVelocity;
        pivotRotationSmoothed = transform.rotation;
    }

    private void FixedUpdate()
    {
        if (cameraTransform == null) return;

        // Reconstructed "Accelleration" macro: change in velocity over time, in the car's local space
        Vector3 worldAcceleration = (rb.linearVelocity - previousVelocity) / Time.fixedDeltaTime;
        previousVelocity = rb.linearVelocity;
        Vector3 localAcceleration = transform.InverseTransformDirection(worldAcceleration);

        // ---- Sensor point: ground ray + wall rays ----
        Vector3 sensorOrigin = transform.TransformPoint(sensorOffset);
        Quaternion sensorRotation = transform.rotation * Quaternion.Euler(sensorEulerAngles);
        Vector3 sensorUp = sensorRotation * Vector3.up;
        Vector3 sensorRight = sensorRotation * Vector3.right;

        // GroundDist is negative, and 0 when the ray hits nothing
        float groundDistance = Physics.Raycast(sensorOrigin, -sensorUp, out RaycastHit groundHit, groundRayLength, groundMask)
            ? -groundHit.distance
            : 0f;

        float leftWallDistance = Physics.Raycast(sensorOrigin, -sensorRight, out RaycastHit leftHit, wallRayLength, wallMask)
            ? leftHit.distance
            : wallRayLength;
        float rightWallDistance = Physics.Raycast(sensorOrigin, sensorRight, out RaycastHit rightHit, wallRayLength, wallMask)
            ? rightHit.distance
            : wallRayLength;

        // ---- Drift ("Get AOS" and "DetermineDrift" subgraphs -> DetermineDrift.cs) ----
        float slip = determineDrift.AOA * determineDrift.Grip0Drift1 / 180f;

        // ---- Pivot (was Camera[0]) ----
        // Height from the ground distance, plus the slip swing
        float groundHeight = groundHeightCurve.Evaluate(Mathf.Clamp(Lerped(ref groundSmoothed, groundDistance, groundSmoothing), -1f, 1f));
        Vector3 slipTarget = new Vector3(slipSideCurve.Evaluate(slip), slipHeightCurve.Evaluate(slip), slipDepthCurve.Evaluate(slip));
        slipOffsetSmoothed = Vector3.Lerp(slipOffsetSmoothed, slipTarget, transitionSpeed * Time.fixedDeltaTime);

        Vector3 pivotLocalPosition = new Vector3(0f, groundHeight, 0f) + Vector3.Scale(slipOffsetSmoothed, slipMoveMultiplier);
        Vector3 pivotPosition = transform.TransformPoint(pivotLocalPosition);

        // The pivot's rotation lags behind the car's rotation instead of following it directly
        pivotRotationSmoothed = Quaternion.Slerp(pivotRotationSmoothed, transform.rotation, rotationFollowSpeed * Time.fixedDeltaTime);
        Quaternion pivotRotation = pivotRotationSmoothed;

        // ---- Roll joint (was Camera[1]): roll from the slip plus the wall closeness ----
        float slipRoll = Lerped(ref slipRollSmoothed, slipRollCurve.Evaluate(slip), transitionSpeed) * slipRollMultiplier;
        float wallRoll = Lerped(ref wallSmoothed, rightWallDistance - leftWallDistance, wallSmoothing) * closeRotationMultiplier;
        float roll = slipRoll + wallRoll;

        Vector3 rollPosition = pivotPosition + pivotRotation * cameraOffset;
        Quaternion rollRotation = pivotRotation * Quaternion.Euler(0f, 0f, roll);

        // ---- Camera (was Camera[2]): moves along z and pitches with the acceleration ----
        float scaledAccel = Lerped(ref accelSmoothed, localAcceleration.z, accelSmoothing) * accelMultiplier;
        float forwardAccel = forwardAccelCurve.Evaluate(scaledAccel);
        float forwardAccelRot = forwardAccelRotCurve.Evaluate(scaledAccel);

        float zOffset = forwardAccel * forwardAccelMoveMultiplier;
        float pitch = Mathf.Clamp(forwardAccelRot * forwardAccelRotMultiplier, 0f, MaxPitch);

        Vector3 cameraPosition = rollPosition + rollRotation * new Vector3(0f, 0f, zOffset);
        Quaternion cameraRotation = rollRotation * Quaternion.Euler(pitch, 0f, 0f);
        cameraTransform.SetPositionAndRotation(cameraPosition, cameraRotation);
    }

    // "FloatLerped(Float, Speed)": fixed-delta-time lerp, one state per use
    private static float Lerped(ref float state, float target, float speed)
    {
        state = Mathf.Lerp(state, target, speed * Time.fixedDeltaTime);
        return state;
    }
}

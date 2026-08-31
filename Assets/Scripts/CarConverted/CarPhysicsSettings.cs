using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Collective, read-only configuration for the car physics system.
/// These correspond 1:1 to the "Object" (inspector-exposed) variables that
/// Physics.asset only ever READ from — it never wrote to any of these at
/// runtime, so they're pulled out here instead of living as scattered
/// Visual Scripting graph variables on the GameObject.
///
/// The 5 variables that the graph DID write to at runtime (Braking,
/// CurrentDriftAngle, CurrentSpeed, Handbrake, Steering) are NOT here —
/// those are runtime state and live as regular fields on CarPhysics /
/// CarMovement instead.
///
/// Attach this alongside CarPhysics / CarMovement and wire it in via
/// [SerializeField] reference, or merge the fields directly if you'd
/// rather not have a second component.
/// </summary>
public class CarPhysicsSettings : MonoBehaviour
{
    [Header("Suspension")]
    [Tooltip("Raycast layer mask used for the wheel suspension raycasts.")]
    [SerializeField] private LayerMask suspensionLayerMask;

    [Tooltip("Rest length / max raycast distance of the suspension, per wheel.")]
    [SerializeField] private float suspensionHeight;

    [SerializeField] private float suspensionStrengthF;
    [SerializeField] private float suspensionStrengthR;
    [SerializeField] private float suspensionDamping;

    [Tooltip("Maps raw spring/damper compression -> a 0..1-ish grip/force curve. " +
             "Was the 'Progression' curve embedded in the Suspension sub-graph.")]
    [SerializeField] private AnimationCurve suspensionProgression = AnimationCurve.Linear(0, 0, 1, 1);

    [Tooltip("Scales compression before it's fed through the Progression curve.")]
    [SerializeField] private float compressionGripMultiplier = 1f;

    [Header("Wheels (index order: FL, FR, RL, RR)")]
    [Tooltip("Raycast origin points, one per wheel, index 0=FL 1=FR 2=RL 3=RR.")]
    [SerializeField] private List<Transform> suspensionPoints = new List<Transform>(4);

    [Tooltip("Visual wheel hub transforms whose local Y position follows suspension travel, index 0=FL 1=FR 2=RL 3=RR.")]
    [SerializeField] private List<Transform> hubs = new List<Transform>(4);

    [SerializeField] private float wheelDiameterFront;
    [SerializeField] private float wheelDiameterRear;

    [Header("Grip")]
    [SerializeField] private float grip;
    [SerializeField] private float driftGrip;
    [SerializeField] private float rearGrip;
    [SerializeField] private float driftDirection;
    [SerializeField] private float driftDirectionReactionSpeed;
    [SerializeField] private float driftInstability;
    [SerializeField] private float driftSteeringResponse;
    [SerializeField] private float gripSteeringResponse;

    [Header("Steering")]
    [SerializeField] private float turnRate;
    [SerializeField] private float frontLateralSteeringTravel;
    [SerializeField] private float brakeSteeringAdd;
    [SerializeField] private float handbrakeSteeringAdd;
    [SerializeField] private float throttleSteeringAdd;
    [SerializeField] private float throttleSteeringMultiplier;
    [SerializeField] private float noThrottleRecovery;

    [Header("Braking")]
    [SerializeField] private float brakeTorque;
    [SerializeField] private float brakeRearBalance;
    [SerializeField] private float handbrakeTorque;

    [Header("Drag")]
    [SerializeField] private float dragCoefficient;

    [Header("Engine / Drivetrain")]
    [Tooltip("0/1/2 select which ThrottleCurve is used — was an int-switch in the graph (Drivetrain==0/1/2).")]
    [SerializeField] private int drivetrain;

    [Header("Spline Follow")]
    [Tooltip("Transform the car's rotation/forward is derived from each FixedUpdate (rail/spline tracking).")]
    [SerializeField] private Transform splineCalculator;

    [SerializeField] private float transitionSpeed;

    // ----- Public read-only accessors -----
    public LayerMask SuspensionLayerMask => suspensionLayerMask;
    public float SuspensionHeight => suspensionHeight;
    public float SuspensionStrengthF => suspensionStrengthF;
    public float SuspensionStrengthR => suspensionStrengthR;
    public float SuspensionDamping => suspensionDamping;
    public AnimationCurve SuspensionProgression => suspensionProgression;
    public float CompressionGripMultiplier => compressionGripMultiplier;

    public IReadOnlyList<Transform> SuspensionPoints => suspensionPoints;
    public IReadOnlyList<Transform> Hubs => hubs;
    public float WheelDiameterFront => wheelDiameterFront;
    public float WheelDiameterRear => wheelDiameterRear;

    public float Grip => grip;
    public float DriftGrip => driftGrip;
    public float RearGrip => rearGrip;
    public float DriftDirection => driftDirection;
    public float DriftDirectionReactionSpeed => driftDirectionReactionSpeed;
    public float DriftInstability => driftInstability;
    public float DriftSteeringResponse => driftSteeringResponse;
    public float GripSteeringResponse => gripSteeringResponse;

    public float TurnRate => turnRate;
    public float FrontLateralSteeringTravel => frontLateralSteeringTravel;
    public float BrakeSteeringAdd => brakeSteeringAdd;
    public float HandbrakeSteeringAdd => handbrakeSteeringAdd;
    public float ThrottleSteeringAdd => throttleSteeringAdd;
    public float ThrottleSteeringMultiplier => throttleSteeringMultiplier;
    public float NoThrottleRecovery => noThrottleRecovery;

    public float BrakeTorque => brakeTorque;
    public float BrakeRearBalance => brakeRearBalance;
    public float HandbrakeTorque => handbrakeTorque;

    public float DragCoefficient => dragCoefficient;

    public int Drivetrain => drivetrain;

    public Transform SplineCalculator => splineCalculator;
    public float TransitionSpeed => transitionSpeed;
}

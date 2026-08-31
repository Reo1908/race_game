using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Fake" wall collision response. Requires the REAL physical collision
/// between the car and walls to be disabled first (Project Settings ->
/// Physics -> Layer Collision Matrix: uncheck Car layer vs Wall layer).
/// That guarantees PhysX never resolves a wall contact itself, so it can
/// never generate torque/spin — not because we cancel it, but because it
/// structurally never happens.
///
/// This script uses one or more TRIGGER box colliders (assign in
/// detectorColliders — e.g. one covering the front half and one covering
/// the rear half, for tighter coverage than a single box without corner
/// overhang) to detect wall overlap, then manually resolves it with pure
/// translation:
///   - Pushes the car's position out of the wall along the penetration normal.
///   - Removes the velocity component heading into the wall (slide, not stop).
///   - Optionally applies a configurable braking effect opposite to the
///     car's current movement direction while contact lasts.
///   - Optionally spawns a self-contained effect/SFX prefab at the contact
///     point when a new wall contact begins.
///
/// Rotation is never touched by this script, so there is no possibility of
/// induced spin from a wall hit, regardless of contact point or drift angle.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class FakeWallCollision : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("The trigger box collider(s) used to detect wall overlap — add as many as you need (e.g. front + rear) to hug the car's shape tightly. If left empty, auto-fills with every trigger Collider found on this GameObject.")]
    [SerializeField] private Collider[] detectorColliders;

    [Tooltip("Only colliders with this tag are treated as walls.")]
    [SerializeField] private string targetTag = "Wall";

    [Header("Push-Out")]
    [Tooltip("Multiplier on how much of the computed overlap is resolved per physics step. 1 = fully resolve immediately, lower = softer/gradual push-out.")]
    [Range(0f, 1f)]
    [SerializeField] private float pushStrength = 1f;

    [Header("Sliding")]
    [Tooltip("How much of the into-wall velocity component to remove. 1 = perfectly slide along the wall, 0 = velocity untouched (will keep re-penetrating and get pushed out repeatedly, feels stuttery).")]
    [Range(0f, 1f)]
    [SerializeField] private float slideDamping = 0.9f;

    [Tooltip("Optional small bounce-back along the wall normal, added after sliding is applied. 0 = no bounce.")]
    [SerializeField] private float bounceStrength = 0f;

    [Header("Impact Slowdown")]
    [Tooltip("Fraction of the car's current speed removed each physics step while touching a wall. This is applied opposite to the car's CURRENT MOVEMENT DIRECTION (not the wall normal) — a general braking effect during impact, not a shove backwards. 0 = no slowdown.")]
    [Range(0f, 1f)]
    [SerializeField] private float impactSlowdown = 0.15f;

    [Header("Impact Effect")]
    [Tooltip("Prefab spawned at the contact point when a new wall collision begins (e.g. a spark/dust burst that plays your SFX script and destroys itself). Leave empty to disable.")]
    [SerializeField] private GameObject impactEffectPrefab;

    [Tooltip("Minimum rigidbody speed (overall velocity magnitude, not just the speed into the wall) required to spawn the impact effect. Prevents the effect from spawning while barely moving against a wall.")]
    [SerializeField] private float minImpactSpeedForEffect = 1f;

    [Header("Debug")]
    [SerializeField] private bool debugMode = false;

    private Rigidbody _rb;

    // Tracks which wall colliders we're currently in contact with, so the
    // impact effect spawns only once per contact (on the frame contact
    // begins) rather than every physics step while overlapping.
    private readonly HashSet<Collider> _activeWallContacts = new HashSet<Collider>();

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        if (detectorColliders == null || detectorColliders.Length == 0)
        {
            Collider[] allColliders = GetComponents<Collider>();
            List<Collider> triggers = new List<Collider>();
            foreach (Collider c in allColliders)
            {
                if (c.isTrigger) triggers.Add(c);
            }
            detectorColliders = triggers.ToArray();

            if (detectorColliders.Length == 0)
            {
                Debug.LogWarning($"[FakeWallCollision] No detector colliders assigned and none found automatically on '{gameObject.name}'. Assign at least one trigger box collider.", this);
            }
        }
        else
        {
            foreach (Collider c in detectorColliders)
            {
                if (c != null && !c.isTrigger)
                {
                    Debug.LogWarning($"[FakeWallCollision] Detector collider '{c.name}' on '{gameObject.name}' is not marked Is Trigger. It needs to be a trigger for this to work correctly.", this);
                }
            }
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (!other.CompareTag(targetTag)) return;
        if (detectorColliders == null || detectorColliders.Length == 0) return;

        // Unity doesn't tell us which of our own colliders triggered this
        // callback, so test every assigned detector against the wall. Only
        // the ones actually overlapping will produce a correction.
        bool anyOverlapped = false;
        Vector3 strongestDirection = Vector3.zero;
        Vector3 strongestContactPoint = Vector3.zero;
        float strongestIntoWallSpeed = 0f;

        foreach (Collider detector in detectorColliders)
        {
            if (detector == null) continue;

            bool overlapped = ResolveAgainst(detector, other, out Vector3 direction, out Vector3 contactPoint, out float intoWallSpeed);
            if (!overlapped) continue;

            anyOverlapped = true;
            if (intoWallSpeed > strongestIntoWallSpeed)
            {
                strongestIntoWallSpeed = intoWallSpeed;
                strongestDirection = direction;
                strongestContactPoint = contactPoint;
            }
        }

        if (!anyOverlapped) return;

        // Spawn the impact effect once, on the frame this wall contact begins.
        // Gated by the rigidbody's overall speed (not just the speed into the
        // wall), so e.g. sliding along a wall at high speed still counts.
        bool isNewContact = _activeWallContacts.Add(other);
        float currentSpeed = _rb.linearVelocity.magnitude;
        if (isNewContact && impactEffectPrefab != null && currentSpeed >= minImpactSpeedForEffect)
        {
            Quaternion rot = strongestDirection != Vector3.zero
                ? Quaternion.LookRotation(strongestDirection)
                : Quaternion.identity;
            Instantiate(impactEffectPrefab, strongestContactPoint, rot);

            if (debugMode)
            {
                Debug.Log($"[FakeWallCollision] Spawned impact effect on '{gameObject.name}' at {strongestContactPoint}, rigidbodySpeed={currentSpeed:F2}", this);
            }
        }
        else if (isNewContact && impactEffectPrefab == null && debugMode)
        {
            Debug.LogWarning($"[FakeWallCollision] New wall contact on '{gameObject.name}' but no impactEffectPrefab is assigned — nothing to spawn.", this);
        }

        // Configurable braking effect while in contact, applied opposite to
        // the car's CURRENT MOVEMENT DIRECTION (not the wall normal) — a
        // general slowdown during impact rather than a directional shove.
        // Clamped to never exceed current speed, so it can only slow the
        // car down, never reverse it.
        if (impactSlowdown > 0f)
        {
            Vector3 currentVelocity = _rb.linearVelocity;
            float speed = currentVelocity.magnitude;
            if (speed > 0.01f)
            {
                Vector3 brakeDir = -currentVelocity.normalized;
                float brakeAmount = Mathf.Min(speed * impactSlowdown, speed);
                _rb.linearVelocity = currentVelocity + brakeDir * brakeAmount;
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        _activeWallContacts.Remove(other);
    }

    /// <summary>
    /// Resolves overlap between one detector and one wall collider: pushes
    /// the car out and removes the into-wall velocity component. Returns
    /// true if there was overlap, and outputs data used for the impact
    /// effect (direction, contact point, into-wall speed).
    /// </summary>
    private bool ResolveAgainst(Collider detector, Collider other, out Vector3 direction, out Vector3 contactPoint, out float intoWallSpeed)
    {
        direction = Vector3.zero;
        contactPoint = Vector3.zero;
        intoWallSpeed = 0f;

        bool overlapped = Physics.ComputePenetration(
            detector, _rb.position, _rb.rotation,
            other, other.transform.position, other.transform.rotation,
            out direction, out float distance);

        if (!overlapped || distance <= 0f) return false;

        // Push the car out of the wall, purely as a position change.
        // Read _rb.position fresh each call so multiple detectors resolving
        // in the same step still stack correctly.
        Vector3 pushOut = direction * (distance * pushStrength);
        _rb.MovePosition(_rb.position + pushOut);

        // Remove the velocity component heading into the wall so the car slides along it.
        Vector3 velocity = _rb.linearVelocity;
        float intoWall = Vector3.Dot(velocity, -direction);
        intoWallSpeed = Mathf.Max(0f, intoWall);

        if (intoWall > 0f)
        {
            Vector3 intoWallComponent = -direction * intoWall;
            velocity -= intoWallComponent * slideDamping;

            if (bounceStrength > 0f)
            {
                velocity += direction * (intoWall * bounceStrength);
            }

            _rb.linearVelocity = velocity;
        }

        // Find the contact point using only our own (convex) detector
        // collider, so it's never at the mercy of the wall's own geometry.
        contactPoint = ComputeContactPoint(detector, direction);

        if (debugMode)
        {
            Debug.Log($"[FakeWallCollision] Resolved overlap via '{detector.name}' on '{gameObject.name}': distance={distance:F3}, direction={direction}, intoWallSpeed={intoWall:F2}", this);
            Debug.DrawRay(_rb.position, direction * 2f, Color.cyan, 0f, false);
        }

        return true;
    }

    /// <summary>
    /// Finds the exact surface point where the detector meets the wall by
    /// casting a short ray from just outside the wall (on the car's side,
    /// per the push direction) back toward it. This works correctly on
    /// non-convex Mesh Colliders, unlike Collider.ClosestPoint /
    /// Physics.ClosestPoint, which require convex geometry.
    /// </summary>
    /// <summary>
    /// Finds the contact point using ONLY our own detector collider, which
    /// (as a trigger box/sphere/capsule) is guaranteed convex — so
    /// Collider.ClosestPoint always works and never throws. We query a point
    /// far away in the direction of the wall (opposite the push-out
    /// direction) and take the closest point on the detector's surface to
    /// that; that's the face of the detector actually touching the wall.
    /// This is independent of the wall's own shape/size, so it can't be
    /// thrown off by a wall's bounding box being larger than its visible
    /// geometry (which was causing spawns to land back at the car).
    /// </summary>
    private Vector3 ComputeContactPoint(Collider detector, Vector3 direction)
    {
        Vector3 farPointTowardWall = detector.bounds.center - direction * 1000f;
        return detector.ClosestPoint(farPointTowardWall);
    }
}

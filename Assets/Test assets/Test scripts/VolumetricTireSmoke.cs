using UnityEngine;

/// <summary>
/// Drives the "_Intensity" property on a VolumetricSmoke material based on
/// slip angle. Attach to the same cube that has the volumetric smoke
/// material — it needs a Rigidbody somewhere in its parent chain to read
/// velocity from, same as the particle version.
/// </summary>
[RequireComponent(typeof(MeshRenderer))]
public class VolumetricTireSmoke : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Rigidbody used to read velocity. Defaults to the nearest Rigidbody in parents if left empty.")]
    [SerializeField] private Rigidbody referenceRigidbody;

    [Header("Slip Detection")]
    [SerializeField] private float slipAngleThreshold = 10f;
    [SerializeField] private float slipAngleMax = 45f;
    [SerializeField] private float minSpeed = 1.5f;

    [Header("Volumetric Response")]
    [Tooltip("How quickly intensity ramps toward its target.")]
    [SerializeField] private float intensityLerpSpeed = 6f;

    private MeshRenderer meshRenderer;
    private MaterialPropertyBlock propBlock;
    private static readonly int IntensityID = Shader.PropertyToID("_Intensity");

    private float currentIntensity;

    public float CurrentSlipAngle { get; private set; }

    private void Awake()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        propBlock = new MaterialPropertyBlock();

        if (referenceRigidbody == null)
            referenceRigidbody = GetComponentInParent<Rigidbody>();

        if (referenceRigidbody == null)
            Debug.LogWarning($"{nameof(VolumetricTireSmoke)} on {name}: no Rigidbody found in parents. Assign one manually.", this);
    }

    private void Update()
    {
        if (referenceRigidbody == null) return;

        CurrentSlipAngle = CalculateSlipAngle();
        float target = CalculateTargetIntensity(CurrentSlipAngle);
        currentIntensity = Mathf.Lerp(currentIntensity, target, Time.deltaTime * intensityLerpSpeed);

        // MaterialPropertyBlock so we don't create a material instance per
        // wheel just to vary one float.
        meshRenderer.GetPropertyBlock(propBlock);
        propBlock.SetFloat(IntensityID, currentIntensity);
        meshRenderer.SetPropertyBlock(propBlock);
    }

    private float CalculateSlipAngle()
    {
        Vector3 velocity = referenceRigidbody.linearVelocity; // Unity 6 renamed Rigidbody.velocity to linearVelocity
        velocity.y = 0f;

        if (velocity.magnitude < minSpeed)
            return 0f;

        Vector3 forward = transform.forward;
        forward.y = 0f;

        return Vector3.Angle(forward, velocity.normalized);
    }

    private float CalculateTargetIntensity(float slipAngle)
    {
        if (slipAngle <= slipAngleThreshold) return 0f;

        float t = Mathf.InverseLerp(slipAngleThreshold, slipAngleMax, slipAngle);
        return Mathf.Clamp01(t);
    }
}

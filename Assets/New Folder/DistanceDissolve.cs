using UnityEngine;

public class DistanceDissolve : MonoBehaviour
{
    [Header("Distance thresholds")]
    [Tooltip("Distance at which the plane is fully visible")]
    public float startFadeDistance = 10f;
    [Tooltip("Distance at which the plane is fully invisible")]
    public float endFadeDistance = 2f;

    [Header("Bloom-out effect")]
    [Tooltip("How much brighter the plane gets right before it vanishes")]
    public float maxEmissionBoost = 4f;
    public Color emissionColor = Color.white;
    public AnimationCurve emissionCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Camera")]
    public Camera targetCamera;

    private Renderer _renderer;
    private Material _mat;
    private Color _baseColor;

    void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _mat = _renderer.material; // instance, so this won't affect other objects sharing the material

        _baseColor = _mat.HasProperty("_BaseColor") ? _mat.GetColor("_BaseColor")
                   : _mat.HasProperty("_Color") ? _mat.GetColor("_Color")
                   : Color.white;

        _mat.EnableKeyword("_EMISSION");

        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    void Update()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
            if (targetCamera == null) return;
        }

        float dist = Vector3.Distance(targetCamera.transform.position, transform.position);
        float t = Mathf.Clamp01(Mathf.InverseLerp(endFadeDistance, startFadeDistance, dist));

        Color c = _baseColor;
        c.a = _baseColor.a * t;

        float bloomT = emissionCurve.Evaluate(1f - t);
        Color emission = emissionColor * (bloomT * maxEmissionBoost);

        if (_mat.HasProperty("_BaseColor")) _mat.SetColor("_BaseColor", c);
        else if (_mat.HasProperty("_Color")) _mat.SetColor("_Color", c);

        _mat.SetColor("_EmissionColor", emission);

        _renderer.enabled = t > 0.001f;
    }
}

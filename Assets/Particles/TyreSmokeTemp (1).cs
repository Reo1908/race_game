using UnityEngine;
using UnityEngine.InputSystem;

// TEMPORARY controller: fades tyre smoke in/out based on slip angle + an input value.
// Both the input and the slip angle are turned into a 0..1 factor and smoothly lerped
// over time (instead of snapping instantly), then multiplied together and used to
// scale the particle systems' Start Color alpha, so the smoke fades in/out smoothly
// rather than popping on/off.
// Meant as a placeholder until real wheel/tyre physics data (per-wheel slip from
// WheelCollider.GetGroundHit, or a custom tyre model) is available.

[RequireComponent(typeof(Rigidbody))]
public class TyreSmokeTemp : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Particle systems to toggle (e.g. one per tyre). Auto-filled from children if left empty.")]
    [SerializeField] private ParticleSystem[] smokeParticles;

    [Header("Input (New Input System)")]
    [Tooltip("Action read as a float (e.g. throttle, brake, or handbrake). Must be > 0 for smoke to be allowed.")]
    [SerializeField] private InputActionReference triggerInputAction;

    [Header("Slip Settings")]
    [Tooltip("Angle (degrees) between forward direction and actual velocity direction at which the angle-factor starts rising from 0.")]
    [SerializeField] private float slipAngleThreshold = 15f;

    [Tooltip("Angle (degrees) at which the angle-factor reaches its maximum of 1.")]
    [SerializeField] private float slipAngleFullEffect = 45f;

    [Tooltip("Below this speed (m/s) the slip angle is noisy/meaningless, so the angle-factor is forced to 0.")]
    [SerializeField] private float minSpeedForSlip = 0.5f;

    [Header("Smooth Alpha Blend")]
    [Tooltip("How quickly the smoke's alpha chases its target value. Higher = snappier, lower = smoother/laggier.")]
    [SerializeField] private float alphaLerpSpeed = 4f;

    [Tooltip("Emission is disabled once the smoothed alpha factor drops below this, so we're not spending performance on invisible particles.")]
    [SerializeField] private float emissionCutoff = 0.02f;

    [Header("Debug")]
    [SerializeField] private bool logDebugInfo = false;

    private Rigidbody rb;
    private ParticleSystem.EmissionModule[] emissionModules;
    private Color[] baseStartColors;
    private bool isEmitting;
    private float currentAlphaFactor01;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (smokeParticles == null || smokeParticles.Length == 0)
            smokeParticles = GetComponentsInChildren<ParticleSystem>();

        if (smokeParticles != null && smokeParticles.Length > 0)
        {
            emissionModules = new ParticleSystem.EmissionModule[smokeParticles.Length];
            baseStartColors = new Color[smokeParticles.Length];

            for (int i = 0; i < smokeParticles.Length; i++)
            {
                if (smokeParticles[i] == null) continue;

                emissionModules[i] = smokeParticles[i].emission;
                emissionModules[i].enabled = false;

                // Assumes Start Color mode is "Color" (not Gradient/Random). If you're using
                // those modes, this will just read their base color field - fine for testing,
                // but swap to driving colorOverLifetime alpha instead for a real setup.
                baseStartColors[i] = smokeParticles[i].main.startColor.color;
            }
        }
        else
        {
            Debug.LogWarning($"{nameof(TyreSmokeTemp)} on {name}: no ParticleSystems assigned or found in children.");
        }

        isEmitting = false;
    }

    private void OnEnable()
    {
        if (triggerInputAction != null)
            triggerInputAction.action.Enable();
    }

    private void OnDisable()
    {
        if (triggerInputAction != null)
            triggerInputAction.action.Disable();
    }

    private void Update()
    {
        if (smokeParticles == null || smokeParticles.Length == 0 || rb == null)
            return;

        float inputValue = triggerInputAction != null
            ? triggerInputAction.action.ReadValue<float>()
            : 0f;

        float slipAngle = GetSlipAngle();

        // Each 0..1 on its own, so either one being ~0 kills the smoke.
        float inputFactor = Mathf.Clamp01(inputValue);
        float angleFactor = Mathf.InverseLerp(slipAngleThreshold, slipAngleFullEffect, slipAngle);

        float targetAlphaFactor = inputFactor * angleFactor;

        // Smoothly chase the target instead of snapping straight to it.
        currentAlphaFactor01 = Mathf.Lerp(currentAlphaFactor01, targetAlphaFactor, Time.deltaTime * alphaLerpSpeed);

        bool shouldEmit = currentAlphaFactor01 > emissionCutoff;

        if (shouldEmit != isEmitting)
        {
            isEmitting = shouldEmit;

            // Toggling emission (not Play/Stop) lets existing smoke drift away naturally.
            for (int i = 0; i < smokeParticles.Length; i++)
            {
                if (smokeParticles[i] == null) continue;
                emissionModules[i].enabled = isEmitting;
            }
        }

        for (int i = 0; i < smokeParticles.Length; i++)
        {
            if (smokeParticles[i] == null) continue;

            ParticleSystem.MainModule main = smokeParticles[i].main;
            Color color = baseStartColors[i];
            color.a = baseStartColors[i].a * currentAlphaFactor01;
            main.startColor = color;
        }

        if (logDebugInfo)
            Debug.Log($"[{name}] slipAngle={slipAngle:F1} input={inputValue:F2} alphaFactor={currentAlphaFactor01:F2} emitting={isEmitting}");
    }

    private float GetSlipAngle()
    {
        // NOTE: Unity 6 renamed Rigidbody.velocity to Rigidbody.linearVelocity.
        // If you're on an older Unity version, replace rb.linearVelocity with rb.velocity.
        Vector3 flatVelocity = Vector3.ProjectOnPlane(rb.linearVelocity, transform.up);

        if (flatVelocity.sqrMagnitude < minSpeedForSlip * minSpeedForSlip)
            return 0f;

        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, transform.up);

        return Vector3.Angle(flatForward, flatVelocity);
    }
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

// URP 17 / Unity 6 Render Graph version of the cheap radial/zoom blur.
// Blur intensity is driven by the active camera's frame-to-frame speed,
// with a clear deadzone in the middle of the screen and smoothing so it
// doesn't snap instantly.
//
// Setup:
// 1. Create a Material using shader "Custom/RadialBlur".
// 2. Select your URP Renderer asset > Add Renderer Feature > Radial Blur Feature.
// 3. Assign the material and tweak the settings on the feature.
public class RadialBlurFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        public Material blurMaterial;

        [Header("Blur")]
        [Tooltip("Max blur intensity, reached at speedForMaxBlur.")]
        [Range(0f, 0.5f)] public float maxBlurStrength = 0.05f;
        [Tooltip("Sample count. Higher = smoother but more expensive.")]
        [Range(1, 16)] public int samples = 6;
        [Range(0f, 1f)] public float centerX = 0.5f;
        [Range(0f, 1f)] public float centerY = 0.5f;

        [Header("Deadzone")]
        [Tooltip("Radius (in UV units from center) that stays completely clear.")]
        [Range(0f, 0.5f)] public float deadzoneRadius = 0.15f;
        [Tooltip("How wide the soft falloff is past the deadzone radius.")]
        [Range(0.001f, 0.5f)] public float deadzoneFeather = 0.15f;

        [Header("Speed-driven intensity")]
        [Tooltip("Scale blur by how fast the camera is moving.")]
        public bool driveBySpeed = true;
        [Tooltip("World units/sec below which there's no blur.")]
        public float minSpeedForBlur = 2f;
        [Tooltip("World units/sec at which blur reaches max intensity.")]
        public float speedForMaxBlur = 20f;
        [Tooltip("Seconds to smoothly ramp intensity toward its target, avoids snapping.")]
        public float intensitySmoothTime = 0.25f;
    }

    public Settings settings = new Settings();
    private RadialBlurPass _pass;

    public override void Create()
    {
        _pass = new RadialBlurPass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.blurMaterial == null) return;
        renderer.EnqueuePass(_pass);
    }

    class RadialBlurPass : ScriptableRenderPass
    {
        private readonly Settings _settings;

        private Vector3 _lastCameraPos;
        private bool _hasLastPos;
        private float _currentIntensity;

        public RadialBlurPass(Settings settings)
        {
            _settings = settings;
            renderPassEvent = settings.renderPassEvent;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType != CameraType.Game) return;

            var resourceData = frameData.Get<UniversalResourceData>();

            // Can't blit the backbuffer directly - it needs an intermediate texture.
            if (resourceData.isActiveTargetBackBuffer) return;

            float targetIntensity = _settings.maxBlurStrength;

            if (_settings.driveBySpeed)
            {
                float dt = Mathf.Max(Time.deltaTime, 0.0001f);
                Vector3 camPos = cameraData.camera.transform.position;

                float speed = _hasLastPos ? Vector3.Distance(camPos, _lastCameraPos) / dt : 0f;
                _lastCameraPos = camPos;
                _hasLastPos = true;

                float normalized = Mathf.Clamp01(Mathf.InverseLerp(_settings.minSpeedForBlur, _settings.speedForMaxBlur, speed));
                targetIntensity = normalized * _settings.maxBlurStrength;
            }

            float rampSpeed = _settings.intensitySmoothTime > 0f
                ? _settings.maxBlurStrength / _settings.intensitySmoothTime
                : Mathf.Infinity;
            _currentIntensity = Mathf.MoveTowards(_currentIntensity, targetIntensity, rampSpeed * Time.deltaTime);

            _settings.blurMaterial.SetFloat("_BlurStrength", _currentIntensity);
            _settings.blurMaterial.SetInt("_Samples", _settings.samples);
            _settings.blurMaterial.SetFloat("_CenterX", _settings.centerX);
            _settings.blurMaterial.SetFloat("_CenterY", _settings.centerY);
            _settings.blurMaterial.SetFloat("_DeadzoneRadius", _settings.deadzoneRadius);
            _settings.blurMaterial.SetFloat("_DeadzoneFeather", _settings.deadzoneFeather);

            var source = resourceData.activeColorTexture;

            var desc = renderGraph.GetTextureDesc(source);
            desc.name = "_RadialBlurTemp";
            desc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            RenderGraphUtils.BlitMaterialParameters blitParams = new(source, destination, _settings.blurMaterial, 0);
            renderGraph.AddBlitPass(blitParams, passName: "Radial Blur");

            resourceData.cameraColor = destination;
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RetroFX
{
    /// <summary>
    /// Minimal test feature - no Volume system involved at all. If this
    /// doesn't show up, the problem is in project/renderer setup, not in
    /// any of the RetroFX shader logic.
    /// Tints the whole screen toward TintColor by TintAmount, always on
    /// whenever this feature is enabled (checkbox in the Renderer Features
    /// list) - no thresholds, no HDR requirement, no Volume needed.
    /// </summary>
    public class TestTintRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader shader;
        [SerializeField] private RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        [Tooltip("Color to blend toward. Default magenta so it's unmissable.")]
        [SerializeField] private Color tintColor = new Color(1f, 0f, 1f, 1f);

        [Tooltip("0 = no effect, 1 = fully replaced by TintColor. Start at 0.5.")]
        [Range(0f, 1f)]
        [SerializeField] private float tintAmount = 0.5f;

        private Material material;
        private TestTintPass pass;

        public override void Create()
        {
            if (shader == null)
            {
                shader = Shader.Find("Hidden/RetroFX/TestTint");
            }
            if (shader != null)
            {
                material = CoreUtils.CreateEngineMaterial(shader);
            }
            pass = new TestTintPass(material) { renderPassEvent = renderPassEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null)
            {
                Debug.LogWarning("[RetroFX] TestTint: material is null - drag the TestTint.shader onto the Shader field on this component manually.");
                return;
            }
            if (renderingData.cameraData.cameraType == CameraType.Preview) return;

            Debug.Log("[RetroFX] TestTint: pass enqueued.");
            pass.Setup(tintColor, tintAmount);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
        }

        private class TestTintPass : ScriptableRenderPass
        {
            private readonly Material material;
            private Color tintColor;
            private float tintAmount;

            private class PassData
            {
                public Material material;
                public TextureHandle source;
            }

            public TestTintPass(Material material)
            {
                this.material = material;
                profilingSampler = new ProfilingSampler("RetroFX Test Tint");
                requiresIntermediateTexture = true;
            }

            public void Setup(Color color, float amount)
            {
                tintColor = color;
                tintAmount = amount;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (material == null) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                TextureHandle source = resourceData.activeColorTexture;

                material.SetColor("_TintColor", tintColor);
                material.SetFloat("_TintAmount", tintAmount);

                var outputDesc = renderGraph.GetTextureDesc(source);
                outputDesc.name = "_TestTint_Output";
                TextureHandle output = renderGraph.CreateTexture(outputDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Test Tint", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(output, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                    });
                }

                resourceData.cameraColor = output;
            }
        }
    }
}

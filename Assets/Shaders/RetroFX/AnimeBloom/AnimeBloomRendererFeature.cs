using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RetroFX
{
    /// <summary>
    /// Adds this to your URP Renderer asset's Renderer Features list.
    /// Reads settings from an AnimeBloomVolume override on the active Volume stack.
    /// </summary>
    public class AnimeBloomRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader shader;
        [SerializeField] private RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        private Material material;
        private AnimeBloomPass pass;

        private static class Passes
        {
            public const int Prefilter = 0;
            public const int Kawase = 1;
            public const int Composite = 2;
        }

        public override void Create()
        {
            if (shader == null)
            {
                shader = Shader.Find("Hidden/RetroFX/AnimeBloom");
            }
            if (shader != null)
            {
                material = CoreUtils.CreateEngineMaterial(shader);
            }
            pass = new AnimeBloomPass(material) { renderPassEvent = renderPassEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null)
            {
                Debug.LogWarning("[RetroFX] AnimeBloom: material is null - shader failed to load/compile. Assign the Shader field manually on the Renderer Feature.");
                return;
            }

            var stack = VolumeManager.instance.stack;
            var settings = stack.GetComponent<AnimeBloomVolume>();
            if (settings == null)
            {
                Debug.LogWarning("[RetroFX] AnimeBloom: no AnimeBloomVolume found on the volume stack.");
                return;
            }
            if (!settings.IsActive())
            {
                Debug.LogWarning("[RetroFX] AnimeBloom: settings found but IsActive() is false - check the 'Enabled' checkbox AND its override tickbox on the Volume, and that Intensity > 0.");
                return;
            }
            if (renderingData.cameraData.cameraType == CameraType.Preview) return;

            Debug.Log("[RetroFX] AnimeBloom: pass enqueued.");
            pass.Setup(settings);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
        }

        private class AnimeBloomPass : ScriptableRenderPass
        {
            private readonly Material material;
            private AnimeBloomVolume settings;

            private class PassData
            {
                public Material material;
                public TextureHandle source;
                public TextureHandle bloomTex;
                public int passIndex;
                public Vector2 blurOffset;
            }

            public AnimeBloomPass(Material material)
            {
                this.material = material;
                profilingSampler = new ProfilingSampler("RetroFX Anime Bloom");
                // This pass samples the camera's active color texture, so it
                // can't render straight to the backbuffer - force URP to give
                // it an off-screen texture to work with.
                requiresIntermediateTexture = true;
            }

            public void Setup(AnimeBloomVolume settings)
            {
                this.settings = settings;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (material == null) return;

                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                if (resourceData.isActiveTargetBackBuffer) return;

                TextureHandle source = resourceData.activeColorTexture;

                material.SetFloat("_Threshold", settings.threshold.value);
                material.SetFloat("_Knee", Mathf.Max(settings.knee.value, 1e-4f));
                material.SetFloat("_Clamp", settings.clamp.value);
                material.SetFloat("_Intensity", settings.intensity.value);
                material.SetColor("_Tint", settings.tint.value);
                material.SetFloat("_Saturation", settings.saturation.value);
                material.SetFloat("_Diffusion", settings.diffusion.value);

                int downsample = Mathf.Max(1, settings.downsample.value);
                int width = Mathf.Max(4, cameraData.cameraTargetDescriptor.width / downsample);
                int height = Mathf.Max(4, cameraData.cameraTargetDescriptor.height / downsample);

                var bloomDesc = renderGraph.GetTextureDesc(source);
                bloomDesc.width = width;
                bloomDesc.height = height;
                bloomDesc.clearBuffer = false;
                bloomDesc.msaaSamples = MSAASamples.None;

                // ---- Prefilter (threshold extract) ----
                bloomDesc.name = "_AnimeBloom_Prefilter";
                TextureHandle current = renderGraph.CreateTexture(bloomDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Anime Bloom Prefilter", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.passIndex = Passes.Prefilter;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(current, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }

                // ---- Iterative blur (thickness / spread) ----
                int iterations = Mathf.Max(1, settings.iterations.value);
                Vector2 stretch = settings.stretch.value;
                float spread = settings.blurSpread.value;

                for (int i = 0; i < iterations; i++)
                {
                    float texelX = (1f / width) * spread * Mathf.Max(0.01f, stretch.x) * (i + 1);
                    float texelY = (1f / height) * spread * Mathf.Max(0.01f, stretch.y) * (i + 1);

                    bloomDesc.name = "_AnimeBloom_Blur" + i;
                    TextureHandle next = renderGraph.CreateTexture(bloomDesc);

                    using (var builder = renderGraph.AddRasterRenderPass<PassData>("Anime Bloom Blur", out var passData, profilingSampler))
                    {
                        passData.material = material;
                        passData.source = current;
                        passData.passIndex = Passes.Kawase;
                        passData.blurOffset = new Vector2(texelX, texelY);
                        builder.UseTexture(current, AccessFlags.Read);
                        builder.SetRenderAttachment(next, 0, AccessFlags.Write);
                        builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                        {
                            data.material.SetVector("_BlurOffset", data.blurOffset);
                            Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                        });
                    }
                    current = next;
                }

                // ---- Composite back onto scene color ----
                // Note: we can't read and write the same TextureHandle in one pass,
                // so composite into a fresh full-res target and swap it in as the
                // camera color for the rest of the frame.
                var compositeDesc = renderGraph.GetTextureDesc(source);
                compositeDesc.name = "_AnimeBloom_Composite";
                compositeDesc.clearBuffer = false;
                compositeDesc.msaaSamples = MSAASamples.None;
                TextureHandle compositeOutput = renderGraph.CreateTexture(compositeDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Anime Bloom Composite", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.bloomTex = current;
                    passData.passIndex = Passes.Composite;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(current, AccessFlags.Read);
                    builder.SetRenderAttachment(compositeOutput, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalTexture("_BloomTex", data.bloomTex);
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }

                resourceData.cameraColor = compositeOutput;
            }
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RetroFX
{
    /// <summary>
    /// Adds this to your URP Renderer asset's Renderer Features list.
    /// Reads settings from a CameraStreakVolume override on the active Volume stack.
    /// </summary>
    public class CameraStreakRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader shader;
        [SerializeField] private RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        private Material material;
        private CameraStreakPass pass;

        private static class Passes
        {
            public const int Prefilter = 0;
            public const int Streak = 1;
            public const int Composite = 2;
        }

        public override void Create()
        {
            if (shader == null)
            {
                shader = Shader.Find("Hidden/RetroFX/CameraStreak");
            }
            if (shader != null)
            {
                material = CoreUtils.CreateEngineMaterial(shader);
            }
            pass = new CameraStreakPass(material) { renderPassEvent = renderPassEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null)
            {
                Debug.LogWarning("[RetroFX] CameraStreak: material is null - shader failed to load/compile. Assign the Shader field manually on the Renderer Feature.");
                return;
            }

            var stack = VolumeManager.instance.stack;
            var settings = stack.GetComponent<CameraStreakVolume>();
            if (settings == null)
            {
                Debug.LogWarning("[RetroFX] CameraStreak: no CameraStreakVolume found on the volume stack.");
                return;
            }
            if (!settings.IsActive())
            {
                Debug.LogWarning("[RetroFX] CameraStreak: settings found but IsActive() is false - check the 'Enabled' checkbox AND its override tickbox on the Volume, and that Intensity > 0.");
                return;
            }
            if (renderingData.cameraData.cameraType == CameraType.Preview) return;

            Debug.Log("[RetroFX] CameraStreak: pass enqueued.");
            pass.Setup(settings);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
        }

        private class CameraStreakPass : ScriptableRenderPass
        {
            private readonly Material material;
            private CameraStreakVolume settings;

            private class PassData
            {
                public Material material;
                public TextureHandle source;
                public TextureHandle streakTex;
                public int passIndex;
                public float streakOffset;
            }

            public CameraStreakPass(Material material)
            {
                this.material = material;
                profilingSampler = new ProfilingSampler("RetroFX Camera Streak");
                // This pass samples the camera's active color texture, so it
                // can't render straight to the backbuffer - force URP to give
                // it an off-screen texture to work with.
                requiresIntermediateTexture = true;
            }

            public void Setup(CameraStreakVolume settings)
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

                float rad = settings.angle.value * Mathf.Deg2Rad;
                // Direction in UV space; correct for aspect ratio so a "vertical"
                // streak looks vertical on screen regardless of resolution.
                float aspect = (float)cameraData.cameraTargetDescriptor.width / cameraData.cameraTargetDescriptor.height;
                Vector2 dir = new Vector2(Mathf.Cos(rad) , Mathf.Sin(rad) * aspect).normalized;
                material.SetVector("_StreakDir", dir);
                material.SetFloat("_ChromaticFringe", settings.chromaticFringe.value / cameraData.cameraTargetDescriptor.width);

                int downsample = Mathf.Max(1, settings.downsample.value);
                int width = Mathf.Max(4, cameraData.cameraTargetDescriptor.width / downsample);
                int height = Mathf.Max(4, cameraData.cameraTargetDescriptor.height / downsample);

                var desc = renderGraph.GetTextureDesc(source);
                desc.width = width;
                desc.height = height;
                desc.clearBuffer = false;
                desc.msaaSamples = MSAASamples.None;

                // ---- Prefilter (threshold extract) ----
                desc.name = "_CameraStreak_Prefilter";
                TextureHandle current = renderGraph.CreateTexture(desc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Camera Streak Prefilter", out var passData, profilingSampler))
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

                // ---- Directional accumulation (builds the trail) ----
                int iterations = Mathf.Max(1, settings.iterations.value);
                float baseOffset = settings.length.value / width;
                float attenuation = settings.attenuation.value;
                material.SetFloat("_Attenuation", attenuation);

                for (int i = 0; i < iterations; i++)
                {
                    float offset = baseOffset * Mathf.Pow(2f, i);

                    desc.name = "_CameraStreak_Pass" + i;
                    TextureHandle next = renderGraph.CreateTexture(desc);

                    using (var builder = renderGraph.AddRasterRenderPass<PassData>("Camera Streak Accumulate", out var passData, profilingSampler))
                    {
                        passData.material = material;
                        passData.source = current;
                        passData.passIndex = Passes.Streak;
                        passData.streakOffset = offset;
                        builder.UseTexture(current, AccessFlags.Read);
                        builder.SetRenderAttachment(next, 0, AccessFlags.Write);
                        builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                        {
                            data.material.SetFloat("_StreakOffset", data.streakOffset);
                            Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                        });
                    }
                    current = next;
                }

                // ---- Composite back onto scene color (fresh target - can't
                // read and write the same handle in one pass) ----
                var compositeDesc = renderGraph.GetTextureDesc(source);
                compositeDesc.name = "_CameraStreak_Composite";
                compositeDesc.clearBuffer = false;
                compositeDesc.msaaSamples = MSAASamples.None;
                TextureHandle compositeOutput = renderGraph.CreateTexture(compositeDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Camera Streak Composite", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.streakTex = current;
                    passData.passIndex = Passes.Composite;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(current, AccessFlags.Read);
                    builder.SetRenderAttachment(compositeOutput, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalTexture("_StreakTex", data.streakTex);
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }

                resourceData.cameraColor = compositeOutput;
            }
        }
    }
}

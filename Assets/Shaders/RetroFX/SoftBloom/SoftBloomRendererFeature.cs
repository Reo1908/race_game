using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RetroFX
{
    /// <summary>
    /// Adds this to your URP Renderer asset's Renderer Features list.
    /// Reads settings from a SoftBloomVolume override on the active Volume stack.
    /// </summary>
    public class SoftBloomRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader shader;
        [SerializeField] private RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

        private Material material;
        private SoftBloomPass pass;

        private static class Passes
        {
            public const int PrefilterHighlight = 0;
            public const int Downsample = 1;
            public const int UpsampleAdd = 2;
            public const int Composite = 3;
            public const int PrefilterDiffuse = 4;
        }

        public override void Create()
        {
            if (shader == null)
            {
                shader = Shader.Find("Hidden/RetroFX/SoftBloom");
            }
            if (shader != null)
            {
                material = CoreUtils.CreateEngineMaterial(shader);
            }
            pass = new SoftBloomPass(material) { renderPassEvent = renderPassEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null)
            {
                Debug.LogWarning("[RetroFX] SoftBloom: material is null - drag SoftBloom.shader onto the Shader field on this component manually.");
                return;
            }

            var stack = VolumeManager.instance.stack;
            var settings = stack.GetComponent<SoftBloomVolume>();
            if (settings == null || !settings.IsActive()) return;
            if (renderingData.cameraData.cameraType == CameraType.Preview) return;

            pass.Setup(settings);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
        }

        private class SoftBloomPass : ScriptableRenderPass
        {
            private readonly Material material;
            private SoftBloomVolume settings;

            private class PassData
            {
                public Material material;
                public TextureHandle source;
                public TextureHandle combineTex;
                public TextureHandle diffuseTex;
                public TextureHandle highlightTex;
                public int passIndex;
                public Vector2 texelSize;
                public float sampleScale;
            }

            public SoftBloomPass(Material material)
            {
                this.material = material;
                profilingSampler = new ProfilingSampler("RetroFX Soft Bloom");
                // Reads the camera's active color texture, so it can't
                // render straight to the backbuffer.
                requiresIntermediateTexture = true;
            }

            public void Setup(SoftBloomVolume settings)
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
                material.SetFloat("_DiffThreshold", settings.diffusionThreshold.value);
                material.SetFloat("_DiffKnee", Mathf.Max(settings.diffusionKnee.value, 1e-4f));
                material.SetFloat("_DiffClamp", settings.diffusionClamp.value);
                material.SetFloat("_DiffusionOpacity", settings.diffusionOpacity.value);
                material.SetFloat("_DiffusionSaturation", settings.diffusionSaturation.value);
                material.SetFloat("_DiffusionBlendMode", (float)(int)settings.diffusionBlendMode.value);
                material.SetFloat("_HighlightIntensity", settings.highlightIntensity.value);
                material.SetColor("_HighlightTint", settings.highlightTint.value);
                material.SetFloat("_HighlightSaturation", settings.highlightSaturation.value);

                int fullWidth = cameraData.cameraTargetDescriptor.width;
                int fullHeight = cameraData.cameraTargetDescriptor.height;

                var baseDesc = renderGraph.GetTextureDesc(source);
                baseDesc.clearBuffer = false;
                baseDesc.msaaSamples = MSAASamples.None;

                // ---- Diffusion layer: threshold (default off), then blur pyramid ----
                int dDown = Mathf.Max(1, settings.diffusionDownsample.value);
                int dW = Mathf.Max(4, fullWidth / dDown);
                int dH = Mathf.Max(4, fullHeight / dDown);
                int dExtra = Mathf.Max(1, settings.diffusionIterations.value) - 1;

                var diffPrefilterDesc = baseDesc;
                diffPrefilterDesc.width = dW;
                diffPrefilterDesc.height = dH;
                diffPrefilterDesc.name = "SoftBloom_DiffPrefilter";
                TextureHandle diffPrefiltered = renderGraph.CreateTexture(diffPrefilterDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Soft Bloom Diffuse Prefilter", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.passIndex = Passes.PrefilterDiffuse;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(diffPrefiltered, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }

                TextureHandle diffuseResult = BuildBloomChain(
                    renderGraph, diffPrefiltered, dW, dH,
                    dW, dH, dExtra, baseDesc, "SoftBloom_Diffuse", settings.diffusionSpread.value);

                // ---- Highlight layer: threshold first, then blur pyramid ----
                int hDown = Mathf.Max(1, settings.highlightDownsample.value);
                int hW = Mathf.Max(4, fullWidth / hDown);
                int hH = Mathf.Max(4, fullHeight / hDown);
                int hExtra = Mathf.Max(1, settings.highlightIterations.value) - 1;

                var prefilterDesc = baseDesc;
                prefilterDesc.width = hW;
                prefilterDesc.height = hH;
                prefilterDesc.name = "SoftBloom_Prefilter";
                TextureHandle prefiltered = renderGraph.CreateTexture(prefilterDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Soft Bloom Prefilter", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.passIndex = Passes.PrefilterHighlight;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(prefiltered, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }

                TextureHandle highlightResult = BuildBloomChain(
                    renderGraph, prefiltered, hW, hH,
                    hW, hH, hExtra, baseDesc, "SoftBloom_Highlight", settings.highlightSpread.value);

                // ---- Composite ----
                var compositeDesc = renderGraph.GetTextureDesc(source);
                compositeDesc.name = "_SoftBloom_Composite";
                compositeDesc.clearBuffer = false;
                compositeDesc.msaaSamples = MSAASamples.None;
                TextureHandle compositeOutput = renderGraph.CreateTexture(compositeDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Soft Bloom Composite", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.diffuseTex = diffuseResult;
                    passData.highlightTex = highlightResult;
                    passData.passIndex = Passes.Composite;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(diffuseResult, AccessFlags.Read);
                    builder.UseTexture(highlightResult, AccessFlags.Read);
                    builder.SetRenderAttachment(compositeOutput, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalTexture("_DiffuseTex", data.diffuseTex);
                        ctx.cmd.SetGlobalTexture("_HighlightTex", data.highlightTex);
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }

                resourceData.cameraColor = compositeOutput;
            }

            /// <summary>
            /// Builds a downsample/upsample blur pyramid from entrySource and
            /// returns the final blurred result at (level0Width, level0Height).
            /// extraLevels controls how many additional halvings happen below
            /// level 0 before upsampling back - 0 means level 0 is the only
            /// level (a single soft downsample+upsample pass).
            /// </summary>
            private TextureHandle BuildBloomChain(
                RenderGraph renderGraph,
                TextureHandle entrySource, int entryWidth, int entryHeight,
                int level0Width, int level0Height, int extraLevels,
                TextureDesc baseDesc, string namePrefix, float sampleScale)
            {
                var mips = new List<TextureHandle>();
                var sizes = new List<Vector2Int>();

                var desc0 = baseDesc;
                desc0.width = level0Width;
                desc0.height = level0Height;
                desc0.name = namePrefix + "_L0";
                TextureHandle mip0 = renderGraph.CreateTexture(desc0);
                AddDownsamplePass(renderGraph, entrySource, new Vector2(1f / entryWidth, 1f / entryHeight), mip0, namePrefix + " L0 Downsample");
                mips.Add(mip0);
                sizes.Add(new Vector2Int(level0Width, level0Height));

                int w = level0Width, h = level0Height;
                for (int i = 0; i < extraLevels; i++)
                {
                    int pw = w, ph = h;
                    w = Mathf.Max(4, w / 2);
                    h = Mathf.Max(4, h / 2);

                    var desc = baseDesc;
                    desc.width = w;
                    desc.height = h;
                    desc.name = namePrefix + "_L" + (i + 1);
                    TextureHandle mip = renderGraph.CreateTexture(desc);
                    AddDownsamplePass(renderGraph, mips[mips.Count - 1], new Vector2(1f / pw, 1f / ph), mip, namePrefix + " L" + (i + 1) + " Downsample");
                    mips.Add(mip);
                    sizes.Add(new Vector2Int(w, h));
                }

                TextureHandle acc = mips[mips.Count - 1];
                Vector2Int accSize = sizes[sizes.Count - 1];

                for (int i = mips.Count - 2; i >= 0; i--)
                {
                    var desc = baseDesc;
                    desc.width = sizes[i].x;
                    desc.height = sizes[i].y;
                    desc.name = namePrefix + "_U" + i;
                    TextureHandle next = renderGraph.CreateTexture(desc);
                    AddUpsampleAddPass(renderGraph, acc, new Vector2(1f / accSize.x, 1f / accSize.y), sampleScale, mips[i], next, namePrefix + " Upsample " + i);
                    acc = next;
                    accSize = sizes[i];
                }

                return acc;
            }

            private void AddDownsamplePass(RenderGraph renderGraph, TextureHandle source, Vector2 texelSize, TextureHandle destination, string passName)
            {
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = source;
                    passData.passIndex = Passes.Downsample;
                    passData.texelSize = texelSize;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        data.material.SetVector("_TexelSize", data.texelSize);
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }
            }

            private void AddUpsampleAddPass(RenderGraph renderGraph, TextureHandle lowResSource, Vector2 texelSize, float sampleScale, TextureHandle combineWith, TextureHandle destination, string passName)
            {
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.source = lowResSource;
                    passData.combineTex = combineWith;
                    passData.passIndex = Passes.UpsampleAdd;
                    passData.texelSize = texelSize;
                    passData.sampleScale = sampleScale;
                    builder.UseTexture(lowResSource, AccessFlags.Read);
                    builder.UseTexture(combineWith, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        data.material.SetVector("_TexelSize", data.texelSize);
                        data.material.SetFloat("_SampleScale", data.sampleScale);
                        ctx.cmd.SetGlobalTexture("_CombineTex", data.combineTex);
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
                    });
                }
            }
        }
    }
}

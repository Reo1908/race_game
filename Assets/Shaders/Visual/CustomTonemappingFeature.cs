using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Custom tonemapping operators (Lottes, Uncharted 2 / Hable) for URP on Unity 6.
//
// SETUP:
// 1. Create a material from "Hidden/Custom/Tonemapping" (CustomTonemapping.shader).
// 2. Add this Renderer Feature to your Universal Renderer asset.
// 3. Assign the material in the Settings.
// 4. In your Volume's Tonemapping override, set Mode = None. Otherwise Unity's
//    own tonemapper runs first and you'll be double-tonemapping.
// 5. This pass runs at AfterRenderingPostProcessing by default so it sits after
//    URP's own post stack (exposure, bloom, etc.) but still before UI/overlay.
public class CustomTonemappingFeature : ScriptableRendererFeature
{
    public enum TonemapOperator { Lottes, Uncharted2, AgX }

    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        public Material material;
        public TonemapOperator tonemapOperator = TonemapOperator.Uncharted2;

        [Header("Uncharted 2 (Hable Filmic)")]
        public float shoulderStrength = 0.15f;
        public float linearStrength = 0.50f;
        public float linearAngle = 0.10f;
        public float toeStrength = 0.20f;
        public float toeNumerator = 0.02f;
        public float toeDenominator = 0.30f;
        public float linearWhitePoint = 11.2f;

        [Header("Lottes")]
        public float contrast = 1.6f;
        public float shoulder = 0.977f;
        public float hdrMax = 8.0f;
        public float midIn = 0.18f;
        public float midOut = 0.267f;

        [Header("AgX")]
        [Tooltip("EV offset applied before the AgX curve. AgX's fixed input range is centered darker than most scenes, so a small positive bias (try 0-1) often looks right.")]
        public float agxExposureBias = 0.0f;
    }

    public Settings settings = new Settings();
    CustomTonemappingPass pass;

    public override void Create()
    {
        pass = new CustomTonemappingPass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.material == null)
            return;

        pass.renderPassEvent = settings.renderPassEvent;
        renderer.EnqueuePass(pass);
    }

    class CustomTonemappingPass : ScriptableRenderPass
    {
        readonly Settings settings;

        class PassData
        {
            public TextureHandle source;
            public Material material;
        }

        public CustomTonemappingPass(Settings settings)
        {
            this.settings = settings;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            // Never write directly to the backbuffer here.
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.activeColorTexture;

            var desc = renderGraph.GetTextureDesc(source);
            desc.name = "_CustomTonemapOutput";
            desc.clearBuffer = false;
            desc.depthBufferBits = 0;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            UpdateMaterialProperties();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Custom Tonemapping", out var passData))
            {
                passData.source = source;
                passData.material = settings.material;

                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            // Hand the result back to the pipeline as the new camera color.
            resourceData.cameraColor = destination;
        }

        void UpdateMaterialProperties()
        {
            var mat = settings.material;

            mat.DisableKeyword("_UNCHARTED2");
            mat.DisableKeyword("_LOTTES");
            mat.DisableKeyword("_AGX");

            switch (settings.tonemapOperator)
            {
                case TonemapOperator.Uncharted2:
                    mat.EnableKeyword("_UNCHARTED2");
                    mat.SetFloat("_ShoulderStrength", settings.shoulderStrength);
                    mat.SetFloat("_LinearStrength", settings.linearStrength);
                    mat.SetFloat("_LinearAngle", settings.linearAngle);
                    mat.SetFloat("_ToeStrength", settings.toeStrength);
                    mat.SetFloat("_ToeNumerator", settings.toeNumerator);
                    mat.SetFloat("_ToeDenominator", settings.toeDenominator);
                    mat.SetFloat("_LinearWhitePoint", settings.linearWhitePoint);
                    break;

                case TonemapOperator.Lottes:
                    mat.EnableKeyword("_LOTTES");
                    mat.SetFloat("_Contrast", settings.contrast);
                    mat.SetFloat("_Shoulder", settings.shoulder);
                    mat.SetFloat("_HdrMax", settings.hdrMax);
                    mat.SetFloat("_MidIn", settings.midIn);
                    mat.SetFloat("_MidOut", settings.midOut);
                    break;

                case TonemapOperator.AgX:
                    mat.EnableKeyword("_AGX");
                    mat.SetFloat("_AgXExposureBias", settings.agxExposureBias);
                    break;
            }
        }
    }
}

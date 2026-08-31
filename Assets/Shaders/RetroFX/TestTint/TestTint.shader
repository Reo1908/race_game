Shader "Hidden/RetroFX/TestTint"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "TestTint"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _TintColor;
            float _TintAmount;

            half4 Frag(Varyings i) : SV_Target
            {
                half3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
                half3 result = lerp(scene, _TintColor.rgb, _TintAmount);
                return half4(result, 1.0h);
            }
            ENDHLSL
        }
    }
}

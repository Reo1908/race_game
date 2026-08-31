Shader "Hidden/RetroFX/AnimeBloom"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // The Blit.hlsl file provides the vertex shader (Vert), the input
        // structure (Attributes) and the output structure (Varyings).
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // _BlitTexture is provided by Blit.hlsl / Blitter.BlitTexture
        TEXTURE2D_X(_BloomTex);

        float _Threshold;
        float _Knee;
        float _Clamp;
        float4 _BlurOffset;   // xy = texel offset for this blur pass (already includes stretch + spread)
        float _Intensity;
        float4 _Tint;
        float _Saturation;
        float _Diffusion;

        half3 Prefilter(half3 c)
        {
            half brightness = max(c.r, max(c.g, c.b));
            half soft = brightness - _Threshold + _Knee;
            soft = clamp(soft, 0.0h, 2.0h * _Knee);
            soft = soft * soft / (4.0h * _Knee + 1e-5h);
            half contribution = max(soft, brightness - _Threshold);
            contribution /= max(brightness, 1e-5h);
            c *= contribution;
            c = min(c, _Clamp.xxx);
            return max(c, 0.0h);
        }

        half4 FragPrefilter(Varyings i) : SV_Target
        {
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            return half4(Prefilter(c), 1.0h);
        }

        // 4-tap "kawase" style blur - cheap, and the slightly blocky falloff
        // reads as chunky/retro rather than a perfectly smooth gaussian.
        half4 FragKawase(Varyings i) : SV_Target
        {
            float2 uv = i.texcoord;
            float2 o = _BlurOffset.xy;
            half3 c = 0.0h;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( o.x,  o.y)).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x,  o.y)).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( o.x, -o.y)).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, -o.y)).rgb;
            c *= 0.25h;
            return half4(c, 1.0h);
        }

        half4 FragComposite(Varyings i) : SV_Target
        {
            half3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            half3 bloom = SAMPLE_TEXTURE2D_X(_BloomTex, sampler_LinearClamp, i.texcoord).rgb;

            half lum = dot(bloom, half3(0.2126h, 0.7152h, 0.0722h));
            bloom = lerp(lum.xxx, bloom, _Saturation);
            bloom *= _Tint.rgb * _Intensity;

            // "diffusion" softly lifts the base image toward the bloom color,
            // approximating an optical diffusion filter instead of pure additive glow.
            half3 washed = lerp(scene, scene + bloom, 1.0h) + bloom * _Diffusion;
            half3 result = scene + bloom + bloom * _Diffusion * 0.5h;

            return half4(result, 1.0h);
        }
        ENDHLSL

        Pass
        {
            Name "Prefilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilter
            ENDHLSL
        }

        Pass
        {
            Name "Kawase"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragKawase
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}

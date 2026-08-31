Shader "Hidden/RetroFX/CameraStreak"
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

        TEXTURE2D_X(_StreakTex);

        float _Threshold;
        float _Knee;
        float _Clamp;
        float2 _StreakDir;      // normalized direction, in UV space (already accounts for aspect)
        float _StreakOffset;    // per-pass sample distance, in UV units
        float _Attenuation;
        float _Intensity;
        float4 _Tint;
        float _ChromaticFringe; // in UV units

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

        // Directional accumulation: samples forward and backward along the
        // streak direction and fades by _Attenuation. Called repeatedly with
        // a growing _StreakOffset to build up a long trail cheaply.
        half4 FragStreak(Varyings i) : SV_Target
        {
            float2 uv = i.texcoord;
            float2 d = _StreakDir * _StreakOffset;

            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            half3 fwd = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + d).rgb;
            half3 bwd = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - d).rgb;

            c = max(c, max(fwd, bwd) * _Attenuation);
            return half4(c, 1.0h);
        }

        half4 FragComposite(Varyings i) : SV_Target
        {
            float2 uv = i.texcoord;
            float2 fringe = _StreakDir * _ChromaticFringe;

            half r = SAMPLE_TEXTURE2D_X(_StreakTex, sampler_LinearClamp, uv + fringe).r;
            half g = SAMPLE_TEXTURE2D_X(_StreakTex, sampler_LinearClamp, uv).g;
            half b = SAMPLE_TEXTURE2D_X(_StreakTex, sampler_LinearClamp, uv - fringe).b;
            half3 streak = half3(r, g, b);

            streak *= _Tint.rgb * _Intensity;

            half3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            return half4(scene + streak, 1.0h);
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
            Name "Streak"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragStreak
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}

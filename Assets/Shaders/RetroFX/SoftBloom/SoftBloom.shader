Shader "Hidden/RetroFX/SoftBloom"
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

        TEXTURE2D_X(_CombineTex);
        TEXTURE2D_X(_DiffuseTex);
        TEXTURE2D_X(_HighlightTex);

        // Highlight layer threshold.
        float _Threshold;
        float _Knee;
        float _Clamp;

        // Diffusion layer threshold (separate from Highlight's).
        float _DiffThreshold;
        float _DiffKnee;
        float _DiffClamp;

        float2 _TexelSize;
        float _SampleScale;

        float _DiffusionOpacity;
        float _DiffusionSaturation;
        float _DiffusionBlendMode; // 0 = Normal, 1 = Additive, 2 = Screen

        float _HighlightIntensity;
        float4 _HighlightTint;
        float _HighlightSaturation;

        half3 PrefilterWith(half3 c, float th, float kn, float cl)
        {
            half brightness = max(c.r, max(c.g, c.b));
            half soft = brightness - th + kn;
            soft = clamp(soft, 0.0h, 2.0h * kn);
            soft = soft * soft / (4.0h * kn + 1e-5h);
            half contribution = max(soft, brightness - th);
            contribution /= max(brightness, 1e-5h);
            c *= contribution;
            c = min(c, cl.xxx);
            return max(c, 0.0h);
        }

        half4 FragPrefilterHighlight(Varyings i) : SV_Target
        {
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            return half4(PrefilterWith(c, _Threshold, _Knee, _Clamp), 1.0h);
        }

        half4 FragPrefilterDiffuse(Varyings i) : SV_Target
        {
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            return half4(PrefilterWith(c, _DiffThreshold, _DiffKnee, _DiffClamp), 1.0h);
        }

        // 13-tap box downsample (the classic "dual filtering" downsample
        // used by Call of Duty / most modern engine blooms). This wide,
        // weighted average is what makes the result look like a genuine
        // soft blur instead of a handful of overlapping ghost copies.
        half3 DownsampleBox13(float2 uv)
        {
            float2 t = _TexelSize;
            half3 A = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-1.0, -1.0)).rgb;
            half3 B = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.0, -1.0)).rgb;
            half3 C = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 1.0, -1.0)).rgb;
            half3 D = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-0.5, -0.5)).rgb;
            half3 E = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.5, -0.5)).rgb;
            half3 F = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-1.0,  0.0)).rgb;
            half3 G = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            half3 H = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 1.0,  0.0)).rgb;
            half3 I = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-0.5,  0.5)).rgb;
            half3 J = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.5,  0.5)).rgb;
            half3 K = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-1.0,  1.0)).rgb;
            half3 L = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.0,  1.0)).rgb;
            half3 M = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 1.0,  1.0)).rgb;

            half2 div = half2(0.5h, 0.125h) * 0.25h;
            half3 o = (D + E + I + J) * div.x;
            o += (A + B + G + F) * div.y;
            o += (B + C + H + G) * div.y;
            o += (F + G + L + K) * div.y;
            o += (G + H + M + L) * div.y;
            return o;
        }

        half4 FragDownsample(Varyings i) : SV_Target
        {
            return half4(DownsampleBox13(i.texcoord), 1.0h);
        }

        // 9-tap tent upsample. Reconstructing the pyramid this way (instead
        // of a plain bilinear upscale) is what keeps the blur smooth and
        // "thick" rather than blocky at wide radii.
        half3 UpsampleTent9(float2 uv)
        {
            float4 d = _TexelSize.xyxy * float4(1.0, 1.0, -1.0, 0.0) * _SampleScale;

            half3 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - d.xy).rgb;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - d.wy).rgb * 2.0h;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - d.zy).rgb;

            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + d.zw).rgb * 2.0h;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb * 4.0h;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + d.xw).rgb * 2.0h;

            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + d.zy).rgb;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + d.wy).rgb * 2.0h;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + d.xy).rgb;

            return s * (1.0h / 16.0h);
        }

        half4 FragUpsampleAdd(Varyings i) : SV_Target
        {
            half3 up = UpsampleTent9(i.texcoord);
            half3 combine = SAMPLE_TEXTURE2D_X(_CombineTex, sampler_LinearClamp, i.texcoord).rgb;
            return half4(up + combine, 1.0h);
        }

        half3 ApplyDiffusionBlend(half3 scene, half3 diffuse, float opacity, float mode)
        {
            half3 normalBlend = lerp(scene, diffuse, opacity);
            half3 additiveBlend = scene + diffuse * opacity;
            half3 screenBlend = 1.0h - (1.0h - scene) * (1.0h - diffuse * opacity);

            half3 result = normalBlend;
            if (mode > 1.5h)
            {
                result = screenBlend;
            }
            else if (mode > 0.5h)
            {
                result = additiveBlend;
            }
            return result;
        }

        half4 FragComposite(Varyings i) : SV_Target
        {
            half3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            half3 diffuse = SAMPLE_TEXTURE2D_X(_DiffuseTex, sampler_LinearClamp, i.texcoord).rgb;
            half3 highlight = SAMPLE_TEXTURE2D_X(_HighlightTex, sampler_LinearClamp, i.texcoord).rgb;

            half diffuseLum = dot(diffuse, half3(0.2126h, 0.7152h, 0.0722h));
            diffuse = lerp(diffuseLum.xxx, diffuse, _DiffusionSaturation);

            // "Double exposure": blend the sharp frame with a fully
            // defocused copy of itself - like shooting two exposures on
            // the same frame of film with the lens racked out of focus.
            half3 result = ApplyDiffusionBlend(scene, diffuse, _DiffusionOpacity, _DiffusionBlendMode);

            // Separate, more concentrated bloom that only picks out the
            // very brightest highlights and adds a stronger glow on top.
            half lum = dot(highlight, half3(0.2126h, 0.7152h, 0.0722h));
            highlight = lerp(lum.xxx, highlight, _HighlightSaturation);
            result += highlight * _HighlightTint.rgb * _HighlightIntensity;

            return half4(result, 1.0h);
        }
        ENDHLSL

        Pass
        {
            Name "PrefilterHighlight"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilterHighlight
            ENDHLSL
        }

        Pass
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDownsample
            ENDHLSL
        }

        Pass
        {
            Name "UpsampleAdd"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUpsampleAdd
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

        Pass
        {
            Name "PrefilterDiffuse"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilterDiffuse
            ENDHLSL
        }
    }
}

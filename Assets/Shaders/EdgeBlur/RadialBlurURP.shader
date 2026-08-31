Shader "Custom/RadialBlur"
{
    Properties
    {
        _BlurStrength ("Blur Strength", Range(0, 0.5)) = 0.05
        _Samples ("Samples", Range(1, 16)) = 6
        _CenterX ("Center X", Range(0, 1)) = 0.5
        _CenterY ("Center Y", Range(0, 1)) = 0.5
        _DeadzoneRadius ("Deadzone Radius", Range(0, 0.5)) = 0.15
        _DeadzoneFeather ("Deadzone Feather", Range(0.001, 0.5)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "RadialBlurPass"

            HLSLPROGRAM
            // Blit.hlsl provides Attributes/Varyings/Vert plus the
            // _BlitTexture/_BlitMipLevel bindings that AddBlitPass wires
            // up automatically.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float _BlurStrength;
            int _Samples;
            float _CenterX;
            float _CenterY;
            float _DeadzoneRadius;
            float _DeadzoneFeather;

            // Cheap radial/zoom blur: samples along the line from the
            // screen center to this pixel. A deadzone keeps the middle
            // of the screen clear so the player can still see, with a
            // soft feathered edge rather than a hard cutoff.
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord.xy;

                float2 center = float2(_CenterX, _CenterY);
                float2 dir = uv - center;
                float dist = length(dir);

                // 0 inside the deadzone, ramps up to 1 past the feather region.
                float mask = smoothstep(_DeadzoneRadius, _DeadzoneRadius + _DeadzoneFeather, dist);
                float strength = _BlurStrength * mask;

                half4 col = half4(0, 0, 0, 0);
                float total = 0;

                for (int s = 0; s < 16; s++)
                {
                    if (s >= _Samples) break;
                    float t = s / (float)max(_Samples - 1, 1);
                    float2 sampleUV = uv - dir * strength * t;
                    col += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, sampleUV, _BlitMipLevel);
                    total += 1;
                }

                col /= total;
                return col;
            }
            ENDHLSL
        }
    }
}

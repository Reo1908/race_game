Shader "Custom/Tonemapping"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    // The Blit.hlsl file provides the vertex shader (Vert), input structure
    // (Attributes) and output structure (Varyings) used below.
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

    // Uncharted 2 / Hable parameters
    float _ShoulderStrength;
    float _LinearStrength;
    float _LinearAngle;
    float _ToeStrength;
    float _ToeNumerator;
    float _ToeDenominator;
    float _LinearWhitePoint;

    // Lottes parameters
    float _Contrast;
    float _Shoulder;
    float _HdrMax;
    float _MidIn;
    float _MidOut;

    // AgX parameters
    float _AgXExposureBias;

    // AgX (minimal polynomial approximation of the reference LUT-based operator).
    // Matrices and polynomial fit are the widely-used approximation derived from
    // Troy Sobotka's AgX (as ported by Benjamin "Bjorn" Wrensch / used in Bevy & Godot).
    static const float3x3 AgXInsetMatrix = float3x3(
        0.856627153315983, 0.0951212405381588, 0.0482516061458583,
        0.137318972929847, 0.761241990602591,  0.101439036467562,
        0.11189821299995,  0.0767994186031903, 0.811302368396859);

    static const float3x3 AgXOutsetMatrix = float3x3(
        1.1271005818144368,  -0.11060664309660323, -0.016493938717834573,
        -0.1413297634984383,  1.157823702216272,   -0.016493938717834257,
        -0.14132976349843826,-0.11060664309660294,  1.2519364065950405);

    static const float AgxMinEv = -12.47393;
    static const float AgxMaxEv = 4.026069;

    float3 AgXDefaultContrastApprox(float3 x)
    {
        float3 x2 = x * x;
        float3 x4 = x2 * x2;
        return 15.5 * x4 * x2
             - 40.14 * x4 * x
             + 31.96 * x4
             - 6.868 * x2 * x
             + 0.4298 * x2
             + 0.1191 * x
             - 0.00232;
    }

    float3 AgXTonemap(float3 color)
    {
        color *= exp2(_AgXExposureBias);

        color = mul(AgXInsetMatrix, color);
        color = max(color, 1e-10); // guard log2 against 0/negative
        color = log2(color);
        color = (color - AgxMinEv) / (AgxMaxEv - AgxMinEv);
        color = saturate(color);

        color = AgXDefaultContrastApprox(color);

        color = mul(AgXOutsetMatrix, color);
        // The polynomial above is fit in sRGB-ish space; undo that so the result
        // lines back up with the linear working space the rest of the pipeline expects.
        color = pow(max(0.0.xxx, color), 2.2);

        return saturate(color);
    }

    float3 Uncharted2TonemapCurve(float3 x)
    {
        float A = _ShoulderStrength;
        float B = _LinearStrength;
        float C = _LinearAngle;
        float D = _ToeStrength;
        float E = _ToeNumerator;
        float F = _ToeDenominator;
        return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
    }

    float3 Uncharted2Tonemap(float3 color)
    {
        float3 curr = Uncharted2TonemapCurve(color);
        float3 whiteScale = 1.0 / Uncharted2TonemapCurve(float3(_LinearWhitePoint, _LinearWhitePoint, _LinearWhitePoint));
        return curr * whiteScale;
    }

    // Timothy Lottes' filmic curve (GDC 2016, "Advanced Techniques and Optimization of HDR Color Pipelines")
    float3 LottesTonemap(float3 x)
    {
        float a = _Contrast;
        float d = _Shoulder;
        float hdrMax = _HdrMax;
        float midIn = _MidIn;
        float midOut = _MidOut;

        float b =
            (-pow(midIn, a) + pow(hdrMax, a) * midOut) /
            ((pow(hdrMax, a * d) - pow(midIn, a * d)) * midOut);
        float c =
            (pow(hdrMax, a * d) * pow(midIn, a) - pow(hdrMax, a) * pow(midIn, a * d) * midOut) /
            ((pow(hdrMax, a * d) - pow(midIn, a * d)) * midOut);

        return pow(x, a) / (pow(x, a * d) * b + c);
    }

    float4 Frag(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float2 uv = input.texcoord;

        float3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

        #if defined(_UNCHARTED2)
            color = Uncharted2Tonemap(color);
        #elif defined(_LOTTES)
            color = LottesTonemap(color);
        #elif defined(_AGX)
            color = AgXTonemap(color);
        #endif

        color = saturate(color);
        return float4(color, 1.0);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "CustomTonemapping"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ _UNCHARTED2 _LOTTES _AGX
            ENDHLSL
        }
    }
}

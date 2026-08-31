Shader "Custom/URP/VolumetricSmoke"
{
    // Raymarches procedural fbm noise through a unit cube to fake a
    // volumetric smoke puff — no baked 3D texture required.
    // Put this on a plain Cube mesh (no collider needed), scaled to the
    // size/shape of the puff you want.
    Properties
    {
        _Color ("Smoke Color", Color) = (0.55, 0.55, 0.55, 1)
        _Density ("Density Multiplier", Range(0, 5)) = 1.5
        _NoiseScale ("Noise Scale", Float) = 3.0
        _StepCount ("Ray Steps", Range(8, 64)) = 32
        _Intensity ("Intensity (driven by script, 0-1)", Range(0, 1)) = 0
        _ScrollSpeed ("Noise Scroll Speed", Vector) = (0, 0.35, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            // Render back faces only: this keeps the box looking correct
            // whether the camera is outside it or has driven inside it.
            Cull Front
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Density;
                float _NoiseScale;
                float _StepCount;
                float _Intensity;
                float4 _ScrollSpeed;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float valueNoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);

                return lerp(
                    lerp(lerp(hash(i + float3(0,0,0)), hash(i + float3(1,0,0)), f.x),
                         lerp(hash(i + float3(0,1,0)), hash(i + float3(1,1,0)), f.x), f.y),
                    lerp(lerp(hash(i + float3(0,0,1)), hash(i + float3(1,0,1)), f.x),
                         lerp(hash(i + float3(0,1,1)), hash(i + float3(1,1,1)), f.x), f.y), f.z);
            }

            float fbm(float3 p)
            {
                float v = 0.0;
                float a = 0.5;
                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    v += a * valueNoise(p);
                    p *= 2.0;
                    a *= 0.5;
                }
                return v;
            }

            // Ray-box intersection against the unit cube (-0.5 to 0.5 in object space)
            bool IntersectBox(float3 ro, float3 rd, out float t0, out float t1)
            {
                float3 invD = 1.0 / rd;
                float3 tA = (-0.5 - ro) * invD;
                float3 tB = (0.5 - ro) * invD;
                float3 tMin = min(tA, tB);
                float3 tMax = max(tA, tB);
                t0 = max(max(tMin.x, tMin.y), tMin.z);
                t1 = min(min(tMax.x, tMax.y), tMax.z);
                return t1 > max(t0, 0.0);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (_Intensity <= 0.001)
                    discard;

                float3 rayOriginWS = _WorldSpaceCameraPos;
                float3 rayDirWS = normalize(IN.positionWS - rayOriginWS);

                float3 rayOriginOS = TransformWorldToObject(rayOriginWS);
                float3 rayDirOS = normalize(TransformWorldToObjectDir(rayDirWS));

                float t0, t1;
                if (!IntersectBox(rayOriginOS, rayDirOS, t0, t1))
                    discard;
                t0 = max(t0, 0.0);

                int steps = (int)_StepCount;
                float dist = t1 - t0;
                float stepSize = dist / steps;
                float3 startPos = rayOriginOS + rayDirOS * t0;
                float3 scroll = _ScrollSpeed.xyz * _Time.y;

                float accumAlpha = 0.0;
                float3 accumColor = 0.0;

                for (int i = 0; i < steps; i++)
                {
                    float3 samplePos = startPos + rayDirOS * (stepSize * i);

                    // Soften density toward the box edges so it reads as a
                    // puff rather than a visible cube.
                    float edgeFade = 1.0 - saturate(length(samplePos) * 1.6);
                    float n = fbm((samplePos + scroll) * _NoiseScale);
                    float density = saturate(n * edgeFade * _Density * _Intensity);

                    accumColor += _Color.rgb * density * (1.0 - accumAlpha);
                    accumAlpha += density * (1.0 - accumAlpha);

                    if (accumAlpha > 0.98) break;
                }

                return half4(accumColor, accumAlpha);
            }
            ENDHLSL
        }
    }
}

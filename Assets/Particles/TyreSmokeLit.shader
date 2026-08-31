Shader "Custom/TyreSmokeLit"
{
    // Hand-written URP lit shader for billboard smoke particles.
    //
    // Why default particle shaders look wrong:
    // A billboard quad has ONE flat normal (pointing straight at the camera).
    // As you orbit the camera, that single normal sweeps through the light direction,
    // so the whole sprite pops from bright to dark uniformly - it reads as "the
    // particle changes color depending on angle".
    //
    // Fix used here:
    // Instead of one flat normal, we reconstruct a fake per-pixel "sphere" normal
    // from the UV coordinates (using the camera-facing direction Unity already
    // gives billboard particles as the base). This makes each particle shade like
    // a soft puffy volume instead of a flat card, so lighting changes gradually
    // and believably instead of flickering as the camera moves.

    Properties
    {
        [MainTexture] _MainTex ("Smoke Texture", 2D) = "white" {}
        [MainColor] _Color ("Tint", Color) = (1,1,1,1)
        _NormalCurvature ("Fake Sphere Normal Strength", Range(0,1)) = 0.7
        _AmbientBoost ("Ambient/GI Boost", Range(0,3)) = 1.2
        _LightWrap ("Light Wrap (soft translucency)", Range(0,1)) = 0.5
        _SoftParticlesDistance ("Soft Particles Fade Distance", Float) = 1.0
        _AlphaClipThreshold ("Alpha Cutout (0 = off)", Range(0,1)) = 0.0

        [Header(Background Blur)]
        _BlurStrength ("Blur Radius (screen UV)", Range(0, 1.0)) = 0.015
        _BlurTintAmount ("Smoke Tint Over Blur", Range(0,1)) = 0.6

        [Header(Procedural Animated Smoke Noise)]
        [Toggle(_USE_PROCEDURAL_NOISE)] _UseProceduralNoise ("Use Procedural Noise", Float) = 0
        _NoiseScale ("Noise Scale", Range(0.5, 12)) = 3.5
        _NoiseSpeed ("Noise Animation Speed", Range(0, 2)) = 0.15
        _NoiseContrast ("Noise Contrast", Range(0.5, 3)) = 1.4
        _EdgeSoftness ("Circular Edge Softness", Range(0.01, 1)) = 0.45
        _EdgeNoiseAmount ("Circular Edge Irregularity", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha // premultiplied - see frag() note near the return
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma shader_feature_local _USE_PROCEDURAL_NOISE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;   // Unity fills this with the camera-facing direction for billboard particles
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float4 color        : TEXCOORD1;
                float3 positionWS   : TEXCOORD2;
                float3 billboardFwd : TEXCOORD3;
                float2 screenUV     : TEXCOORD4;
                float  eyeDepth     : TEXCOORD5;
                float  fogCoord     : TEXCOORD6;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _NormalCurvature;
                float _AmbientBoost;
                float _LightWrap;
                float _SoftParticlesDistance;
                float _AlphaClipThreshold;
                float _BlurStrength;
                float _BlurTintAmount;
                float _NoiseScale;
                float _NoiseSpeed;
                float _NoiseContrast;
                float _EdgeSoftness;
                float _EdgeNoiseAmount;
            CBUFFER_END

            // ---------------------------------------------------------------------------
            // Procedural animated smoke noise: multi-octave value noise + circular fade.
            // No texture asset needed - it's driven entirely by _Time, so it never repeats
            // or tiles, and it costs nothing in memory.
            // ---------------------------------------------------------------------------

            float2 SmokeHash2(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return -1.0 + 2.0 * frac(sin(p) * 43758.5453123);
            }

            // Smooth 2D value noise, returns roughly [-1, 1].
            float SmokeValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f); // smoothstep interpolation, avoids grid artifacts

                float a = dot(SmokeHash2(i + float2(0, 0)), f - float2(0, 0));
                float b = dot(SmokeHash2(i + float2(1, 0)), f - float2(1, 0));
                float c = dot(SmokeHash2(i + float2(0, 1)), f - float2(0, 1));
                float d = dot(SmokeHash2(i + float2(1, 1)), f - float2(1, 1));

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // Fractal Brownian Motion: stack 3 octaves of noise, each one drifting in its
            // own direction/speed. That's what keeps layered noise from reading as one
            // static repeating blob - each layer disagrees with the others over time,
            // which is exactly what turbulent smoke actually looks like.
            float SmokeFBM(float2 uv, float time, float baseScale, float speed)
            {
                float sum = 0.0;
                float amplitude = 0.5;
                float scale = baseScale;

                float2 drifts[3] = {
                    float2( 0.35,  0.20) * speed,
                    float2(-0.18,  0.30) * speed,
                    float2( 0.22, -0.27) * speed
                };

                UNITY_UNROLL
                for (int i = 0; i < 3; i++)
                {
                    float2 p = uv * scale + drifts[i] * time;
                    sum += SmokeValueNoise(p) * amplitude;
                    scale *= 2.07;      // each octave roughly doubles in frequency
                    amplitude *= 0.5;   // and contributes half as much as the last
                }
                return sum * 0.5 + 0.5; // remap ~[-1,1] to [0,1]
            }

            // Soft round falloff from the sprite's center, with the edge itself perturbed
            // by noise so the silhouette reads as a wispy puff instead of a perfect disc.
            float SmokeCircularFade(float2 uv, float noiseSample, float edgeSoftness, float edgeNoiseAmount)
            {
                float dist = length(uv - 0.5) * 2.0; // 0 at center, 1 at the sprite's edge
                dist += (noiseSample - 0.5) * edgeNoiseAmount;
                return saturate(1.0 - smoothstep(1.0 - edgeSoftness, 1.0, dist));
            }

            // Cheap 9-tap blur of whatever is behind the particle (the already-rendered opaque scene).
            half3 SampleBlurredBackground(float2 uv, float radius)
            {
                const float2 offsets[8] = {
                    float2( 1,  0), float2(-1,  0), float2(0,  1), float2(0, -1),
                    float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                    float2( 0.7071, -0.7071), float2(-0.7071, -0.7071)
                };

                half3 sum = SampleSceneColor(uv).rgb;
                UNITY_UNROLL
                for (int i = 0; i < 8; i++)
                {
                    sum += SampleSceneColor(uv + offsets[i] * radius).rgb;
                }
                return sum / 9.0;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                // positionNDC.xy from GetVertexPositionInputs is NOT yet perspective-divided -
                // divide by positionCS.w ourselves to get a true 0..1 screen UV.
                OUT.screenUV = posInputs.positionNDC.xy / posInputs.positionCS.w;
                OUT.eyeDepth = posInputs.positionCS.w;

                OUT.billboardFwd = normalize(TransformObjectToWorldNormal(IN.normalOS));
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.color = IN.color * _Color;

                OUT.fogCoord = ComputeFogFactor(posInputs.positionCS.z);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half4 baseColor = tex * IN.color;

                #ifdef _USE_PROCEDURAL_NOISE
                    // Leave _MainTex as plain white if you want PURE procedural smoke, or
                    // keep a soft round gradient texture there to combine with this for
                    // extra shape control - either way this multiplies into baseColor.a.
                    float noiseVal = SmokeFBM(IN.uv, _Time.y, _NoiseScale, _NoiseSpeed);
                    float circularMask = SmokeCircularFade(IN.uv, noiseVal, _EdgeSoftness, _EdgeNoiseAmount);
                    float proceduralDensity = saturate(pow(noiseVal * circularMask, _NoiseContrast));
                    baseColor.a *= proceduralDensity;
                #endif

                if (_AlphaClipThreshold > 0.0)
                    clip(baseColor.a - _AlphaClipThreshold);

                // --- Fake sphere normal reconstruction ---
                float3 forwardWS = normalize(IN.billboardFwd);
                float3 upGuess = (abs(forwardWS.y) > 0.99) ? float3(0, 0, 1) : float3(0, 1, 0);
                float3 rightWS = normalize(cross(upGuess, forwardWS));
                float3 upWS = cross(forwardWS, rightWS);

                float2 centered = IN.uv * 2.0 - 1.0;
                float r2 = saturate(dot(centered, centered));
                float3 sphereNormal = normalize(
                    rightWS * centered.x +
                    upWS * centered.y +
                    forwardWS * sqrt(max(0.0001, 1.0 - r2)));

                float3 shadingNormal = normalize(lerp(forwardWS, sphereNormal, _NormalCurvature));

                // --- Main light (wrapped diffuse so smoke isn't harshly black on its dark side) ---
                Light mainLight = GetMainLight();
                float wrappedNdotL = dot(shadingNormal, mainLight.direction) * (1.0 - _LightWrap * 0.5) + _LightWrap * 0.5;
                wrappedNdotL = saturate(wrappedNdotL);
                half3 lighting = mainLight.color * mainLight.distanceAttenuation * wrappedNdotL;

                // --- Ambient / GI, stable regardless of view angle ---
                lighting += SampleSH(shadingNormal) * _AmbientBoost;

                // --- Additional lights (e.g. headlights/taillights hitting the smoke) ---
                #ifdef _ADDITIONAL_LIGHTS
                    int additionalLightsCount = GetAdditionalLightsCount();
                    for (int i = 0; i < additionalLightsCount; i++)
                    {
                        Light addLight = GetAdditionalLight(i, IN.positionWS);
                        float addNdotL = saturate(dot(shadingNormal, addLight.direction) * (1.0 - _LightWrap * 0.5) + _LightWrap * 0.5);
                        lighting += addLight.color * addLight.distanceAttenuation * addNdotL;
                    }
                #endif

                // --- Soft particles: fade out where the sprite intersects nearby geometry ---
                float rawDepth = SampleSceneDepth(IN.screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float softFactor = saturate((sceneEyeDepth - IN.eyeDepth) / max(_SoftParticlesDistance, 0.0001));

                half3 litSmoke = baseColor.rgb * lighting;

                // --- Blur whatever is behind the smoke ---
                // (hardware alpha blending only interpolates one pixel with itself - it can't blur,
                // so we manually sample and average neighbouring pixels of the opaque scene here,
                // then blend our own lit smoke color on top of that blurred result.)
                // NOTE: radius is no longer scaled by baseColor.a. The wispy, low-alpha edges are
                // exactly where you want the halo to spread - shrinking the radius there was
                // killing the effect right where it mattered most.
                float blurRadius = _BlurStrength;
                half3 blurredBG = SampleBlurredBackground(IN.screenUV, blurRadius);
                half3 smokeOverBlur = lerp(blurredBG, litSmoke, _BlurTintAmount);

                // How "present" the smoke is at this pixel: its own alpha plus the soft-particle fade.
                half coverage = saturate(baseColor.a * softFactor);

                half3 finalColor = MixFog(smokeOverBlur, IN.fogCoord);

                // IMPORTANT: this pass uses PREMULTIPLIED alpha (Blend One OneMinusSrcAlpha).
                // finalColor above is already the fully-composited pixel (smoke blended over our
                // manually-blurred background). Blending it again with regular
                // SrcAlpha/OneMinusSrcAlpha would mix it with the real, SHARP back-buffer a second
                // time - that double-mix is what was cancelling out almost all of the blur.
                // Premultiplying by coverage means: fully transparent pixels show the real scene
                // untouched, fully covered pixels show our blurred composite untouched, and
                // everything in between blends cleanly with no second dilution.
                return half4(finalColor * coverage, coverage);
            }
            ENDHLSL
        }
    }
}

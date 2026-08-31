Shader "Hidden/RetroFX/PhosphorTrail"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D_X(_HistoryTex);
        TEXTURE2D_X(_DecayedTex);
        TEXTURE2D_X(_FreshTex);
        TEXTURE2D_X(_TrailTex);
        TEXTURE2D_X(_MotionVectorTexture);
        SAMPLER(sampler_MotionVectorTexture);

        float _Threshold;
        float _Knee;
        float _Clamp;
        float _Decay;
        float2 _TexelSize;
        float _StampSpread;
        float _SmearAmount;
        int _SmearSteps;
        float4 _Tint;
        float _Intensity;

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

        // Pass 0: threshold the current frame (also serves as the initial downsample).
        half4 FragPrefilter(Varyings i) : SV_Target
        {
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            return half4(Prefilter(c), 1.0h);
        }

        // Pass 1: widen ("dilate") this frame's fresh bright pixels using a
        // max-filter ring, BEFORE they get written into the trail. At high
        // speeds a moving light can travel more than one stamp's width
        // between frames, leaving gaps that read as a choppy string of
        // blobs instead of one continuous streak. Fattening each frame's
        // stamp makes consecutive frames overlap again. max() (not an
        // average) keeps this from just dimming/blurring the highlight.
        half3 Dilate(float2 uv)
        {
            float2 t = _TexelSize * _StampSpread;
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 1.0,  0.0)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-1.0,  0.0)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.0,  1.0)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.0, -1.0)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.7071,  0.7071)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-0.7071,  0.7071)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2( 0.7071, -0.7071)).rgb);
            c = max(c, SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * float2(-0.7071, -0.7071)).rgb);
            return c;
        }

        half4 FragDilate(Varyings i) : SV_Target
        {
            return half4(Dilate(i.texcoord), 1.0h);
        }

        // Pass 2: smear each bright pixel BACKWARD along its own motion
        // vector, from its current position to roughly where it was last
        // frame. Dilate above fattens a stamp uniformly in every direction
        // (helps a little at any speed); this instead connects it
        // specifically along the path of travel, which is what actually
        // turns a string of separate blobs into one continuous line at
        // high speed. Also works for a moving CAMERA past a static light,
        // since URP's motion vectors include camera motion too - matching
        // real tube-camera trail footage where the camera itself pans.
        half3 MotionSmear(float2 uv)
        {
            float2 mv = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_MotionVectorTexture, uv).xy * _SmearAmount;
            half3 result = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

            int steps = max(_SmearSteps, 1);
            for (int s = 1; s <= steps; s++)
            {
                float t = (float)s / (float)steps;
                float2 sampleUV = uv - mv * t;
                half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, sampleUV).rgb;
                result = max(result, c);
            }
            return result;
        }

        half4 FragMotionSmear(Varyings i) : SV_Target
        {
            return half4(MotionSmear(i.texcoord), 1.0h);
        }

        // Pass 2: fade the previous frame's history buffer.
        half4 FragDecay(Varyings i) : SV_Target
        {
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            return half4(c * _Decay, 1.0h);
        }

        // Pass 3: combine this frame's (dilated) bright pixels with the
        // decayed history. max() (rather than add) keeps the trail from
        // runaway brightening on repeated frames while still holding onto
        // strong highlights as they fade.
        half4 FragCombine(Varyings i) : SV_Target
        {
            half3 fresh = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            half3 decayed = SAMPLE_TEXTURE2D_X(_DecayedTex, sampler_LinearClamp, i.texcoord).rgb;
            return half4(max(fresh, decayed), 1.0h);
        }

        // Pass 4: subtract this frame's OWN (dilated) bright pixels from
        // the accumulated history before displaying it. Without this, a
        // light that isn't moving would show its full brightness added
        // back on top of the scene every single frame - i.e. it'd just
        // look like a second bloom layer. This leaves only the genuine
        // "leftover glow" from frames where the light *used* to be but
        // isn't right now, so a static light contributes nothing extra
        // and only motion trails show.
        half4 FragResidual(Varyings i) : SV_Target
        {
            half3 hist = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            half3 fresh = SAMPLE_TEXTURE2D_X(_FreshTex, sampler_LinearClamp, i.texcoord).rgb;
            return half4(max(hist - fresh, 0.0h), 1.0h);
        }

        // Pass 5: small 4-tap blur, used only for display - NOT fed back
        // into history, so it doesn't compound into runaway softness over time.
        half4 FragSoften(Varyings i) : SV_Target
        {
            float2 uv = i.texcoord;
            float2 o = _TexelSize;
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( o.x,  o.y)).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x,  o.y)).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( o.x, -o.y)).rgb;
            c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, -o.y)).rgb;
            c *= 0.2h;
            return half4(c, 1.0h);
        }

        half4 FragComposite(Varyings i) : SV_Target
        {
            half3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord).rgb;
            half3 trail = SAMPLE_TEXTURE2D_X(_TrailTex, sampler_LinearClamp, i.texcoord).rgb;
            trail *= _Tint.rgb * _Intensity;
            return half4(scene + trail, 1.0h);
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
            Name "Dilate"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDilate
            ENDHLSL
        }

        Pass
        {
            Name "MotionSmear"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMotionSmear
            ENDHLSL
        }

        Pass
        {
            Name "Decay"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDecay
            ENDHLSL
        }

        Pass
        {
            Name "Combine"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCombine
            ENDHLSL
        }

        Pass
        {
            Name "Residual"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragResidual
            ENDHLSL
        }

        Pass
        {
            Name "Soften"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragSoften
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

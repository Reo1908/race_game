// CRT/phosphor backdrop for the pause menu. Sits behind the whole menu on a
// RawImage that shows either the frozen (blurred) game frame or nothing at all.
//
// Everything animated is driven from _UnscaledTime instead of _Time, because
// the menu runs at Time.timeScale = 0 and the built-in _Time would be frozen
// solid — no rolling scan bar, no grain, just a dead still image.
Shader "UI/RetroPause/Backdrop"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _ScreenTint   ("Screen Tint", Color) = (0.031, 0.059, 0.098, 1)
        _TintAmount   ("Tint Amount", Range(0,1)) = 0.78
        _Desaturate   ("Desaturate", Range(0,1)) = 0.55
        _Brightness   ("Brightness", Range(0,2)) = 0.55

        _ScanCount    ("Scanline Count", Float) = 420
        _ScanAmount   ("Scanline Amount", Range(0,1)) = 0.22
        _SweepAmount  ("Sweep Amount", Range(0,1)) = 0.16
        _SweepWidth   ("Sweep Width", Range(0.01,0.9)) = 0.16

        _Vignette     ("Vignette", Range(0,2)) = 0.95
        _GrainAmount  ("Grain", Range(0,1)) = 0.07
        _Chroma       ("Chroma Split", Range(0,0.05)) = 0.0025

        _Phosphor     ("Phosphor Color", Color) = (0.44, 0.95, 1, 1)
        _Reveal       ("Reveal", Range(0,1)) = 1
        _UnscaledTime ("Unscaled Time", Float) = 0
        _FlipY        ("Flip Y", Float) = 0

        _StencilComp      ("Stencil Comparison", Float) = 8
        _Stencil          ("Stencil ID", Float) = 0
        _StencilOp        ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask  ("Stencil Read Mask", Float) = 255
        _ColorMask        ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "RetroPauseBackdrop"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;

            fixed4 _ScreenTint;
            fixed4 _Phosphor;
            float _TintAmount, _Desaturate, _Brightness;
            float _ScanCount, _ScanAmount, _SweepAmount, _SweepWidth;
            float _Vignette, _GrainAmount, _Chroma;
            float _Reveal, _UnscaledTime, _FlipY;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPos = v.vertex;
                OUT.vertex = UnityObjectToClipPos(v.vertex);
                OUT.texcoord = v.texcoord;
                OUT.color = v.color * _Color;
                return OUT;
            }

            // Cheap hash noise — enough for film grain, no texture needed.
            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                if (_FlipY > 0.5) uv.y = 1.0 - uv.y;

                float2 centered = uv - 0.5;

                // Barrel-ish pull toward the tube center, strongest at the corners.
                float r2 = dot(centered, centered);
                float2 warped = uv + centered * r2 * 0.045;

                // Chromatic split grows outward from the middle, like a real tube.
                float2 split = centered * _Chroma * (1.0 + r2 * 3.0);
                fixed3 col;
                col.r = tex2D(_MainTex, warped + split).r;
                col.g = tex2D(_MainTex, warped).g;
                col.b = tex2D(_MainTex, warped - split).b;

                // Desaturate toward luma, then push everything into the tint.
                float luma = dot(col, fixed3(0.299, 0.587, 0.114));
                col = lerp(col, luma.xxx, _Desaturate);
                col = lerp(col, _ScreenTint.rgb + luma * _Phosphor.rgb * 0.35, _TintAmount);
                col *= _Brightness;

                // Scanlines.
                float scan = sin(uv.y * _ScanCount * 3.14159265) * 0.5 + 0.5;
                col *= 1.0 - scan * _ScanAmount;

                // Aperture grille: dim every other screen-space column a touch.
                float grille = frac(uv.x * _ScreenParams.x * 0.5) < 0.5 ? 1.0 : 1.0 - _ScanAmount * 0.35;
                col *= grille;

                // Rolling brightness bar travelling bottom-to-top.
                float sweepPos = frac(_UnscaledTime * 0.18);
                float sweep = smoothstep(_SweepWidth, 0.0, abs(uv.y - sweepPos));
                col += _Phosphor.rgb * sweep * _SweepAmount;

                // Vignette.
                float vig = 1.0 - smoothstep(0.25, 0.78, length(centered) * _Vignette);
                col *= lerp(0.35, 1.0, vig);

                // Grain, re-seeded every frame off the unscaled clock.
                float grain = hash21(uv * 512.0 + frac(_UnscaledTime) * 97.0) - 0.5;
                col += grain * _GrainAmount;

                // Power-on wipe: the picture opens out from the horizontal centerline.
                float wipe = smoothstep(0.0, 1.0, _Reveal);
                float band = 1.0 - smoothstep(wipe * 0.55, wipe * 0.55 + 0.06, abs(centered.y));
                float edge = smoothstep(0.0, 0.04, wipe);
                // Hot phosphor line riding the leading edge of the wipe.
                float inner = 1.0 - smoothstep(wipe * 0.55 - 0.035, wipe * 0.55, abs(centered.y));
                col += _Phosphor.rgb * saturate(band - inner) * edge * 1.6;
                float alpha = IN.color.a * band * edge;

                fixed4 outColor = fixed4(col * IN.color.rgb, alpha);

                #ifdef UNITY_UI_CLIP_RECT
                outColor.a *= UnityGet2DClipping(IN.worldPos.xy, _ClipRect);
                #endif

                return outColor;
            }
        ENDCG
        }
    }
}

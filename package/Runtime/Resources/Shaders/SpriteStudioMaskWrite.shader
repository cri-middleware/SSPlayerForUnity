Shader "SpriteStudio/MaskWrite"
{
    // Stencil-write pass for masks (port of SS6PU Stencil_SpriteStudio6.shader). Writes only the
    // stencil buffer (ColorMask 0); the colour output is ignored. The stencil op (Increment /
    // Decrement / Invert), Ref and the bit-isolated Read/Write masks are property-driven and set by
    // the C# player per writer. A pure mask uses this with an Increment-class op; the matching
    // restore submesh reuses the same geometry with a Decrement-class op.
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}

        // Cutout threshold = (255 - PartState.mask) / 255. A texel is discarded (contributes no
        // coverage) when its alpha is <= the threshold, matching SS6PU / SSPlayerForGodot. A
        // negative value disables the cutout (e.g. Shape masks whose geometry itself is the mask).
        _MaskThreshold ("Mask Threshold", Float) = -1

        // --- Stencil state (bit-isolated; set by C#) ---
        _StencilRef       ("Stencil Ref",        Float) = 0
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilOp ("Stencil Op", Float) = 0 // Keep (no-op until configured)
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask  ("Stencil Read Mask",  Float) = 255
    }

    // === URP (Universal Render Pipeline) SubShader ===
    // Mirrors the Built-in SubShader below; only the engine API layer differs (URP include,
    // TransformObjectToHClip, SAMPLE_TEXTURE2D, CBUFFER for SRP Batcher). The property-driven Stencil
    // write and ColorMask 0 are identical. URP selects this via the RenderPipeline tag.
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        ColorMask 0
        Blend SrcAlpha OneMinusSrcAlpha

        Stencil
        {
            Ref [_StencilRef]
            Comp Always
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1; // [rate, blend_idx, isNormalized, reserved]
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;

            cbuffer UnityPerMaterial
            {
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float _MaskThreshold;
                float _StencilRef;
                float _StencilOp;
                float _StencilWriteMask;
                float _StencilReadMask;
            };

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                float isNormalized = IN.texcoord1.z;
                float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
                OUT.texcoord = IN.texcoord * texelSizeMultiplier;
                OUT.texcoord.y = 1.0 - OUT.texcoord.y;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                // Cutout: only texels above the threshold contribute to the stencil coverage.
                float a = tex2D(_MainTex, IN.texcoord).a;
                if (a <= _MaskThreshold)
                {
                    discard;
                }
                // ColorMask 0 makes the colour output irrelevant; only the stencil op runs.
                return half4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }

    // === Built-in Render Pipeline SubShader ===
    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest LEqual
        ColorMask 0
        Blend SrcAlpha OneMinusSrcAlpha

        Stencil
        {
            Ref [_StencilRef]
            Comp Always
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1; // [rate, blend_idx, isNormalized, reserved]
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _MaskThreshold;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                // Same UV convention as SpriteStudioDefault: pixel UVs (texcoord1.z == 0) are
                // normalized by the texel size; SS top-left origin is flipped to Unity bottom-left.
                float isNormalized = IN.texcoord1.z;
                float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
                OUT.texcoord = IN.texcoord * texelSizeMultiplier;
                OUT.texcoord.y = 1.0 - OUT.texcoord.y;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                // Cutout: only texels above the threshold contribute to the stencil coverage.
                float a = tex2D(_MainTex, IN.texcoord).a;
                if (a <= _MaskThreshold)
                {
                    discard;
                }
                // ColorMask 0 makes the colour output irrelevant; only the stencil op runs.
                return half4(0.0, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
}

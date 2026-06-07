Shader "SpriteStudio/Default"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}

        // --- Blend state (set by the C# MaterialKey per the per-blend blend-factor table) ---
        // Defaults are Mix + PMA output = equivalent to the previous behavior. The MaterialKey
        // overrides these per ssab BlendType.
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1   // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10  // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendOp)]   _BlendOp  ("Blend Op",  Float) = 0   // Add
        // PMA / discard are uniform branches (not keywords), so variants collapse to one.
        _InputPMA   ("Input PMA",   Float) = 0   // 1 = texture is premultiplied -> un-premultiply
        _OutputPMA  ("Output PMA",  Float) = 1   // 1 = emit premultiplied alpha (previous behavior)
        _NotDiscard ("Not Discard", Float) = 0   // 1 = keep transparent pixels (Mul/Invert)

        // --- Stencil state (mask test; property-driven, bit-isolated; set by C# per submesh) ---
        // Defaults are a no-op (Comp Always / Pass Keep) so non-masked parts are unaffected. A
        // masked target overrides _StencilComp (Equal/NotEqual) and _StencilReadMask (the isolated
        // bit) to keep or discard fragments based on the coverage written by SpriteStudio/MaskWrite.
        _StencilRef       ("Stencil Ref",        Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comp", Float) = 8 // Always
        [Enum(UnityEngine.Rendering.StencilOp)]       _StencilPass ("Stencil Pass", Float) = 0 // Keep
        _StencilReadMask  ("Stencil Read Mask",  Float) = 255
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
    }

    // === URP (Universal Render Pipeline) SubShader ===
    // Same colour core (SpriteStudioCore.hlsl) and property-driven Blend/Stencil as the Built-in
    // SubShader below; only the engine API layer differs (URP includes, TransformObjectToHClip,
    // TEXTURE2D/SAMPLE_TEXTURE2D, CBUFFER for SRP Batcher). URP selects this SubShader via the
    // RenderPipeline tag; the Built-in pipeline falls through to the second SubShader.
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        ZWrite Off
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]

        Stencil
        {
            Ref [_StencilRef]
            Comp [_StencilComp]
            Pass [_StencilPass]
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
            #include "Includes/SpriteStudioCore.hlsl"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1; // [rate, blend_idx, isNormalized(effect), reserved]
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                half4  color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1;
            };

            sampler2D _MainTex;

            // SRP Batcher: every per-material property lives in UnityPerMaterial (textures excluded).
            cbuffer UnityPerMaterial
            {
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float _InputPMA;
                float _OutputPMA;
                float _NotDiscard;
                float _SrcBlend;
                float _DstBlend;
                float _BlendOp;
                float _StencilRef;
                float _StencilComp;
                float _StencilPass;
                float _StencilReadMask;
                float _StencilWriteMask;
            };

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                // Normal parts carry pixel coordinates; effect parts carry normalized coordinates
                // (texcoord1.z = 1).
                float isNormalized = IN.texcoord1.z;
                float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
                OUT.texcoord = IN.texcoord * texelSizeMultiplier;
                // SS uses a top-left UV origin, Unity uses bottom-left -> flip V.
                OUT.texcoord.y = 1.0 - OUT.texcoord.y;
                OUT.color = IN.color;
                OUT.texcoord1 = IN.texcoord1;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                float4 tex = ss_input_mediate(tex2D(_MainTex, IN.texcoord), _InputPMA);

                float rate         = IN.texcoord1.x;
                float blendIdx     = IN.texcoord1.y;
                float isNormalized = IN.texcoord1.z;

                float3 rgb;
                if (isNormalized > 0.5)
                    rgb = tex.rgb * IN.color.rgb;       // effect: tint by vertex color
                else
                    rgb = ss_partcolor_blend(tex.rgb, IN.color.rgb, rate, blendIdx);

                float alpha = tex.a * IN.color.a;
                float4 col = float4(rgb, alpha);

                if (ss_should_discard(col.a, 0.0, _NotDiscard))
                    discard;

                col = ss_output_color(col, _OutputPMA);

                // Effect additive a=0 trick (mirrors the Built-in pass; see TODO(MaterialKey)).
                if (isNormalized > 0.5 && abs(blendIdx - 2.0) < 0.5)
                    col.a = 0.0;

                return col;
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
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        // Blend state is material-property driven (references [_Prop] rather than fixed values).
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]

        // Stencil state is also property-driven (render state, not a keyword -> no variant blow-up,
        // no pink/missing-variant risk). Defaults are a no-op for non-masked parts.
        Stencil
        {
            Ref [_StencilRef]
            Comp [_StencilComp]
            Pass [_StencilPass]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Includes/SpriteStudioCore.hlsl"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1; // [rate, blend_idx, isNormalized(effect), reserved]
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                half4  color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 texcoord1: TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float _InputPMA;
            float _OutputPMA;
            float _NotDiscard;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                // Normal parts carry pixel coordinates; effect parts carry normalized coordinates
                // (texcoord1.z = 1).
                float isNormalized = IN.texcoord1.z;
                float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
                OUT.texcoord = IN.texcoord * texelSizeMultiplier;
                // SS uses a top-left UV origin, Unity uses bottom-left -> flip V (once, here, not on the CPU).
                OUT.texcoord.y = 1.0 - OUT.texcoord.y;
                OUT.color = IN.color;
                OUT.texcoord1 = IN.texcoord1;
                return OUT;
            }

            half4 frag(v2f IN) : SV_Target
            {
                // [input] Sample the texture (and un-premultiply if needed).
                float4 tex = ss_input_mediate(tex2D(_MainTex, IN.texcoord), _InputPMA);

                float rate         = IN.texcoord1.x;
                float blendIdx     = IN.texcoord1.y; // 0=Mix,1=Mul,2=Add,3=Sub (part-color blend kind)
                float isNormalized = IN.texcoord1.z; // 1 = effect part / 0 = normal part

                float3 rgb;
                if (isNormalized > 0.5)
                {
                    // Effect: tint by the vertex color (multiply).
                    rgb = tex.rgb * IN.color.rgb;
                }
                else
                {
                    // Normal part: part-color composition (applies the already-mediated values).
                    rgb = ss_partcolor_blend(tex.rgb, IN.color.rgb, rate, blendIdx);
                }

                float alpha = tex.a * IN.color.a;
                float4 col = float4(rgb, alpha);

                // [discard] Drop transparent pixels (disabled by _NotDiscard).
                if (ss_should_discard(col.a, 0.0, _NotDiscard))
                {
                    discard;
                }

                // [output] PMA finalize (correct only in combination with the blend state).
                col = ss_output_color(col, _OutputPMA);

                // TODO(MaterialKey): effect additive is currently passed through via the a=0 trick.
                //   Once the C# side assigns an Add-blend material to effect Add submeshes, remove
                //   this special case and rely solely on the blend state.
                if (isNormalized > 0.5 && abs(blendIdx - 2.0) < 0.5)
                {
                    col.a = 0.0;
                }

                return col;
            }
            ENDHLSL
        }
    }
}

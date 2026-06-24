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
            #pragma vertex ss_world_vert
            #pragma fragment frag
            #include "UnityCG.cginc"

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

            // Shared vertex stage + PartColor coefficient packing. Declared after the material params
            // so the include can reference them (the URP-cbuffer / Built-in-loose split stays here).
            #include "Includes/SpriteStudioVertex.hlsl"

            half4 frag(ss_world_v2f IN) : SV_Target
            {
                float4 tex = ss_world_sample(IN);
                // Branchless PartColor composition (varg resolved per vertex). AddOn shaders replace
                // only the rgb computation here, then call ss_partcolor_blend the same way.
                float3 rgb = ss_partcolor_blend(tex.rgb, IN.color.rgb, IN.partcolorVarg);
                float alpha = tex.a * IN.color.a;
                return ss_world_finalize(rgb, alpha, IN.partcolorVarg.w);
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
            #pragma vertex ss_world_vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float _InputPMA;
            float _OutputPMA;
            float _NotDiscard;

            // Shared vertex stage + PartColor coefficient packing. Declared after the material params
            // so the include can reference them (same vertex stage as the URP SubShader above).
            #include "Includes/SpriteStudioVertex.hlsl"

            half4 frag(ss_world_v2f IN) : SV_Target
            {
                // [input] Sample the texture (and un-premultiply if needed).
                float4 tex = ss_world_sample(IN);
                // Branchless PartColor composition (varg resolved per vertex). Effect parts and the
                // additive a=0 trick are folded into the vertex stage, so the fragment has no branch.
                // AddOn shaders replace only the rgb computation here, then call ss_partcolor_blend.
                float3 rgb = ss_partcolor_blend(tex.rgb, IN.color.rgb, IN.partcolorVarg);
                float alpha = tex.a * IN.color.a;
                // [finalize] discard test, PMA finalize, and the world additive alpha multiplier.
                return ss_world_finalize(rgb, alpha, IN.partcolorVarg.w);
            }
            ENDHLSL
        }
    }
}

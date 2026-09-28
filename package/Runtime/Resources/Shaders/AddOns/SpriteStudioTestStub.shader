Shader "SpriteStudio/AddOn/TestStub"
{
    // Per-part AddOn template / dispatch verification. Identical to SpriteStudio/Default except the
    // fragment inverts the sampled red channel, so it is visually distinct when the per-part path
    // selects it. Shares the world vertex stage (SpriteStudioVertex.hlsl) -> fragment-only authoring.
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1   // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10  // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendOp)]   _BlendOp  ("Blend Op",  Float) = 0   // Add
        _InputPMA   ("Input PMA",   Float) = 0
        _OutputPMA  ("Output PMA",  Float) = 1
        _NotDiscard ("Not Discard", Float) = 0

        _StencilRef       ("Stencil Ref",        Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comp", Float) = 8 // Always
        [Enum(UnityEngine.Rendering.StencilOp)]       _StencilPass ("Stencil Pass", Float) = 0 // Keep
        _StencilReadMask  ("Stencil Read Mask",  Float) = 255
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
    }

    // === URP (Universal Render Pipeline) SubShader ===
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
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex ss_world_vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

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

            #include "../Includes/SpriteStudioVertex.hlsl"

            half4 frag(ss_world_v2f IN) : SV_Target
            {
                float4 tex = ss_world_sample(IN);
                // Stub effect: invert the sampled red channel (visually distinct from Default).
                float3 rgb = float3(1.0 - tex.r, tex.g, tex.b);
                rgb = ss_partcolor_blend(rgb, IN.color.rgb, IN.partcolorVarg);
                float alpha = tex.a * IN.color.a;
                return ss_world_finalize(rgb, alpha, IN.partcolorVarg.w);
            }
            ENDHLSL
        }
        // --- Editor Scene-view selection / picking: reuse SpriteStudio/Default's passes (base-silhouette
        // hit-area; an AddOn's effect-modified silhouette need not be matched exactly). ---
        UsePass "SpriteStudio/Default/SCENESELECTIONPASS"
        UsePass "SpriteStudio/Default/PICKING"
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
            HLSLPROGRAM
            #pragma vertex ss_world_vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float _InputPMA;
            float _OutputPMA;
            float _NotDiscard;

            #include "../Includes/SpriteStudioVertex.hlsl"

            half4 frag(ss_world_v2f IN) : SV_Target
            {
                float4 tex = ss_world_sample(IN);
                // Stub effect: invert the sampled red channel (visually distinct from Default).
                float3 rgb = float3(1.0 - tex.r, tex.g, tex.b);
                rgb = ss_partcolor_blend(rgb, IN.color.rgb, IN.partcolorVarg);
                float alpha = tex.a * IN.color.a;
                return ss_world_finalize(rgb, alpha, IN.partcolorVarg.w);
            }
            ENDHLSL
        }
        // --- Editor Scene-view selection / picking: reuse SpriteStudio/Default's passes (base-silhouette
        // hit-area; an AddOn's effect-modified silhouette need not be matched exactly). ---
        UsePass "SpriteStudio/Default/SCENESELECTIONPASS"
        UsePass "SpriteStudio/Default/PICKING"
    }
}

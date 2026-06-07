Shader "SpriteStudio/Canvas"
{
    // Canvas (uGUI) variant of SpriteStudio/Default and the template for custom Canvas shaders.
    // To author one: copy this shell, keep the Properties + Stencil/ColorMask/ZTest render state
    // verbatim (the uGUI clip contract; ShaderLab cannot #include render state), then edit only the
    // fragment between ss_canvas_sample and ss_canvas_finalize (see SpriteStudioCanvasUI.hlsl).
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}

        // Single fixed PMA blend (One, OneMinusSrcAlpha); MIX vs ADD chosen per-vertex via the a=0
        // trick (TEXCOORD1.w), all other ssab blends collapse to MIX.
        _InputPMA   ("Input PMA",   Float) = 0
        _OutputPMA  ("Output PMA",  Float) = 1
        _NotDiscard ("Not Discard", Float) = 0

        // --- uGUI stencil scaffolding (read-only; driven by StencilMaterial.Add when this graphic
        // is nested under a uGUI Mask). Defaults are a no-op (Comp Always / Op Keep / full masks). ---
        _Stencil          ("Stencil ID",        Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comparison", Float) = 8 // Always
        [Enum(UnityEngine.Rendering.StencilOp)]       _StencilOp   ("Stencil Operation",  Float) = 0 // Keep
        _StencilReadMask  ("Stencil Read Mask",  Float) = 255
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _ColorMask        ("Color Mask",         Float) = 15
    }

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

        // uGUI stencil test (read-only). Set by StencilMaterial.Add for a Mask parent; a no-op
        // (Comp Always / Op Keep) otherwise so an unmasked graphic is unaffected.
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
        ZTest [unity_GUIZTestMode] // respects the Canvas render mode (Overlay ignores depth)
        ColorMask [_ColorMask]
        // Single fixed PMA blend; MIX vs ADD is handled per-vertex via the a=0 trick (TEXCOORD1.w).
        Blend One OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #include "Includes/SpriteStudioCanvasUI.hlsl"
            #pragma vertex ss_canvas_vert
            #pragma fragment frag

            half4 frag(ss_canvas_v2f IN) : SV_Target
            {
                float rate, blendIdx, isNormalized;
                float4 tex = ss_canvas_sample(IN, rate, blendIdx, isNormalized);
                // --- Custom shaders replace only this colour composition ---
                float3 rgb = ss_canvas_default_rgb(tex, IN, rate, blendIdx, isNormalized);
                float alpha = tex.a * IN.color.a;
                // -----------------------------------------------------------
                // TEXCOORD1.w = per-vertex isAdditive (ADD -> a=0 PMA trick); others collapse to MIX.
                return ss_canvas_finalize(rgb, alpha, IN.texcoord1.w);
            }
            ENDHLSL
        }
    }
}

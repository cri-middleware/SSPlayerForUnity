Shader "SpriteStudio/Canvas"
{
    // Canvas (uGUI) variant of SpriteStudio/Default, and the canonical template for custom
    // SpriteStudio Canvas shaders. The SpriteStudio colour core (part-color composition, PMA,
    // discard) and the property-driven blend state are identical to the world shader, so per-blend
    // multi-material resolution works the same way. The difference is the stencil scaffolding:
    // instead of the world path's SpriteStudio *internal* mask (which contends with uGUI's own
    // Mask/RectMask2D), this shader carries the uGUI-standard stencil properties so a parent uGUI
    // Mask can drive a READ-ONLY stencil test via StencilMaterial.Add (MaskableGraphic). SpriteStudio
    // internal masks are intentionally dropped on the Canvas path (project_unity_canvas_plan); use
    // the world (MeshRenderer) player for full internal masking. RectMask2D needs no shader support —
    // MaskableGraphic clips it through CanvasRenderer.EnableRectClipping (hardware rect clipping).
    //
    // To author a custom Canvas shader: copy this shell, keep the Properties + Stencil/ColorMask/ZTest
    // render state verbatim (the uGUI clip contract; ShaderLab cannot #include render state), then edit
    // only the fragment between ss_canvas_sample and ss_canvas_finalize (see SpriteStudioCanvasUI.hlsl).
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}

        // --- Blend state (set by the C# MaterialKey per the B-table in project_blend_mapping_truth) ---
        // Defaults are Mix + PMA output. The MaterialKey overrides these per ssab BlendType.
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1   // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10  // OneMinusSrcAlpha
        [Enum(UnityEngine.Rendering.BlendOp)]   _BlendOp  ("Blend Op",  Float) = 0   // Add
        // PMA / discard are uniform branches (not keywords), so variants collapse to one.
        _InputPMA   ("Input PMA",   Float) = 0   // 1 = texture is premultiplied -> un-premultiply
        _OutputPMA  ("Output PMA",  Float) = 1   // 1 = emit premultiplied alpha
        _NotDiscard ("Not Discard", Float) = 0   // 1 = keep transparent pixels (Mul/Invert)

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
        Blend [_SrcBlend] [_DstBlend]
        BlendOp [_BlendOp]

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
                return ss_canvas_finalize(rgb, alpha, blendIdx, isNormalized);
            }
            ENDHLSL
        }
    }
}

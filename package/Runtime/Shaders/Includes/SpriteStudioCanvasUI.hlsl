// Canvas (uGUI) scaffolding for SpriteStudio shaders: the reusable vertex stage, texture sampling,
// and colour finalize, so a custom Canvas shader reduces to the fixed ShaderLab stencil block +
// this include + a short fragment. Pixel mediation lives in SpriteStudioCore.hlsl.
//
// Contract for a Canvas-compatible custom shader (so a parent uGUI Mask can clip it read-only via
// StencilMaterial.Add / MaskableGraphic): declare these properties + render state in ShaderLab —
//   _Stencil/_StencilComp/_StencilOp/_StencilReadMask/_StencilWriteMask/_ColorMask,
//   Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp]
//             ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] },
//   ColorMask [_ColorMask], ZTest [unity_GUIZTestMode].
// RectMask2D needs no shader support — MaskableGraphic clips it via CanvasRenderer.EnableRectClipping.

#ifndef SPRITESTUDIO_CANVAS_UI_INCLUDED
#define SPRITESTUDIO_CANVAS_UI_INCLUDED

#include "UnityCG.cginc"
#include "SpriteStudioCore.hlsl"

// Vertex input/output. Appdata TEXCOORD1 = [rate, blend_idx, isNormalized(effect), isAdditive],
// matching the vertex layout the geometry builder emits (shared with SpriteStudio/Default). The v2f
// carries the per-vertex-resolved PartColor `varg` instead, so the fragment stays branchless.
struct ss_canvas_appdata
{
    float4 vertex   : POSITION;
    float4 color    : COLOR;
    float2 texcoord : TEXCOORD0;
    float4 texcoord1: TEXCOORD1;
};

struct ss_canvas_v2f
{
    float4 vertex        : SV_POSITION;
    half4  color         : COLOR;
    float2 texcoord      : TEXCOORD0;
    float4 partcolorVarg : TEXCOORD1; // xyz = PartColor varg (branchless), w = output alpha multiplier
};

// Standard SpriteStudio Canvas uniforms. A custom shader gets these by including this file; it only
// adds its own extra properties on top.
sampler2D _MainTex;
float4 _MainTex_TexelSize;
float _InputPMA;
float _OutputPMA;
float _NotDiscard;

// Standard vertex stage: clip-space transform, pixel/normalized UV resolve, SS top-left -> Unity
// bottom-left V flip, and the PartColor coefficient packing (so the fragment needs no per-blend
// branch). Reference it with `#pragma vertex ss_canvas_vert`.
ss_canvas_v2f ss_canvas_vert(ss_canvas_appdata IN)
{
    ss_canvas_v2f OUT;
    OUT.vertex = UnityObjectToClipPos(IN.vertex);
    float rate         = IN.texcoord1.x;
    float blendIdx     = IN.texcoord1.y;
    float isNormalized = IN.texcoord1.z;
    float isAdditive   = IN.texcoord1.w;
    // Normal parts carry pixel coordinates; effect parts carry normalized coordinates (z == 1).
    float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
    OUT.texcoord = IN.texcoord * texelSizeMultiplier;
    OUT.texcoord.y = 1.0 - OUT.texcoord.y;
    OUT.color = IN.color;
    // PartColor: resolve the branchless varg in the vertex stage (effect tint folded in).
    float3 varg = ss_partcolor_varg(rate, blendIdx, isNormalized);
    // Canvas single-material a=0 trick: ANY additive (ADD) part collapses to alpha 0 so the fixed PMA
    // blend (One, OneMinusSrcAlpha) becomes additive; all other blends keep their alpha (MIX).
    OUT.partcolorVarg = float4(varg, 1.0 - isAdditive);
    return OUT;
}

// Sample the texture (un-premultiplying when requested). The SS per-vertex PartColor channels are
// resolved into IN.partcolorVarg in the vertex stage; a custom shader reads `tex` here.
float4 ss_canvas_sample(ss_canvas_v2f IN)
{
    return ss_input_mediate(ss_tex2D(_MainTex, IN.texcoord), _InputPMA);
}

// Default SpriteStudio RGB composition (branchless; effect tint is already folded into varg). A custom
// shader can ignore this and compute its own rgb, then call
// `ss_partcolor_blend(rgb, IN.color.rgb, IN.partcolorVarg)` the same way.
float3 ss_canvas_default_rgb(float4 tex, ss_canvas_v2f IN)
{
    return ss_partcolor_blend(tex.rgb, IN.color.rgb, IN.partcolorVarg);
}

// Finalize a composed (rgb, alpha) into the output colour: discard test, PMA finalize, and the
// per-vertex additive alpha multiplier. The Canvas path uses a single fixed PMA blend (One,
// OneMinusSrcAlpha); ADD parts carry alphaMul 0 so the blend becomes additive (src + dst), all other
// blends carry 1 (MIX). alphaMul is IN.partcolorVarg.w, resolved in the vertex stage.
float4 ss_canvas_finalize(float3 rgb, float alpha, float alphaMul)
{
    float4 col = float4(rgb, alpha);
    if (ss_should_discard(col.a, 0.0, _NotDiscard))
    {
        discard;
    }
    col = ss_output_color(col, _OutputPMA);
    col.a *= alphaMul;
    return col;
}

#endif // SPRITESTUDIO_CANVAS_UI_INCLUDED

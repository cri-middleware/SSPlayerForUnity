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
// and, for a parent RectMask2D, `#pragma multi_compile_local _ UNITY_UI_CLIP_RECT` in the pass:
// CanvasRenderer.EnableRectClipping only hands the shader _ClipRect and that keyword, and
// ss_canvas_finalize does the clipping with them.

#ifndef SPRITESTUDIO_CANVAS_UI_INCLUDED
#define SPRITESTUDIO_CANVAS_UI_INCLUDED

#include "UnityCG.cginc"
#include "UnityUI.cginc"
#include "SpriteStudioCore.hlsl"

// Vertex input/output. Appdata TEXCOORD1 = [rate, blend_idx, isNormalized(effect), isAdditive], as the
// geometry builder emits it (shared with SpriteStudio/Default). The part colour, which SpriteStudio/
// Default reads from COLOR, arrives in TEXCOORD2 here: on a canvas COLOR is uGUI's channel -- Graphic.color
// times the part's own alpha, which the canvas multiplies by the CanvasRenderer colour and the inherited
// CanvasGroup alpha. The v2f carries the per-vertex-resolved PartColor `varg`, so the fragment stays
// branchless.
struct ss_canvas_appdata
{
    float4 vertex   : POSITION;
    float4 color    : COLOR;
    float2 texcoord : TEXCOORD0;
    float4 texcoord1: TEXCOORD1;
    float3 texcoord2: TEXCOORD2;
};

struct ss_canvas_v2f
{
    float4 vertex        : SV_POSITION;
    half4  color         : COLOR;     // the uGUI tint; its alpha carries the part's alpha too
    float2 texcoord      : TEXCOORD0;
    float4 partcolorVarg : TEXCOORD1; // xyz = PartColor varg (branchless), w = output alpha multiplier
    half3  partColor     : TEXCOORD2;
    float4 mask          : TEXCOORD3; // RectMask2D: xy = position against _ClipRect, zw = edge falloff
};

// Standard SpriteStudio Canvas uniforms. A custom shader gets these by including this file; it only
// adds its own extra properties on top.
sampler2D _MainTex;
float4 _MainTex_TexelSize;
float _InputPMA;
float _OutputPMA;
float _NotDiscard;
// Set by the canvas when Canvas.vertexColorAlwaysGammaSpace is on: COLOR then arrives in gamma in a Linear
// project, where it otherwise arrives linearised.
float _UIVertexColorAlwaysGammaSpace;
// Set by the canvas on a graphic a RectMask2D clips: the rect in canvas space, and its softness.
float4 _ClipRect;
float _UIMaskSoftnessX;
float _UIMaskSoftnessY;

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
    // The tint multiplies the linear output in a Linear project (ss_canvas_finalize), as UI/Default's does.
    if (_UIVertexColorAlwaysGammaSpace > 0.5 && !IsGammaSpace())
    {
        OUT.color.rgb = UIGammaToLinear(OUT.color.rgb);
    }
    OUT.partColor = IN.texcoord2;
#ifdef UNITY_UI_CLIP_RECT
    // As UI/Default computes it: the vertex against the rect in canvas space, and a falloff one pixel
    // wide (plus the softness) at the rect's edges.
    float2 pixelSize = OUT.vertex.w;
    pixelSize /= float2(1, 1) * abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
    float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
    OUT.mask = float4(IN.vertex.xy * 2 - clampedRect.xy - clampedRect.zw,
                      0.25 / (0.25 * half2(_UIMaskSoftnessX, _UIMaskSoftnessY) + abs(pixelSize.xy)));
#else
    OUT.mask = float4(0, 0, 0, 0);
#endif
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
// `ss_partcolor_blend(rgb, IN.partColor, IN.partcolorVarg)` the same way.
float3 ss_canvas_default_rgb(float4 tex, ss_canvas_v2f IN)
{
    return ss_partcolor_blend(tex.rgb, IN.partColor, IN.partcolorVarg);
}

// Finalize a composed (rgb, alpha) into the output colour: discard test, PMA finalize, the uGUI tint,
// and the per-vertex additive alpha multiplier. The tint (IN.color.rgb) applies last, after the part
// colour and any custom composition, the way UI/Default multiplies its vertex colour into the texel;
// its alpha is already in `alpha`. The Canvas path uses a single fixed PMA blend (One,
// OneMinusSrcAlpha); ADD parts carry alphaMul 0 so the blend becomes additive (src + dst), all other
// blends carry 1 (MIX). alphaMul is IN.partcolorVarg.w, resolved in the vertex stage.
float4 ss_canvas_finalize(float3 rgb, float alpha, ss_canvas_v2f IN)
{
#ifdef UNITY_UI_CLIP_RECT
    // RectMask2D fades alpha out past the rect, before the premultiply so ADD parts clip too.
    half2 m = saturate((_ClipRect.zw - _ClipRect.xy - abs(IN.mask.xy)) * IN.mask.zw);
    alpha *= m.x * m.y;
#endif
    float4 col = float4(rgb, alpha);
    if (ss_should_discard(col.a, 0.0, _NotDiscard))
    {
        discard;
    }
    col = ss_output_color(col, _OutputPMA);
    col.rgb *= IN.color.rgb;
    col.a *= IN.partcolorVarg.w;
    return col;
}

#endif // SPRITESTUDIO_CANVAS_UI_INCLUDED

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

// Vertex input/output. TEXCOORD1 = [rate, blend_idx, isNormalized(effect), reserved], matching the
// vertex layout the geometry builder emits (shared with SpriteStudio/Default).
struct ss_canvas_appdata
{
    float4 vertex   : POSITION;
    float4 color    : COLOR;
    float2 texcoord : TEXCOORD0;
    float4 texcoord1: TEXCOORD1;
};

struct ss_canvas_v2f
{
    float4 vertex   : SV_POSITION;
    half4  color    : COLOR;
    float2 texcoord : TEXCOORD0;
    float4 texcoord1: TEXCOORD1;
};

// Standard SpriteStudio Canvas uniforms. A custom shader gets these by including this file; it only
// adds its own extra properties on top.
sampler2D _MainTex;
float4 _MainTex_TexelSize;
float _InputPMA;
float _OutputPMA;
float _NotDiscard;

// Standard vertex stage: clip-space transform, pixel/normalized UV resolve, SS top-left -> Unity
// bottom-left V flip. Reference it with `#pragma vertex ss_canvas_vert`.
ss_canvas_v2f ss_canvas_vert(ss_canvas_appdata IN)
{
    ss_canvas_v2f OUT;
    OUT.vertex = UnityObjectToClipPos(IN.vertex);
    // Normal parts carry pixel coordinates; effect parts carry normalized coordinates (z == 1).
    float isNormalized = IN.texcoord1.z;
    float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
    OUT.texcoord = IN.texcoord * texelSizeMultiplier;
    OUT.texcoord.y = 1.0 - OUT.texcoord.y;
    OUT.color = IN.color;
    OUT.texcoord1 = IN.texcoord1;
    return OUT;
}

// Sample the texture (un-premultiplying when requested) and unpack the SS per-vertex channels.
float4 ss_canvas_sample(ss_canvas_v2f IN, out float rate, out float blendIdx, out float isNormalized)
{
    rate         = IN.texcoord1.x;
    blendIdx     = IN.texcoord1.y; // 0=Mix,1=Mul,2=Add,3=Sub (part-color blend kind)
    isNormalized = IN.texcoord1.z; // 1 = effect part / 0 = normal part
    return ss_input_mediate(tex2D(_MainTex, IN.texcoord), _InputPMA);
}

// Default SpriteStudio RGB composition: effect parts tint by vertex colour; normal parts apply the
// already-mediated part-color. A custom shader can ignore this and compute its own rgb instead.
float3 ss_canvas_default_rgb(float4 tex, ss_canvas_v2f IN, float rate, float blendIdx, float isNormalized)
{
    if (isNormalized > 0.5)
        return tex.rgb * IN.color.rgb;
    return ss_partcolor_blend(tex.rgb, IN.color.rgb, rate, blendIdx);
}

// Finalize a composed (rgb, alpha) into the output colour: discard test, PMA finalize, and the
// additive a=0 pass-through. The Canvas path uses a single fixed PMA blend (One, OneMinusSrcAlpha);
// ADD parts (and effect-additive) emit alpha 0 so the blend becomes additive (src + dst). All other
// blends collapse to MIX. isAdditive is the per-vertex flag the mesh job packs into TEXCOORD1.w.
float4 ss_canvas_finalize(float3 rgb, float alpha, float isAdditive)
{
    float4 col = float4(rgb, alpha);
    if (ss_should_discard(col.a, 0.0, _NotDiscard))
    {
        discard;
    }
    col = ss_output_color(col, _OutputPMA);
    if (isAdditive > 0.5)
    {
        col.a = 0.0;
    }
    return col;
}

#endif // SPRITESTUDIO_CANVAS_UI_INCLUDED

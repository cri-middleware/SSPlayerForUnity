// Shared world (MeshRenderer) vertex stage for SpriteStudio shaders: the standard vertex function,
// texture sampling, and colour finalize, so SpriteStudio/Default and per-part AddOn shaders share one
// vertex stage and differ only in the fragment (the colour effect). Colour math + the PartColor
// coefficient table live in SpriteStudioCore.hlsl; the Canvas (uGUI) path has the parallel scaffolding
// in SpriteStudioCanvasUI.hlsl (same structure, engine-specific clip/stencil contract).
//
// Each SubShader must declare the per-pipeline material params BEFORE including this file (Built-in:
// loose floats; URP: inside `cbuffer UnityPerMaterial` for SRP Batcher) — kept out of the include so
// the URP-cbuffer / Built-in-loose split stays in the SubShader:
//   sampler2D _MainTex;  float4 _MainTex_TexelSize;  float _InputPMA;  float _OutputPMA;  float _NotDiscard;

#ifndef SPRITESTUDIO_VERTEX_INCLUDED
#define SPRITESTUDIO_VERTEX_INCLUDED

#include "SpriteStudioCore.hlsl"

struct ss_world_appdata
{
    float4 vertex   : POSITION;
    float4 color    : COLOR;
    float2 texcoord : TEXCOORD0;
    float4 texcoord1: TEXCOORD1; // [rate, blend_idx, isNormalized(effect), isAdditive]
};

struct ss_world_v2f
{
    float4 vertex        : SV_POSITION;
    half4  color         : COLOR;
    float2 texcoord      : TEXCOORD0;
    float4 partcolorVarg : TEXCOORD1; // xyz = PartColor varg (branchless), w = output alpha multiplier
};

// Standard vertex stage: clip transform, pixel/normalized UV resolve, SS top-left -> Unity bottom-left
// V flip, and the PartColor coefficient packing (so the fragment needs no per-blend branch).
// Reference it with `#pragma vertex ss_world_vert`.
ss_world_v2f ss_world_vert(ss_world_appdata IN)
{
    ss_world_v2f OUT;
    OUT.vertex = UnityObjectToClipPos(IN.vertex);

    float rate         = IN.texcoord1.x;
    float blendIdx     = IN.texcoord1.y;
    float isNormalized = IN.texcoord1.z;

    // Normal parts carry pixel coordinates; effect parts carry normalized coordinates (z == 1).
    float2 texelSizeMultiplier = lerp(_MainTex_TexelSize.xy, float2(1.0, 1.0), isNormalized);
    OUT.texcoord = IN.texcoord * texelSizeMultiplier;
    // SS uses a top-left UV origin, Unity uses bottom-left -> flip V (once, here, not on the CPU).
    OUT.texcoord.y = 1.0 - OUT.texcoord.y;

    OUT.color = IN.color;

    // PartColor: resolve the branchless varg in the vertex stage.
    float3 varg = ss_partcolor_varg(rate, blendIdx, isNormalized);
    // World additive a=0 trick applies ONLY to effect-Add parts; normal/shape/mesh Add parts use the
    // real GPU blend state (per-blend material) and keep their alpha. For an effect part blendIdx>=2
    // means Add (effect blend is Mix(0) or Add(2)).
    float effectAdd = step(0.5, isNormalized) * step(1.5, blendIdx);
    OUT.partcolorVarg = float4(varg, 1.0 - effectAdd);
    return OUT;
}

// Sample the main texture (un-premultiplying when requested; RP-agnostic mediation in Core).
float4 ss_world_sample(ss_world_v2f IN)
{
    return ss_input_mediate(ss_tex2D(_MainTex, IN.texcoord), _InputPMA);
}

// Finalize a composed (rgb, alpha): discard test, PMA finalize, then the world additive alpha
// multiplier (1 for everything except effect-Add, which collapses to a=0).
half4 ss_world_finalize(float3 rgb, float alpha, float alphaMul)
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

#endif // SPRITESTUDIO_VERTEX_INCLUDED

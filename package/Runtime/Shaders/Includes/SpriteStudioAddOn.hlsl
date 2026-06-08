// Shared AddOn (per-part custom shader) effect library for SpriteStudio. The actual colour effects
// (sepia, outline, ...) live here as target-agnostic functions so both the world (MeshRenderer) and
// the future Canvas (uGUI) AddOn shaders call the SAME effect, differing only in the engine-specific
// vertex / sampling scaffolding (SpriteStudioVertex.hlsl / SpriteStudioCanvasUI.hlsl).
//
// Authoring a world AddOn shader: copy AddOns/SpriteStudioTestStub.shader, keep the Properties +
// Blend/Stencil render state, declare any per-part params it uses (_SsParam0.._SsParam7 / _SsCellRect)
// inside the UnityPerMaterial cbuffer (URP) or as loose floats (Built-in) for SRP Batcher, then in the
// fragment compute rgb via an effect function here and finish with ss_partcolor_blend + the matching
// ss_world_finalize (mirroring SpriteStudio/Default).
//
// PartColor policy: AddOns mirror SSPlayerForGodot exactly. Most route the effect result through
// ss_partcolor_blend; ss-blur is the documented multiply-only exception. Do NOT "fix" PartColor here
// (a coordinated tool-side revision may change it across all platforms later).

#ifndef SPRITESTUDIO_ADDON_INCLUDED
#define SPRITESTUDIO_ADDON_INCLUDED

#include "SpriteStudioCore.hlsl"

// ss-sepia: ported from the SS6 SDK / SSPlayerForGodot (ss_sepia.fs). `power` (ss_param0) is signed:
//   power > 0 : warm sepia tint over the luminance grayscale; abs(power) is the blend amount.
//   power < 0 : pure grayscale (no warming).
//   power == 0: pass-through.
float3 ss_addon_sepia(float3 rgb, float power)
{
    // ITU-R BT.601 luminance weights (SS6 reference).
    float gray = dot(rgb, float3(0.298912, 0.586611, 0.114478));
    float3 grayRgb = float3(gray, gray, gray);
    float3 sepiaRgb = grayRgb * float3(1.07, 0.74, 0.43);
    // step(power, 0.0) == 1 when power <= 0 -> grayscale; otherwise the sepia tint.
    float3 toned = lerp(sepiaRgb, grayRgb, step(power, 0.0));
    return lerp(rgb, toned, abs(power));
}

#endif // SPRITESTUDIO_ADDON_INCLUDED

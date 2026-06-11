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

// ---- ss-hsb: hue / saturation / brightness shift (ss_param0/1/2, signed) ----
// RGB<->HSB helpers ported verbatim from the SS6 SDK (ss-hsb.fs); alpha passes through unchanged so
// these operate on rgb only (GLSL mix/fract -> HLSL lerp/frac).
float3 ss_addon_rgb_to_hsb(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 ss_addon_hsb_to_rgb(float3 hsb)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(hsb.xxx + K.xyz) * 6.0 - K.www);
    return hsb.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), hsb.y);
}

float3 ss_addon_hsb(float3 rgb, float hue, float saturation, float brightness)
{
    float3 hsb = ss_addon_rgb_to_hsb(rgb);
    hsb.x += hue;
    hsb.y = clamp(hsb.y + saturation, 0.0, 1.0);
    hsb.z = clamp(hsb.z + brightness, 0.0, 1.0);
    if (hsb.x < 0.0) hsb.x += 1.0;
    if (hsb.x > 1.0) hsb.x -= 1.0;
    return ss_addon_hsb_to_rgb(hsb);
}

// ---- ss-step: brightness-driven posterise (ss_param0=threshold signed, 1=stages, 2=colour mix) ----
// Ported from the SS6 SDK (ss-step.fs).
float3 ss_addon_step(float3 rgb, float threshold, float stage, float colorMix)
{
    float d = 1.0 + 255.0 * abs(stage);
    float e = 1.0e-10;
    float t = abs(threshold);
    float b = abs(step(threshold, 0.0) - max(rgb.r, max(rgb.g, rgb.b)));
    float r = clamp(floor((b - t) / (t + e) * 256.0 / d) * d / 256.0, 0.0, 1.0);
    float c = step(t, b) * r;
    return lerp(float3(c, c, c), rgb * c, colorMix);
}

// ---- ss-bmask: brightness-threshold mask (ss_param0=brightness signed) ----
// Ported from the SS6 SDK (ss-bmask.fs). Returns true to KEEP the pixel; the shader discards otherwise.
bool ss_addon_bmask_keep(float3 rgb, float brightness)
{
    float b = abs(step(brightness, 0.0) - max(rgb.r, max(rgb.g, rgb.b)));
    return b > abs(brightness);
}

// =========================================================================
// Neighbour / UV sampling AddOns. These read the texture themselves; `texelSize` is _MainTex_TexelSize.xy.
// They operate on straight-alpha SS textures, so no inverse-PMA mediation is applied (a no-op there).
//
// V-FLIP: the vertex stage flips V (SS top-left UV -> Unity bottom-left) so the texture renders the right
// way up. AddOns that compute directions / positions in UV space (wave / move / pix / noise / scatter /
// circle / spot) must do that math in SS (top-left) space to match the SS6 / ForGodot reference, then
// flip back when sampling. ss_addon_uv un-flips the incoming frag UV; ss_addon_sample samples at an
// SS-space UV. This is our explicit vertex flip (not a platform GL/DX difference) so no #if is needed.
// Symmetric AddOns sample at the frag UV directly (no flip): blur via the colour primitive ss_tex2D,
// outline via raw tex2D (it reads alpha only, which is never colorspace-converted).
// =========================================================================

float2 ss_addon_uv(float2 fragUv) { return float2(fragUv.x, 1.0 - fragUv.y); }
float4 ss_addon_sample(sampler2D tex, float2 ssUv) { return ss_tex2D(tex, float2(ssUv.x, 1.0 - ssUv.y)); }
// Explicit-LOD (mip 0) variant of ss_addon_sample. Required wherever the sample sits inside a
// varying-iteration loop: tex2D's implicit gradient forces the compiler to unroll such a loop
// (FXC X3570), which on Apple/AMD/Vulkan compilers turns into a pathologically slow / hanging shader
// compile. tex2Dlod needs no derivatives, so [loop] is honoured and it compiles fast on every backend.
// SS sprite atlases are sampled at mip 0 anyway, so this is visually identical.
float4 ss_addon_sample_lod(sampler2D tex, float2 ssUv) { return ss_tex2Dlod(tex, float2(ssUv.x, 1.0 - ssUv.y)); }

// ---- ss-outline: 4-neighbour alpha-edge outline (ss_param0=threshold signed) ----
// Returns the outline intensity v (0..1); the shader feeds it as both rgb and alpha then discards v<=0.
float ss_addon_outline(sampler2D tex, float2 uv, float2 texelSize, float threshold)
{
    float ratio = abs(threshold);
    float e = 1.0e-5;
    float lo = 0.0;
    lo += step(tex2D(tex, uv + float2(-texelSize.x, 0.0)).a, ratio);
    lo += step(tex2D(tex, uv + float2(+texelSize.x, 0.0)).a, ratio);
    lo += step(tex2D(tex, uv + float2(0.0, -texelSize.y)).a, ratio);
    lo += step(tex2D(tex, uv + float2(0.0, +texelSize.y)).a, ratio);
    float hi = step(ratio + e, tex2D(tex, uv).a);
    return min(hi * lo, 1.0);
}

// ---- ss-blur: 9-tap box blur (ss_param0=focus). Returns the averaged texel. ----
// NOTE: ss-blur is the documented multiply-only PartColor exception (see SS6/ForGodot); the shader
// multiplies this by the vertex colour instead of calling ss_partcolor_blend.
float4 ss_addon_blur(sampler2D tex, float2 uv, float2 texelSize, float focus)
{
    float2 d = float2(focus * texelSize.x * 16.0, focus * texelSize.y * 16.0);
    float4 sum = ss_tex2D(tex, uv);
    sum += ss_tex2D(tex, float2(uv.x + d.x, uv.y + d.y));
    sum += ss_tex2D(tex, float2(uv.x + d.x, uv.y));
    sum += ss_tex2D(tex, float2(uv.x,       uv.y + d.y));
    sum += ss_tex2D(tex, float2(uv.x - d.x, uv.y - d.y));
    sum += ss_tex2D(tex, float2(uv.x + d.x, uv.y - d.y));
    sum += ss_tex2D(tex, float2(uv.x - d.x, uv.y + d.y));
    sum += ss_tex2D(tex, float2(uv.x - d.x, uv.y));
    sum += ss_tex2D(tex, float2(uv.x,       uv.y - d.y));
    return sum / 9.0;
}

// ---- ss-pix: UV-quantise pixelation (ss_param0=power). Returns the quantised texel. ----
float4 ss_addon_pix(sampler2D tex, float2 uv, float2 texelSize, float power)
{
    float2 ssUv = ss_addon_uv(uv);
    float v = 1.0 + 96.0 * power;
    float2 stepUv = float2(v * texelSize.x, v * texelSize.y);
    float2 coord = floor(ssUv / stepUv) * stepUv;
    return ss_addon_sample(tex, coord);
}

// ---- ss-noise: per-texel pseudo-random noise (ss_param0=power, 1=colour, 2=phase) ----
float ss_addon_noise_rand(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

float4 ss_addon_noise(sampler2D tex, float2 uv, float2 texelSize, float power, float colorAmt, float phase)
{
    float2 ssUv = ss_addon_uv(uv);
    float2 u = floor(ssUv / texelSize) * texelSize;
    float4 pixel = ss_addon_sample(tex, ssUv);
    float2 t = u + float2(phase, phase);
    float3 c;
    c.r = ss_addon_noise_rand(t);
    c.g = lerp(c.r, ss_addon_noise_rand(t * c.r), colorAmt);
    c.b = lerp(c.g, ss_addon_noise_rand(t * c.g), colorAmt);
    pixel.rgb = clamp(lerp(pixel.rgb, c, power), 0.0, 1.0);
    return pixel;
}

// ---- ss-wave: sinusoidal horizontal warp (ss_param0=width, 1=height, 2=phase) ----
// PI -> UNITY_PI (UnityCG.cginc, included by the shader before this file).
float4 ss_addon_wave(sampler2D tex, float2 uv, float2 texelSize, float width, float height, float phase)
{
    float2 ssUv = ss_addon_uv(uv);
    float l = 384.0 * height * 0.5;
    float s = l * sin(ssUv.y * 384.0 * width * 0.5 + UNITY_PI * 2.0 * phase) * texelSize.x;
    return ss_addon_sample(tex, float2(ssUv.x + s, ssUv.y));
}

// ---- ss-move: directional motion-blur smear (ss_param0=distance signed, 1=direction, 2=power) ----
// Accumulates back-traced samples along (sin,cos)(direction*PI). The loop bound is dynamic but capped
// at 128 (matching ForGodot). Sampling here MUST use ss_addon_sample_lod (explicit mip 0), NOT tex2D:
// an implicit-gradient sample inside this varying-iteration loop forces the HLSL compiler to unroll it
// to 128x (FXC X3570) despite [loop], which on Apple/AMD/Vulkan compilers becomes a pathologically slow
// or hanging shader compile (GeForce/D3D tolerates it, so the runaway only shows on those backends).
float4 ss_addon_move(sampler2D tex, float2 uv, float2 texelSize, float dist, float direction, float power)
{
    float2 ssUv = ss_addon_uv(uv);
    int count = (int)floor(abs(dist) * 96.0);
    if (count > 128) count = 128;
    float2 vel = float2(sin(direction * UNITY_PI) * texelSize.x, cos(direction * UNITY_PI) * texelSize.y) * sign(dist);
    float2 coord = ssUv + vel * (float)count;
    coord -= vel;
    float4 pixel = ss_addon_sample_lod(tex, coord);
    [loop]
    for (int i = 1; i < count; i++)
    {
        coord -= vel;
        pixel = lerp(ss_addon_sample_lod(tex, coord), pixel, 0.96 * abs(power));
    }
    return pixel;
}

// ---- ss-scatter: per-cell directional UV scatter (ss_param0=power, 1=ratio, 2=phase) ----
float4 ss_addon_scatter(sampler2D tex, float2 uv, float2 texelSize, float power, float ratio, float phase)
{
    float2 ssUv = ss_addon_uv(uv);
    float2 u = floor(ssUv / texelSize) * texelSize;
    float2 t = u + float2(phase, phase);
    float r = ss_addon_noise_rand(t) * UNITY_PI * 2.0 - UNITY_PI;
    float r2 = step(ss_addon_noise_rand(t * r), ratio);
    float2 v = float2(sin(r), cos(r)) * texelSize * 96.0 * power * r2;
    return ss_addon_sample(tex, ssUv + v);
}

// ---- Cell-rect AddOns (ss-circle / ss-spot) ----
// cellRectPixel = (lt.u, lt.v, rb.u, rb.v) in PIXEL UVs (from LocalUvs). Convert to this shader's frag
// UV space the same way the vertex stage builds IN.texcoord: u * texel, 1 - v * texel (SS top-left ->
// Unity bottom-left V flip). cellRectPixel == 0 (Shape/Mesh parts) collapses the rect to the origin so
// the effect degenerates (ss-circle discards everything; ss-spot yields no light).

// ss-circle: unwrap the cell rectangle into a disc. ss_param0 = phase, ss_param1 = direction.
// Returns the sampled texel; `keep` is false outside the disc (the shader discards then).
float4 ss_addon_circle(sampler2D tex, float2 uv, float2 texelSize, float4 cellRectPixel, float phase, float direction, out bool keep)
{
    float e = 1.0e-10;
    float2 ssUv = ss_addon_uv(uv);
    float2 uv1 = texelSize + float2(e, e);
    // Cell rect in SS top-left UV (pixel * texel; no V flip — the math is done in SS space).
    float2 lt_uv = cellRectPixel.xy * texelSize;
    float2 rb_uv = cellRectPixel.zw * texelSize;
    float2 center_uv = (lt_uv + rb_uv) * 0.5;

    float2 tx = ssUv / uv1;
    float2 c  = center_uv / uv1;
    float2 lt = lt_uv / uv1;
    float2 rb = rb_uv / uv1;
    float2 d  = rb - lt;
    float2 v  = tx - c;
    float r = min(abs(rb.x - c.x), abs(rb.y - c.y)) + e;
    float dis = length(v);
    keep = (dis <= r);

    float2 nv = (dis > e) ? (v / dis) : float2(1.0, 0.0);
    float uu = 1.0 - dis / r;
    float vv = (atan2(nv.y, nv.x) / UNITY_PI + 1.0) * 0.5 + phase;
    if (vv < 0.0) vv += 1.0;
    if (vv > 1.0) vv -= 1.0;
    if (direction <= 0.0) vv = 1.0 - vv;

    float2 st = d * float2(uu, vv) * uv1;
    return ss_addon_sample(tex, lt_uv + st);
}

// ss-spot: radial spotlight gradient centred at the cell rect. ss_param0 = power, 1 = decay, 2 = colour.
float4 ss_addon_spot(sampler2D tex, float2 uv, float2 texelSize, float4 cellRectPixel, float power, float decay, float colorAmt)
{
    float e = 1.0e-10;
    float p = abs(power);
    float dAbs = abs(decay);
    float2 ssUv = ss_addon_uv(uv);
    float2 uv1 = texelSize + float2(e, e);
    float2 lt_uv = cellRectPixel.xy * texelSize;
    float2 rb_uv = cellRectPixel.zw * texelSize;
    float2 center_uv = (lt_uv + rb_uv) * 0.5;

    float2 c = center_uv / uv1;
    float2 rb = rb_uv / uv1;
    float l = min(abs(rb.x - c.x), abs(rb.y - c.y)) + e;
    float2 t = ssUv / uv1;
    float2 v = c - t;

    float fR = max(p / (length(v / l) + e) - p, 0.0);
    float fL = max(p - length(v / l), 0.0);
    fR = clamp(lerp(fR, fL, dAbs), 0.0, 1.0);

    float3 sampled = ss_addon_sample(tex, ssUv).rgb;
    return float4(lerp(float3(1.0, 1.0, 1.0), sampled, colorAmt), fR);
}

#endif // SPRITESTUDIO_ADDON_INCLUDED

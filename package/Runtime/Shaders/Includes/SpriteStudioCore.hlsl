// Shared library of SpriteStudio-specific color composition, PMA, and part-color
// mediation. The single source of truth, #included by both the Built-in hand-written
// HLSL shaders and URP (Shader Graph Custom Function).
//
// Policy:
//  - SS6PU expressed the equivalent logic with #define macros gated by #if defined(PS_*)
//    keywords. Keywords cause shader-variant explosion / device "pink shader" stripping,
//    so here we replace them with "functions instead of macros" and
//    "material-float uniform branches instead of keywords".
//  - PMA in/out (_InputPMA / _OutputPMA) and discard (_NotDiscard) are passed in by the
//    shader as material properties. The per-blend (ssab BlendType) values are set by the
//    C# MaterialKey side, following the per-blend blend-factor table.

#ifndef SPRITESTUDIO_CORE_INCLUDED
#define SPRITESTUDIO_CORE_INCLUDED

// [input mediation point] Un-premultiply the sampled texel in Linear color space for the blends
//  that request it. Sampling itself is done by the caller (RP-specific: tex2D on Built-in,
//  SAMPLE_TEXTURE2D on URP) so this stays render-pipeline agnostic. Mirrors SS6PU's
//  PixelSolveColorspaceInput, gated on color space: in Gamma it is a no-op (touching the input there
//  brightens/shifts the InputPMA modes - Mix/Mul/Mulalpha/Invert - at antialiased edges).
//  TODO(linear): also port SS6PU's pow(1/2.2) input / pow(2.2) output adjust when validating Linear.
float4 ss_input_mediate(float4 c, float inputPMA)
{
#if !defined(UNITY_COLORSPACE_GAMMA)
    if (inputPMA > 0.5 && c.a > (1.0 / 255.0))
    {
        c.rgb /= c.a;
    }
#endif
    return c;
}

// [part-color coefficient table] Per-blend coefficients (Mix/Mul/Add/Sub), mirroring SSPlayerForGodot
//  (ss_library_vs.glsl `_PartColorCoef`) and the SS6 SDK reference. The vertex stage resolves these
//  into `varg`; the fragment then composites with a single branchless formula (ss_partcolor_blend).
//  Keeping the implementation method identical to ForGodot (vertex-resolved table + branchless pixel
//  formula) avoids per-platform spec drift before the all-platform release.
static const float4 _SsPartColorCoef[4] =
{
    float4(-1.0,  1.0, 0.0, 0.0), // Mix
    float4(-1.0,  1.0, 1.0, 0.0), // Mul
    float4( 0.0,  1.0, 0.0, 0.0), // Add
    float4( 0.0, -1.0, 0.0, 0.0)  // Sub
};

// [part-color coefficient resolve | VERTEX stage] Pack the per-part blend kind + rate into the
//  branchless `varg` consumed by ss_partcolor_blend (no per-blend branch in the fragment).
//  blendIdx: 0=Mix / 1=Mul / 2=Add / 3=Sub (lower 4 of ss.format.BlendType).
//  Effect parts (isNormalized > 0.5) tint by the vertex colour (pixel * color); that is the generic
//  formula with coefficients (0, 1, 1), so the fragment stays branchless for effects too. Mirrors
//  SSPlayerForGodot default.vs.
float3 ss_partcolor_varg(float rate, float blendIdx, float isNormalized)
{
    int idx = clamp((int)(blendIdx + 0.5), 0, 3);
    float4 c = _SsPartColorCoef[idx];
    float3 varg = float3(1.0 + c.x * rate, c.y * rate, c.z);
    return lerp(varg, float3(0.0, 1.0, 1.0), step(0.5, isNormalized));
}

// [part-color composition | PIXEL stage] Branchless generic compositing. `varg.xyz` is resolved per
//  vertex by ss_partcolor_varg (the .w slot is unused here; callers reuse it as an alpha carrier).
//  Mirrors SSPlayerForGodot (ss_library_fs.glsl): pixel*varg.x + lerp(1, pixel, varg.z)*color*varg.y.
//  Reproduces Mix/Mul/Add/Sub exactly. NOTE: Add/Sub are intentionally NOT saturated, matching
//  ForGodot (the previous Unity implementation saturated them). The 4-corner colours are already
//  mediated on the CPU and folded into the vertex colour; this only applies the result.
float3 ss_partcolor_blend(float3 pixelRGB, float3 colorRGB, float4 varg)
{
    return pixelRGB * varg.x + lerp(float3(1.0, 1.0, 1.0), pixelRGB, varg.z) * colorRGB * varg.y;
}

// [discard test] Whether to discard the pixel at an alpha threshold. Blends with
//  notDiscard > 0.5 (Mul/Invert, etc.) must keep transparent pixels, so they are not
//  discarded. Function form of SS6PU's PixelDiscardAlpha (PS_NOT_DISCARD).
bool ss_should_discard(float alpha, float threshold, float notDiscard)
{
    return (notDiscard <= 0.5) && (threshold >= alpha);
}

// [output mediation point] Finalize the color into premultiplied alpha. Pass-through when
//  outputPMA <= 0.5. alpha == 0 collapses to black (PMA convention). Equivalent to
//  SS6PU's PixelSolvePMA.
//  Note: masking (stencil) is handled on a separate path (Stencil block), not here.
float4 ss_output_color(float4 color, float outputPMA)
{
    if (outputPMA > 0.5)
    {
        color.rgb = (color.a <= 0.0) ? float3(0.0, 0.0, 0.0) : color.rgb * color.a;
    }
    return color;
}

#endif // SPRITESTUDIO_CORE_INCLUDED

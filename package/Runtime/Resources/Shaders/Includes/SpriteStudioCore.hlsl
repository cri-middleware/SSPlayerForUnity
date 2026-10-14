// Shared library of SpriteStudio-specific color composition, PMA, and part-color
// mediation. The single source of truth, #included by the hand-written shaders of both
// pipelines (Built-in and URP).
//
// Policy:
//  - Keyword-gated #define macros (#if defined(PS_*)) would cause shader-variant explosion /
//    device "pink shader" stripping, so here we use "functions instead of macros" and
//    "material-float uniform branches instead of keywords".
//  - PMA in/out (_InputPMA / _OutputPMA) and discard (_NotDiscard) are passed in by the
//    shader as material properties. The per-blend (ssab BlendType) values are set by the
//    C# MaterialKey side, following the per-blend blend-factor table.

#ifndef SPRITESTUDIO_CORE_INCLUDED
#define SPRITESTUDIO_CORE_INCLUDED

// =====================================================================
// [colorspace conversion seam] SpriteStudio authors its blends in sRGB (gamma) space (the general SS
//  spec). In a Linear-colorspace project the hardware sampler decodes sRGB textures to linear on read
//  and re-encodes the framebuffer on write, which breaks that assumption. To stay faithful we re-encode
//  the sampled texel RGB to sRGB on input and decode the composed RGB back to linear on output. The two
//  functions below are the single point that policy lives; callers (ss_input_mediate, ss_addon_sample*,
//  ss_output_color) route their RGB through them. Swap the WHOLE policy here via SS_COLORSPACE_CONV_MODE
//  (compile-time #define, no multi_compile keyword variant):
//    0 NONE   : identity            - Gamma project; nothing to undo.
//    1 FAST   : sqrt / (c*c)        - cheap pow2 approximation.
//    2 APPROX : Unity LinearToGammaSpace / GammaToLinearSpace - polynomial approximation.
//    3 EXACT  : Unity LinearToGammaSpaceExact / GammaToLinearSpaceExact - true sRGB piecewise curve,
//               exactly cancels the hardware sRGB decode (read) / encode (write).
//  Default: NONE in Gamma projects, EXACT in Linear. Define SS_COLORSPACE_CONV_MODE before this header
//  to override per shader. RULES (do not relax): operate on RGB ONLY - alpha is ALWAYS linear and must
//  never be gamma-adjusted (a past special-order build did and it ruins linear compositing). PartColor /
//  vertex-colour RGB is sRGB by spec and is NOT routed through these (it is already in the right space).
//  These functions MUST stay texture-free / gradient-free: never sample inside them, so they are safe to
//  call from inside the dynamic [loop] AddOns (ss-move) without forcing an implicit-gradient unroll.
//  Depends on UnityCG.cginc (LinearToGammaSpace*/GammaToLinearSpace*), which every SubShader includes
//  before this header.
// =====================================================================
#define SS_CSCONV_NONE   0
#define SS_CSCONV_FAST   1
#define SS_CSCONV_APPROX 2
#define SS_CSCONV_EXACT  3

#ifndef SS_COLORSPACE_CONV_MODE
    #if defined(UNITY_COLORSPACE_GAMMA)
        #define SS_COLORSPACE_CONV_MODE SS_CSCONV_NONE
    #else
        #define SS_COLORSPACE_CONV_MODE SS_CSCONV_EXACT
    #endif
#endif

// linear -> gamma(sRGB). Apply to a sampled texel's RGB right after fetching (undo the HW sRGB decode).
float3 ss_linear_to_gamma(float3 c)
{
#if   SS_COLORSPACE_CONV_MODE == SS_CSCONV_FAST
    return sqrt(c);
#elif SS_COLORSPACE_CONV_MODE == SS_CSCONV_APPROX
    return LinearToGammaSpace(c);
#elif SS_COLORSPACE_CONV_MODE == SS_CSCONV_EXACT
    return float3(LinearToGammaSpaceExact(c.r), LinearToGammaSpaceExact(c.g), LinearToGammaSpaceExact(c.b));
#else // SS_CSCONV_NONE
    return c;
#endif
}

// gamma(sRGB) -> linear. Apply to the composed RGB at the output stage (so the HW re-encode lands right).
float3 ss_gamma_to_linear(float3 c)
{
#if   SS_COLORSPACE_CONV_MODE == SS_CSCONV_FAST
    return c * c;
#elif SS_COLORSPACE_CONV_MODE == SS_CSCONV_APPROX
    return GammaToLinearSpace(c);
#elif SS_COLORSPACE_CONV_MODE == SS_CSCONV_EXACT
    return float3(GammaToLinearSpaceExact(c.r), GammaToLinearSpaceExact(c.g), GammaToLinearSpaceExact(c.b));
#else // SS_CSCONV_NONE
    return c;
#endif
}

// [texture fetch points] The single colour-sampling entry points: every colour texel is fetched through
//  ss_tex2D / ss_tex2Dlod, so the input half of the colorspace seam (ss_linear_to_gamma on RGB) lives in
//  exactly two places. Alpha is passed through untouched (always linear). No V flip here - that is an
//  AddOn-space concern handled by ss_addon_sample; the main path and blur sample at the frag UV directly.
//  Use ss_tex2Dlod (explicit mip 0) inside any varying-iteration loop so the implicit-gradient unroll
//  (FXC X3570) is never triggered; ss_tex2D (implicit LOD) is fine everywhere else. Alpha-only taps
//  (ss-outline, MaskWrite) deliberately keep the raw tex2D - re-encoding RGB they discard is wasted work.
float4 ss_tex2D(sampler2D tex, float2 uv)
{
    float4 c = tex2D(tex, uv);
    c.rgb = ss_linear_to_gamma(c.rgb);
    return c;
}
float4 ss_tex2Dlod(sampler2D tex, float2 uv)
{
    float4 c = tex2Dlod(tex, float4(uv, 0.0, 0.0));
    c.rgb = ss_linear_to_gamma(c.rgb);
    return c;
}

// [input mediation point] Un-premultiply the sampled texel in Linear color space for the blends
//  that request it. Sampling itself is done by the caller (RP-specific: tex2D on Built-in,
//  SAMPLE_TEXTURE2D on URP) so this stays render-pipeline agnostic. Gated on color space: in Gamma
//  it is a no-op (touching the input there brightens/shifts the InputPMA modes -
//  Mix/Mul/Mulalpha/Invert - at antialiased edges).
//  The sRGB<->linear adjust (pow on input/output) now lives in the colorspace seam:
//  ss_tex2D re-encodes sampled RGB to sRGB on input, ss_output_color decodes to linear on output.
//  This function does only the inverse-PMA mediation; it never touches the colorspace itself.
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

// [part-color coefficient table] Per-blend coefficients (Mix/Mul/Add/Sub), from the SS6 SDK reference
//  (ss_library_vs.glsl `_PartColorCoef`). The vertex stage resolves these
//  into `varg`; the fragment then composites with a single branchless formula (ss_partcolor_blend).
//  Implementation method: vertex-resolved table + branchless pixel formula, avoiding per-platform
//  spec drift before the all-platform release.
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
//  formula with coefficients (0, 1, 1), so the fragment stays branchless for effects too. Matches the
//  SS6 SDK reference (default.vs).
float3 ss_partcolor_varg(float rate, float blendIdx, float isNormalized)
{
    int idx = clamp((int)(blendIdx + 0.5), 0, 3);
    float4 c = _SsPartColorCoef[idx];
    float3 varg = float3(1.0 + c.x * rate, c.y * rate, c.z);
    return lerp(varg, float3(0.0, 1.0, 1.0), step(0.5, isNormalized));
}

// [part-color composition | PIXEL stage] Branchless generic compositing. `varg.xyz` is resolved per
//  vertex by ss_partcolor_varg (the .w slot is unused here; callers reuse it as an alpha carrier).
//  Formula (ss_library_fs.glsl): pixel*varg.x + lerp(1, pixel, varg.z)*color*varg.y.
//  Reproduces Mix/Mul/Add/Sub exactly. NOTE: Add/Sub are intentionally NOT saturated
//  (the previous Unity implementation saturated them). The 4-corner colours are already
//  mediated on the CPU and folded into the vertex colour; this only applies the result.
float3 ss_partcolor_blend(float3 pixelRGB, float3 colorRGB, float4 varg)
{
    return pixelRGB * varg.x + lerp(float3(1.0, 1.0, 1.0), pixelRGB, varg.z) * colorRGB * varg.y;
}

// [discard test] Whether to discard the pixel at an alpha threshold. Blends with
//  notDiscard > 0.5 (Mul/Invert, etc.) must keep transparent pixels, so they are not
//  discarded.
bool ss_should_discard(float alpha, float threshold, float notDiscard)
{
    return (notDiscard <= 0.5) && (threshold >= alpha);
}

// [output mediation point] Finalize the color into premultiplied alpha, then the output half of the
//  colorspace seam. First decode the composed sRGB colour to linear (ss_gamma_to_linear, so the
//  hardware linear->sRGB framebuffer encode lands right; no-op in Gamma projects), THEN premultiply by
//  alpha. Order matters: premultiply is a coverage weight and MUST happen in linear space. Doing it in
//  sRGB space first (rgb*a, then decode) computes pow(rgb*a) = pow(rgb)*pow(a), i.e. FG_lin * a^gamma,
//  which under-weights the source at fractional alpha and shows up as a dark fringe at antialiased part
//  edges under Linear colorspace. Pass-through PMA when outputPMA <= 0.5. alpha == 0 collapses to black
//  (PMA convention). RGB only - alpha stays linear.
//  Note: masking (stencil) is handled on a separate path (Stencil block), not here.
float4 ss_output_color(float4 color, float outputPMA)
{
    color.rgb = ss_gamma_to_linear(color.rgb);
    if (outputPMA > 0.5)
    {
        color.rgb = (color.a <= 0.0) ? float3(0.0, 0.0, 0.0) : color.rgb * color.a;
    }
    return color;
}

#endif // SPRITESTUDIO_CORE_INCLUDED

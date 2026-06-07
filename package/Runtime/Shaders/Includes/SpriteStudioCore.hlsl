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

// [part-color composition] Overlay the vertex (overlay) color onto the texture color using
//  the per-part blend kind and rate.
//  blendIdx: 0=Mix / 1=Mul / 2=Add / 3=Sub (lower 4 of ss.format.BlendType).
//  The 4-corner colors are already mediated on the CPU and folded into the vertex color,
//  then interpolated by the rasterizer. This only
//  applies the result; it never re-implements the mediation math.
float3 ss_partcolor_blend(float3 texRGB, float3 ovColorRGB, float rate, float blendIdx)
{
    if (blendIdx < 0.5)        // Mix
        return texRGB * (1.0 - rate) + ovColorRGB * rate;
    else if (blendIdx < 1.5)   // Mul
        return texRGB * (1.0 - rate) + (texRGB * ovColorRGB) * rate;
    else if (blendIdx < 2.5)   // Add
        return saturate(texRGB + ovColorRGB * rate);
    else                       // Sub
        return saturate(texRGB - ovColorRGB * rate);
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

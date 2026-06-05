using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Identifies a unique material variant: a base texture combined with a draw blend mode.
    /// PMA and discard are a function of the blend type (see <see cref="SpriteStudioBlendTable"/>),
    /// so they are not separate key axes. Mask axes will be added here later.
    /// </summary>
    public readonly struct SpriteStudioMaterialKey : IEquatable<SpriteStudioMaterialKey>
    {
        public readonly uint TextureHash;
        public readonly byte BlendType;   // ssab BlendType (0..7)
        // Stencil compare for the mask-test variant (UnityEngine.Rendering.CompareFunction). 0 =
        // Disabled = a plain colour material with no stencil test (the default / non-masked case).
        public readonly byte StencilComp;

        public SpriteStudioMaterialKey(uint textureHash, byte blendType)
            : this(textureHash, blendType, 0) { }

        public SpriteStudioMaterialKey(uint textureHash, byte blendType, byte stencilComp)
        {
            TextureHash = textureHash;
            BlendType = blendType;
            StencilComp = stencilComp;
        }

        public bool Equals(SpriteStudioMaterialKey other)
            => TextureHash == other.TextureHash && BlendType == other.BlendType && StencilComp == other.StencilComp;

        public override bool Equals(object obj)
            => obj is SpriteStudioMaterialKey k && Equals(k);

        public override int GetHashCode()
            => unchecked((((int)TextureHash * 397) ^ BlendType) * 397 ^ StencilComp);
    }

    /// <summary>
    /// Maps each ssab BlendType (0..7) to a GPU blend state plus the PMA / discard policy the
    /// SpriteStudio shader needs (project_blend_mapping_truth).
    ///
    /// Factors are the SDK OpenGL canonical table, keyed by the ssab enum value. The target we
    /// match is the SS authoring tool / SDK output, which uses STRAIGHT (non-premultiplied) alpha,
    /// so InputPMA / OutputPMA are 0 here. (SS6PU premultiplies Mul / Mulalpha / Invert in its own
    /// pipeline; that is SS6PU-specific and would darken those modes at fractional alpha vs the
    /// authoring output.) Mix stays premultiplied because it is mathematically identical either way.
    ///
    /// Never key off SS6PU's enum names: their Mul / Mulalpha are swapped relative to ssab
    /// (ssab Mul = alpha-ignored multiply).
    /// </summary>
    public static class SpriteStudioBlendTable
    {
        public struct State
        {
            public BlendMode Src;
            public BlendMode Dst;
            public BlendOp Op;
            public float InputPMA;   // 0 or 1
            public float OutputPMA;  // 0 or 1
            public float NotDiscard; // 0 or 1
        }

        public static State Get(byte blendType)
        {
            switch (blendType)
            {
                case 0: // Mix
                    return new State { Src = BlendMode.One, Dst = BlendMode.OneMinusSrcAlpha, Op = BlendOp.Add, InputPMA = 1, OutputPMA = 1, NotDiscard = 0 };
                case 1: // Mul (alpha NOT considered = pure multiply; equals SS6PU MUL_NA)
                    return new State { Src = BlendMode.Zero, Dst = BlendMode.SrcColor, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 2: // Add
                    return new State { Src = BlendMode.SrcAlpha, Dst = BlendMode.One, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 3: // Sub
                    return new State { Src = BlendMode.SrcAlpha, Dst = BlendMode.One, Op = BlendOp.ReverseSubtract, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 4: // Mulalpha (alpha considered; equals SS6PU MUL)
                    return new State { Src = BlendMode.DstColor, Dst = BlendMode.OneMinusSrcAlpha, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 5: // Screen
                    return new State { Src = BlendMode.OneMinusDstColor, Dst = BlendMode.One, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 6: // Exclusion
                    return new State { Src = BlendMode.OneMinusDstColor, Dst = BlendMode.OneMinusSrcColor, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 7: // Invert
                    return new State { Src = BlendMode.OneMinusDstColor, Dst = BlendMode.Zero, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                // Mul2 / Div2 / Screen2 / Overlay2 (8..11) are framebuffer-fetch dependent = out of scope.
                default:
                    goto case 0; // fall back to Mix
            }
        }
    }

    /// <summary>Shader property IDs and material-variant construction for SpriteStudio draw blends.</summary>
    public static class SpriteStudioMaterial
    {
        /// <summary>ssab BlendType.Mix. The base material is authored as Mix + PMA, so it needs no variant.</summary>
        public const byte BlendMix = 0;

        static readonly int IdSrcBlend = Shader.PropertyToID("_SrcBlend");
        static readonly int IdDstBlend = Shader.PropertyToID("_DstBlend");
        static readonly int IdBlendOp = Shader.PropertyToID("_BlendOp");
        static readonly int IdInputPMA = Shader.PropertyToID("_InputPMA");
        static readonly int IdOutputPMA = Shader.PropertyToID("_OutputPMA");
        static readonly int IdNotDiscard = Shader.PropertyToID("_NotDiscard");

        // Stencil properties (shared by SpriteStudio/Default's test block and SpriteStudio/MaskWrite).
        static readonly int IdStencilRef = Shader.PropertyToID("_StencilRef");
        static readonly int IdStencilComp = Shader.PropertyToID("_StencilComp");       // color shader: mask test
        static readonly int IdStencilOp = Shader.PropertyToID("_StencilOp");           // MaskWrite shader: incr/decr/invert
        static readonly int IdStencilReadMask = Shader.PropertyToID("_StencilReadMask");
        static readonly int IdStencilWriteMask = Shader.PropertyToID("_StencilWriteMask");
        public static readonly int IdMaskThreshold = Shader.PropertyToID("_MaskThreshold");
        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");

        // Mask stencil bit isolation: the coverage counter lives in the low 5 bits (0x1F => nesting
        // depth up to 31, covering the requested 16). The low bits are required because IncrementWrap's
        // carry lands in bit 0; Forward rendering (BRP + URP Forward, no deferred/decals) leaves them
        // free. See project_unity_mask_port_plan. Adjust here if the target pipeline reserves them.
        public const byte MaskStencilMask = 0x1F;

        /// <summary>
        /// Clone the base material and apply the blend state for the given ssab BlendType. The clone
        /// is flagged HideAndDontSave (runtime-only, never serialized); the owner is responsible for
        /// destroying it (SpriteStudioAnimeAsset destroys its cached variants on disable).
        /// </summary>
        public static Material CreateVariant(Material baseMaterial, byte blendType)
        {
            var state = SpriteStudioBlendTable.Get(blendType);
            var m = new Material(baseMaterial) { hideFlags = HideFlags.HideAndDontSave };
            m.SetFloat(IdSrcBlend, (float)(int)state.Src);
            m.SetFloat(IdDstBlend, (float)(int)state.Dst);
            m.SetFloat(IdBlendOp, (float)(int)state.Op);
            m.SetFloat(IdInputPMA, state.InputPMA);
            m.SetFloat(IdOutputPMA, state.OutputPMA);
            m.SetFloat(IdNotDiscard, state.NotDiscard);
            return m;
        }

        /// <summary>
        /// Build a mask-test colour variant: a normal per-blend colour material whose stencil block
        /// keeps/discards fragments by comparing the isolated mask bits. <paramref name="stencilComp"/>
        /// is a UnityEngine.Rendering.CompareFunction (NotEqual = draw inside the mask, Equal = draw
        /// outside). Ref is 0 so "masked" means "any isolated bit set". The clone is HideAndDontSave.
        /// </summary>
        public static Material CreateTestVariant(Material baseMaterial, byte blendType, byte stencilComp)
        {
            var m = CreateVariant(baseMaterial, blendType);
            m.SetFloat(IdStencilRef, 0f);
            m.SetFloat(IdStencilComp, stencilComp);
            m.SetFloat(IdStencilReadMask, MaskStencilMask);
            m.SetFloat(IdStencilWriteMask, 0f); // a test never writes the stencil
            return m;
        }

        /// <summary>
        /// Build a stencil-write material (SpriteStudio/MaskWrite) for a mask writer. <paramref name="stencilOp"/>
        /// is a UnityEngine.Rendering.StencilOp (IncrementWrap/DecrementWrap/Invert). The per-frame
        /// cutout threshold is NOT set here (it varies per frame); pass it via a MaterialPropertyBlock
        /// (_MaskThreshold) at draw time. The material is HideAndDontSave (runtime-only).
        /// </summary>
        public static Material CreateMaskWriteMaterial(Shader maskWriteShader, Texture mainTex, byte stencilOp)
        {
            var m = new Material(maskWriteShader) { hideFlags = HideFlags.HideAndDontSave };
            if (mainTex != null) m.SetTexture(IdMainTex, mainTex);
            m.SetFloat(IdStencilRef, 0f);
            m.SetFloat(IdStencilOp, stencilOp);
            m.SetFloat(IdStencilReadMask, MaskStencilMask);
            m.SetFloat(IdStencilWriteMask, MaskStencilMask);
            return m;
        }
    }
}

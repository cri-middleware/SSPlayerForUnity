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
        public readonly byte BlendType; // ssab BlendType (0..7)

        public SpriteStudioMaterialKey(uint textureHash, byte blendType)
        {
            TextureHash = textureHash;
            BlendType = blendType;
        }

        public bool Equals(SpriteStudioMaterialKey other)
            => TextureHash == other.TextureHash && BlendType == other.BlendType;

        public override bool Equals(object obj)
            => obj is SpriteStudioMaterialKey k && Equals(k);

        public override int GetHashCode()
            => unchecked(((int)TextureHash * 397) ^ BlendType);
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
    }
}

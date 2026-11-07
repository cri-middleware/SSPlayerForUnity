using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Identifies a unique material variant: a base texture combined with a draw blend mode.
    /// PMA and discard are a function of the blend type (see <see cref="SpriteStudioBlendTable"/>),
    /// so they are not separate key axes.
    /// </summary>
    internal readonly struct SpriteStudioMaterialKey : IEquatable<SpriteStudioMaterialKey>
    {
        public readonly uint TextureHash;
        public readonly byte BlendType;   // ssab BlendType (0..7)
        // Stencil compare for the mask-test variant (UnityEngine.Rendering.CompareFunction). 0 =
        // Disabled = a plain colour material with no stencil test (the default / non-masked case).
        public readonly byte StencilComp;
        // Per-part AddOn shader id hash (FrameData PartAttributeShader.IdHash). 0 = the shared default
        // shader (SpriteStudio/Default). Non-zero selects an AddOn shader variant from the catalog.
        public readonly uint ShaderHash;

        public SpriteStudioMaterialKey(uint textureHash, byte blendType)
            : this(textureHash, blendType, 0, 0) { }

        public SpriteStudioMaterialKey(uint textureHash, byte blendType, byte stencilComp)
            : this(textureHash, blendType, stencilComp, 0) { }

        public SpriteStudioMaterialKey(uint textureHash, byte blendType, byte stencilComp, uint shaderHash)
        {
            TextureHash = textureHash;
            BlendType = blendType;
            StencilComp = stencilComp;
            ShaderHash = shaderHash;
        }

        public bool Equals(SpriteStudioMaterialKey other)
            => TextureHash == other.TextureHash && BlendType == other.BlendType && StencilComp == other.StencilComp && ShaderHash == other.ShaderHash;

        public override bool Equals(object obj)
            => obj is SpriteStudioMaterialKey k && Equals(k);

        public override int GetHashCode()
            => unchecked(((((int)TextureHash * 397) ^ BlendType) * 397 ^ StencilComp) * 397 ^ (int)ShaderHash);
    }

    /// <summary>
    /// Maps each ssab BlendType (0..7) to a GPU blend state plus the PMA / discard policy the
    /// SpriteStudio shader needs.
    ///
    /// Factors are the SDK OpenGL canonical table, keyed by the ssab enum value. The target we
    /// match is the SS authoring tool / SDK output, which uses STRAIGHT (non-premultiplied) alpha,
    /// so InputPMA is 0 for every blend. (Un-premultiplying a straight-alpha texture would brighten
    /// the input at fractional alpha.) Mix must also stay InputPMA = 0: the plain-Mix path uses the
    /// base material (shader default InputPMA = 0), so setting it to 1 here only for the variant
    /// paths (masked / AddOn Mix, via CreateVariant) would make those parts disagree with plain Mix
    /// at antialiased edges under Linear colorspace. Keeping 0 here keeps every Mix path consistent.
    ///
    /// Key off the ssab BlendType values, not names: Mul / Mulalpha can be swapped relative to their
    /// names (ssab Mul = alpha-ignored multiply).
    /// </summary>
    internal static class SpriteStudioBlendTable
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
                    return new State { Src = BlendMode.One, Dst = BlendMode.OneMinusSrcAlpha, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 1, NotDiscard = 0 };
                case 1: // Mul (alpha NOT considered = pure multiply)
                    return new State { Src = BlendMode.Zero, Dst = BlendMode.SrcColor, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 2: // Add
                    return new State { Src = BlendMode.SrcAlpha, Dst = BlendMode.One, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 3: // Sub
                    return new State { Src = BlendMode.SrcAlpha, Dst = BlendMode.One, Op = BlendOp.ReverseSubtract, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 4: // Mulalpha (alpha considered)
                    return new State { Src = BlendMode.DstColor, Dst = BlendMode.OneMinusSrcAlpha, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 5: // Screen
                    return new State { Src = BlendMode.OneMinusDstColor, Dst = BlendMode.One, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 6: // Exclusion
                    return new State { Src = BlendMode.OneMinusDstColor, Dst = BlendMode.OneMinusSrcColor, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                case 7: // Invert
                    return new State { Src = BlendMode.OneMinusDstColor, Dst = BlendMode.Zero, Op = BlendOp.Add, InputPMA = 0, OutputPMA = 0, NotDiscard = 0 };
                // The SS6-era "2" variants have no fixed-function equivalent. Mul2 and Screen2 draw as Mul and
                // Screen; Div2 and Overlay2 need the backdrop, which no blend state reaches, and fall back to
                // Mix with anything unknown.
                case 8: // Mul2
                    goto case 1;
                case 10: // Screen2
                    goto case 5;
                default:
                    goto case 0; // fall back to Mix
            }
        }
    }

    /// <summary>Shader property IDs and material-variant construction for SpriteStudio draw blends.</summary>
    internal static class SpriteStudioMaterial
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

        // Per-part AddOn shader params (_SsParam0.._SsParam7), set on the part's own material each frame.
        static readonly int IdSsParam0 = Shader.PropertyToID("_SsParam0");
        static readonly int IdSsParam1 = Shader.PropertyToID("_SsParam1");
        static readonly int IdSsParam2 = Shader.PropertyToID("_SsParam2");
        static readonly int IdSsParam3 = Shader.PropertyToID("_SsParam3");
        static readonly int IdSsParam4 = Shader.PropertyToID("_SsParam4");
        static readonly int IdSsParam5 = Shader.PropertyToID("_SsParam5");
        static readonly int IdSsParam6 = Shader.PropertyToID("_SsParam6");
        static readonly int IdSsParam7 = Shader.PropertyToID("_SsParam7");
        // Per-part cell rectangle (pixel UVs lt.u/lt.v/rb.u/rb.v) for cell-rect AddOns (circle / spot).
        static readonly int IdSsCellRect = Shader.PropertyToID("_SsCellRect");

        /// <summary>
        /// Push a submesh's per-part AddOn params (P0..P7) onto its material. Setting a property the
        /// shader does not declare is a harmless no-op in Unity, so an AddOn that uses only _SsParam0
        /// (e.g. sepia) ignores the rest. Material (not MaterialPropertyBlock) so SRP Batcher is kept.
        /// </summary>
        public static void ApplyAddOnParams(Material m, in SubmeshInfo info)
        {
            m.SetFloat(IdSsParam0, info.P0);
            m.SetFloat(IdSsParam1, info.P1);
            m.SetFloat(IdSsParam2, info.P2);
            m.SetFloat(IdSsParam3, info.P3);
            m.SetFloat(IdSsParam4, info.P4);
            m.SetFloat(IdSsParam5, info.P5);
            m.SetFloat(IdSsParam6, info.P6);
            m.SetFloat(IdSsParam7, info.P7);
            // Cell rect (a no-op for AddOns that do not declare _SsCellRect, e.g. sepia).
            m.SetVector(IdSsCellRect, new Vector4(info.CellL, info.CellT, info.CellR, info.CellB));
        }

        // Mask stencil bit isolation: the whole SS mask lives in the low 4 bits. These are URP's
        // user-reserved bits (URP StencilUsage: bits 0-3 = UserMask; bit 4 = light stencil, bits 5-6 =
        // material type, bit 7 reserved), so confining the SS mask to 0x0F coexists with every URP
        // feature (Decals / Rendering Layers / SSAO / camera stacking / deferred). Built-in shares the
        // same layout. Caution: do not widen past 0x0F or the mask will corrupt (and be corrupted by)
        // URP's reserved bits - in particular bit 4 is NOT free.
        //
        // The 4 bits are split into two independent planes, because the two writer ops are not mutually
        // commutative and would corrupt each other if they shared bits (a writer's coverage has to be
        // undone exactly by its paired restore, whatever is drawn in between):
        //
        //   Incr -> Invert : 0 -> 1 -> 254 (covered)
        //   Invert -> Incr : 0 -> 255 -> 0 (NOT covered)   <- same two writers, order-dependent result
        //   Incr(1) -> another Invert(254) -> Decr(253)    <- restore does not return to 0; the residue
        //                                                     leaks onto every part drawn afterwards
        //
        // A stencil write is `new = (op(old) & WriteMask) | (old & ~WriteMask)`: the op still runs on the
        // full value, but restricting the written bits discards the carry/borrow, so each plane stays
        // closed. Invert only ever flips the parity bit and never touches the counter.
        //
        //   maskInfluence=1 -> Invert            -> parity plane 0x08 (self-inverse; unlimited overlap)
        //   maskInfluence=0 -> Incr / DecrWrap   -> counter plane 0x07 (overlap depth up to 7)
        //
        // Coverage stays "any isolated bit set" (Ref 0 + Equal/NotEqual over ReadMask 0x0F), which reads
        // the two planes as a union - matching SpriteStudio 7.5, where mixed maskInfluence composes as a union.
        public const byte MaskStencilMask = 0x0F;

        /// <summary>Parity plane (Invert writers, maskInfluence=1). Overlap composes as XOR/parity.</summary>
        public const byte MaskStencilMaskParity = 0x08;

        /// <summary>Counter plane (IncrementWrap/DecrementWrap writers, maskInfluence=0). Depth up to 7.</summary>
        public const byte MaskStencilMaskCount = 0x07;

        /// <summary>
        /// The stencil write mask a mask writer must use for the given op. Op and plane are 1:1
        /// (Invert -> parity, IncrementWrap/DecrementWrap -> counter), so the plane never has to be
        /// threaded through the call chain alongside the op. Keep every writer routed through this so
        /// the two planes cannot drift apart.
        /// </summary>
        public static byte MaskWriteMaskForOp(byte stencilOp)
            => stencilOp == (byte)UnityEngine.Rendering.StencilOp.Invert
                ? MaskStencilMaskParity
                : MaskStencilMaskCount;

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
        /// Build a per-part AddOn colour variant: clone the texture base (carries _MainTex + the import
        /// props), apply the per-blend state, then swap to the AddOn shader. Matching property names
        /// (_MainTex, blend / stencil props) carry over by name across the shader swap; the AddOn's own
        /// params (_SsParam0..) keep their defaults until the per-part dispatch sets them per frame.
        /// The clone is HideAndDontSave (runtime-only); the owner destroys it on disable.
        /// </summary>
        public static Material CreateAddOnVariant(Material textureBase, Shader addonShader, byte blendType)
        {
            var m = CreateVariant(textureBase, blendType);
            if (addonShader != null) m.shader = addonShader;
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
        /// is a UnityEngine.Rendering.StencilOp (IncrementWrap/DecrementWrap/Invert). The write mask is
        /// derived from the op so Invert and Incr/Decr writers land on separate bit planes (see
        /// MaskWriteMaskForOp); the caller's cache key already includes the op, so the two planes get
        /// their own material variants for free. The per-frame cutout threshold is NOT set here (it
        /// varies per frame); pass it via a MaterialPropertyBlock (_MaskThreshold) at draw time. The
        /// material is HideAndDontSave (runtime-only).
        /// </summary>
        public static Material CreateMaskWriteMaterial(Shader maskWriteShader, Texture mainTex, byte stencilOp)
        {
            var m = new Material(maskWriteShader) { hideFlags = HideFlags.HideAndDontSave };
            if (mainTex != null) m.SetTexture(IdMainTex, mainTex);
            m.SetFloat(IdStencilRef, 0f);
            m.SetFloat(IdStencilOp, stencilOp);
            // Read the union of both planes; write only this op's own plane.
            m.SetFloat(IdStencilReadMask, MaskStencilMask);
            m.SetFloat(IdStencilWriteMask, MaskWriteMaskForOp(stencilOp));
            return m;
        }
    }
}

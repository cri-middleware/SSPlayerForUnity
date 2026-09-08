using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Layer 2: shared logic layer (Internal Player).
    /// Holds the FFI context (void*) to the native (Rust) side and manages the
    /// animation Tick and Job System-based mesh construction.
    /// Enforces strict lifecycle management (IDisposable) to prevent memory leaks.
    /// </summary>
    public class SpriteStudioInternalPlayer : IDisposable
    {
        // Native context (pointer management is delegated to it).
        internal SpriteStudioNativeContext _nativeContext;
        internal SsPlayerHandle _handle => _nativeContext.Handle;
        private unsafe void* _resourceHandle => _nativeContext.ResourceHandle;

        /// <summary>
        /// Current native runtime handle as an <see cref="System.IntPtr"/>, or Zero when the handle is
        /// invalid (e.g. after <see cref="Dispose"/>). Lets the update manager re-check a player's
        /// liveness after the prepare pass without dereferencing a possibly-freed raw pointer.
        /// Internal on purpose: a raw native pointer must not become package API (handing it to an
        /// arbitrary DllImport crashes the runtime with no diagnostic).
        /// </summary>
        internal System.IntPtr NativeHandle { get { unsafe { return _handle.IsValid ? (System.IntPtr)_handle.ptr : System.IntPtr.Zero; } } }

        public delegate byte[] AssetResolver(uint packHash, uint nameHash);
        private AssetResolver _assetResolver;

        internal SpriteStudioGeometryBuilder _geometryBuilder;

        public JobHandle CurrentUpdateJobHandle => _geometryBuilder != null ? _geometryBuilder.CurrentUpdateJobHandle : default;
        public int VertexCount => _geometryBuilder != null ? _geometryBuilder.VertexCount : 0;
        public int IndexCount => _geometryBuilder != null ? _geometryBuilder.IndexCount : 0;
        public int SubmeshCount => _geometryBuilder != null ? _geometryBuilder.SubmeshCount : 0;
        public NativeArray<SubmeshInfo> Submeshes => _geometryBuilder != null ? _geometryBuilder.Submeshes : default;
        public NativeArray<SsVertex> MeshVertices => _geometryBuilder.MeshVertices;
        public NativeArray<uint> MeshIndices => _geometryBuilder.MeshIndices;
        // The child's own topology signature. A parent folds each visible instance child's signature into its
        // own so its index-upload cache reacts to a child-internal reorder (which the parent's coarse per-batch
        // signature cannot see on its own - it treats the child as one opaque block).
        public ulong TopologySignature => _geometryBuilder != null ? _geometryBuilder.TopologySignature : 0UL;

        /// <summary>
        /// Uniform scale applied to vertex positions (= 1 / PixelsPerUnit). Default 1 (= pixel-for-pixel).
        /// Set only on the top-level player; Instance children stay at 1
        /// (children are ingested into the parent in px and scaled once by the parent; prevents double scaling).
        /// </summary>
        public float VertexScale { get; set; } = 1f;

        /// <summary>Mirror the drawn animation horizontally / vertically. See <see cref="RootScale"/>.</summary>
        public bool FlipX { get; set; }

        /// <inheritdoc cref="FlipX"/>
        public bool FlipY { get; set; }

        /// <summary>
        /// Post-scale shift of the drawn animation, in Unity world units. Like <see cref="FlipX"/> this moves
        /// only what this player draws, never the GameObject, so children and colliders stay where they are.
        /// Same "top-level player only" rule as <see cref="VertexScale"/>.
        /// </summary>
        public Vector2 VertexOffset { get; set; } = Vector2.zero;

        /// <summary>
        /// <see cref="VertexScale"/> resolved per axis, negated on each flipped axis. This is the one place the
        /// flip is turned into numbers: both the mesh job and <see cref="TryGetPartLocalMatrix"/> read it, so a
        /// part tracker cannot drift away from the geometry it is supposed to be pinned to.
        /// </summary>
        internal Vector2 RootScale => new Vector2(FlipX ? -VertexScale : VertexScale,
                                                  FlipY ? -VertexScale : VertexScale);

        private bool _isDisposed = false;

        ~SpriteStudioInternalPlayer()
        {
            if (!_isDisposed)
            {
                Debug.LogError("[SpriteStudioInternalPlayer] Memory leak detected! Dispose() was not called before the object was garbage collected.");
            }
        }

        
        // --- Effect / mask / instance-slot state ---

        internal struct EffectSlotState { public System.IntPtr EffectSlot; }
        internal EffectSlotState[] _effectSlots;
        internal int[] _effectPartIndices;
        internal int _effectPartCount;
        internal bool _hasEffectParts;

internal struct MaskPartData
        {
            public bool IsMaskPart;        // part_type == PartTypeMask
            public bool DrawAsMask;        // unified pure-mask flag (shape/text/nines mask)
            public bool MaskWrite;         // clipping writer flag
            public bool MaskInfluence;     // writer OP (false=increment / true=invert); on a drawn target = mask opt-in
            public bool VisibleInsideMask; // target polarity (draw where masked == VisibleInsideMask)
            // Derived (static), mirroring _build_mask_writers:
            public bool PureMask;          // IsMaskPart || DrawAsMask: draws no colour, masks parts drawn BEFORE it
            public bool Writes;            // PureMask || MaskWrite: contributes stencil coverage
            public bool IsClipping;        // MaskWrite && !PureMask: draws normally, masks parts drawn AFTER it
            // Geometry kind the mesh job uses to build this part's stencil-write geometry. PartTypeMask
            // has no colour batch, so it is built as a Normal quad from local vertices (Mask parts are
            // routed through the Normal build path).
            public byte DrawKind;          // SsDrawBatchKind: Normal / Shape / Mesh
        }
        // Per-part static mask metadata (index-aligned with ssab parts). Null until SetupSlots runs.
        internal MaskPartData[] _maskPartData;

        // Part name -> part index (into the world-matrix array). Built in SetupSlots; used by part tracking.
        private Dictionary<string, int> _partNameToIndex;
        // Part index -> part name (index-aligned with ssab parts). Built once in SetupSlots so the event
        // dispatch path resolves names by array lookup instead of decoding a fresh string from the
        // FlatBuffer per event. Null until SetupSlots runs.
        private string[] _partNames;
        // Part index -> "is this a bone-skinned mesh part" (index-aligned with ssab parts). A skinned mesh's
        // own transform (what part tracking reads) sits at its setup/parent node, not where the bone-driven
        // vertices draw, so a tracker pinned to it lands off the art; a rigid (deform-only) mesh has no bones
        // and is false. Built in SetupSlots; null until then.
        private bool[] _partSkinnedMesh;
        // How many of `_partNames` belong to the current pack. The array is grown but never shrunk, so
        // its Length is capacity, not the part count.
        private int _partCount;
        // True when this ssab has at least one mask writer (pure mask or clipping). When false the
        // whole mask path is skipped, like _build_mask_writers returning empty.
        internal bool _hasMaskParts;

        // Per-part AddOn shader catalog (set by the owning world player each frame before the build).
        // null = no per-part shader dispatch (the Canvas/Graphic path leaves this null). The geometry
        // builder consults it to carve out is-per-part shader parts; the player resolves their material.
        public SpriteStudioShaderCatalog ShaderCatalog;

        // When true (the Canvas/uGUI path), SpriteStudio internal masking is suppressed and the stencil
        // buffer is left to uGUI's own Mask/RectMask2D. The world (MeshRenderer) path leaves it false.
        internal bool _suppressInternalMask;

        // Mask context inherited from the instance part that draws this player. SS7.5 composes the two
        // masking flags down the instance hierarchy - visibleInsideMask accumulates with OR, maskInfluence
        // chains with AND - so the parent writes these right before it builds this child and the child
        // bakes the composed result into its own submesh roles. The defaults are the identity elements of
        // those operators, which is exactly what a root player (drawn by nobody) has to see. Nesting needs
        // no extra work: every level hands on the value it already composed.
        internal bool _inheritedMasked;                 // an ancestor instance part is a mask target
        internal bool _inheritedMaskInfluence = true;   // AND identity
        internal bool _inheritedVisibleInsideMask;      // OR identity

        // True when masking must be honoured: this asset has writers of its own, or an ancestor instance
        // is a mask target and its coverage still reaches these parts (a child asset with no mask parts of
        // its own is still clipped by the mask around the instance that draws it). Canvas suppresses all
        // of it and leaves the stencil to uGUI.
        // _maskPartData is required because both paths below read per-part flags out of it; _hasMaskParts
        // already implies it, but _inheritedMasked alone does not (it is set by the parent, which cannot
        // know whether this child has finished SetupSlots).
        internal bool MaskActive => (_hasMaskParts || _inheritedMasked) && _maskPartData != null && !_suppressInternalMask;

        // True when parts have to be emitted one by one so each can carry its own stencil role: this asset
        // has mask writers, or an ancestor instance's mask reaches these parts. Canvas suppression is NOT
        // tested here - the per-part path still has to run to drop pure masks - so it checks MaskActive.
        internal bool MaskAffectsParts => (_hasMaskParts || _inheritedMasked) && _maskPartData != null;

        // === Mask composition rules (SS7.5) ================================================
        // Kept here, as three small statics, so the geometry builder and the tests share one definition
        // of the rules instead of restating them at each decision site.

        /// <summary>
        /// Whether a part is clipped by the stencil, composing its own maskInfluence with the value
        /// inherited from the instance chain (AND). MaskWrite is deliberately outside the composition:
        /// it says the part writes a mask of its own, which is not something a descendant inherits.
        /// </summary>
        internal static bool IsMaskTarget(in MaskPartData md, bool inheritedMaskInfluence)
            => (md.MaskInfluence && inheritedMaskInfluence) || md.MaskWrite;

        /// <summary>
        /// A part's effective polarity: draw inside the mask (true) or outside it (false).
        /// visibleInsideMask accumulates with OR down the instance chain, so once any level asks for
        /// "inside", every part below it draws inside.
        /// </summary>
        internal static bool IsDrawnInsideMask(in MaskPartData md, bool inheritedVisibleInsideMask)
            => md.VisibleInsideMask || inheritedVisibleInsideMask;

        /// <summary>
        /// Compose the context an instance part hands to the player it draws. Each level passes on what
        /// it has already composed, so nesting chains without any extra bookkeeping - and because
        /// maskInfluence chains with AND, once it reaches false it can never return to true, leaving
        /// "not masked" decided for the whole subtree.
        /// </summary>
        internal static void ComposeInheritedContext(in MaskPartData instancePart,
            bool inheritedMaskInfluence, bool inheritedVisibleInsideMask,
            out bool childMasked, out bool childMaskInfluence, out bool childVisibleInsideMask)
        {
            childMaskInfluence = inheritedMaskInfluence && instancePart.MaskInfluence;
            childVisibleInsideMask = IsDrawnInsideMask(instancePart, inheritedVisibleInsideMask);
            childMasked = IsMaskTarget(instancePart, inheritedMaskInfluence);
        }

        /// <summary>
        /// A mask WRITER's effective maskInfluence: its own value composed (AND) with the value inherited
        /// down the instance chain. The writer's stencil op - and therefore its bit plane - follows this,
        /// not the raw own flag: influence=1 -> Invert (parity plane, self-inverse), influence=0 ->
        /// Incr/Decr (counter plane, union). So a mask inside a maskInfluence=0 instance moves to the
        /// counter plane and composes with an outer mask as a union - which is what SS7.5 does when
        /// maskInfluence is mixed. This is the writer-side twin of the target-side composition in
        /// <see cref="IsMaskTarget"/>. A root player passes the AND identity (true), leaving a standalone
        /// asset's planes unchanged; MaskWrite is deliberately outside it (a writer's op is about its own
        /// mask, not something a descendant inherits).
        /// </summary>
        internal static bool EffectiveMaskInfluence(in MaskPartData md, bool inheritedMaskInfluence)
            => md.MaskInfluence && inheritedMaskInfluence;

        /// <summary>
        /// Suppress SpriteStudio internal masking for this player and (recursively) its instance
        /// children. Set true on the Canvas path; left false for the world path. Propagates so an
        /// instance child's own internal masks are also dropped on Canvas instead of writing stencil.
        /// </summary>
        public bool SuppressInternalMask
        {
            get => _suppressInternalMask;
            set
            {
                _suppressInternalMask = value;
                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        var c = _instanceChildren[i].Player;
                        if (c != null) c.SuppressInternalMask = value;
                    }
                }
            }
        }

        internal struct InstanceChildState { public System.IntPtr InstanceSlot; public SpriteStudioInternalPlayer Player; }
        internal InstanceChildState[] _instanceChildren;
        internal int[] _instancePartIndices;
        internal int _instancePartCount;
        internal bool _hasInstanceParts;

        // ----------------------------------
        public SpriteStudioInternalPlayer()
        {
            _nativeContext = new SpriteStudioNativeContext();
            _geometryBuilder = new SpriteStudioGeometryBuilder(this);
        }

        public event Action OnAnimationFinished;
        public event Action OnAnimationLooped;
        public event Action<SpriteStudioUserData> OnUserData;
        public event Action<SpriteStudioSignal> OnSignal;
        public event Action<SpriteStudioAudio> OnAudio;

        private ss.format.SsAnimeBinary? _ssab;
        private ss.format.AnimationData? _currentAnimation;

        // Rising-edge memory for OnAnimationFinished (previous value of is_finished).
        private bool _prevFinished;

        // Grow-only backing buffer for SpriteStudioSignal parameters, filled per PollEvents pass and sliced
        // per signal (each signal gets a distinct [start, count) range) — no per-signal allocation and no
        // boxing. Reset at the start of each poll; a signal's slice stays valid until the next poll on this
        // player. See SpriteStudioSignal / AppendSignalParam.
        private SpriteStudioSignalParam[] _signalParamBuffer;
        private int _signalParamCount;

        // True once an ssab has been loaded into the native runtime. Lets the owner (PlaybackCore /
        // player) detect "Internal created but ssab not yet loaded" — which happens when OnEnable runs
        // before the .ssab finishes importing on an Editor restart — and self-heal by loading it later.
        public bool HasSsab => _hasSsab;
        private bool _hasSsab;

        // The pack this player was loaded with (SsAnimeBinary.name_hash), and the packs loaded above it.
        // SetupSlots walks the *pack's* Instance parts, so a part that resolves back to a pack already on
        // this chain makes LoadSsab -> SetupSlots recurse until the stack dies — taking the Editor with it
        // (a StackOverflowException cannot be caught). Resolution has two fallbacks that can return the
        // resolver's own SsabData (a same-pack reference, and the animation-name-hash search that runs when
        // the pack hash does not match), so this is reachable whenever an import leaves ExternalReferences
        // unpopulated. Keyed on the pack hash rather than on `byte[]` identity, so a resolver that
        // materialises fresh bytes per call (Addressables / AssetBundle / File.ReadAllBytes) is caught too.
        // The chain is only meaningful while a subtree is being built; SetupSlots drops it on the way in.
        private uint _packHash;
        private HashSet<uint> _ancestorPackHashes;

        // The buffer whose load failed. LoadSsab is retried every frame while !HasSsab (the "asset still
        // importing" self-heal in SpriteStudioPlaybackCore.EnsureCreated), so without this a permanently
        // bad buffer would log its error once per frame, forever. Cleared on a successful load.
        private byte[] _failedSsabData;

        /// <summary>
        /// Load SsabData (a byte array) and bind the resources.
        /// Recursively creates child players if there are Instance parts.
        /// </summary>
        public void LoadSsab(byte[] ssabData, AssetResolver resolver = null)
        {
            _assetResolver = resolver;
            // Nothing is set up on the pack about to be bound, whatever was set up on the one being
            // replaced. Cleared here rather than on success, because a REFUSED load leaves the previous
            // pack's selection just as meaningless.
            CurrentAnimationName = null;

            // On a reload (runtime asset swap) an in-flight Burst Job may still be borrowing the old
            // ssab buffer. Always complete the Job before destroying / re-borrowing native resources to
            // prevent a segfault from referencing freed memory.
            // On the first load no Job has been scheduled, so Complete() on the default handle is a no-op.
            _geometryBuilder?.CurrentUpdateJobHandle.Complete();

            // Reject data that is not an .ssab at all before handing the bytes to the native runtime
            // (same check the importer applies, so the two cannot disagree about what is loadable).
            if (!SpriteStudioSsabFormat.IsSsabBuffer(ssabData))
            {
                _hasSsab = false;
                LogLoadFailureOnce(ssabData, "Refused to load .ssab: missing \"SSAB\" identifier.");
                return;
            }

            // Keep the native load + slot setup from throwing out through the owning component's OnEnable
            // on malformed input or an unexpected edge (e.g. resource exhaustion).
            try
            {
                // Release order matters, and mirrors Dispose(): the previous load's effect slots and
                // instance children are derived from the resource the native context is about to destroy
                // (an effect slot borrows both that resource and the pinned ssab buffer for its whole
                // lifetime), so they have to go BEFORE the reload — not afterwards in SetupSlots.
                ReleaseDerivedHandles();

                _nativeContext.LoadSsab(ssabData);

                var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
                _ssab = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb);
                if (SetupSlots(_ssab.Value))
                {
                    _hasSsab = true;
                    _failedSsabData = null;
                }
                else
                {
                    // Nothing was set up because the native resource is missing (ss_runtime_create failed),
                    // so nothing is playable. Leave HasSsab false rather than reporting a successful load:
                    // the owner's EnsureCreated then keeps retrying instead of rendering nothing forever.
                    _hasSsab = false;
                    _ssab = null;
                    _currentAnimation = null;
                    LogLoadFailureOnce(ssabData, "Failed to load .ssab: the native runtime is unavailable.");
                }
            }
            catch (Exception e)
            {
                _hasSsab = false;
                LogLoadFailureOnce(ssabData, "Failed to load .ssab.", e);
            }
        }

        // Report a failing buffer once instead of once per frame: LoadSsab is retried every frame while
        // !HasSsab (see _failedSsabData). A different array — a reimport, an asset swap — reports again.
        private void LogLoadFailureOnce(byte[] ssabData, string message, Exception e = null)
        {
            if (ReferenceEquals(ssabData, _failedSsabData)) return;
            _failedSsabData = ssabData;
            Debug.LogError("[SpriteStudioInternalPlayer] " + message);
            if (e != null) Debug.LogException(e);
        }

        /// <summary>
        /// Destroy the handles derived from the native resource / runtime: the effect slots created from
        /// the resource, and the instance children that own their own native contexts. Must run BEFORE the
        /// native context that owns the resource is destroyed or reloaded, so a derived handle is never
        /// left pointing at a freed resource or an unpinned buffer. Idempotent — the arrays are nulled as
        /// they are released — so both <see cref="Dispose"/> and the <see cref="LoadSsab"/> reload path
        /// can call it unconditionally.
        /// </summary>
        private void ReleaseDerivedHandles()
        {
            _hasEffectParts = false;
            _hasInstanceParts = false;
            _effectPartCount = 0;
            _instancePartCount = 0;
            unsafe
            {
                if (_effectSlots != null)
                {
                    for (int i = 0; i < _effectSlots.Length; i++)
                    {
                        if (_effectSlots[i].EffectSlot != System.IntPtr.Zero)
                        {
                            NativeMethods.ss_effect_slot_destroy((void*)_effectSlots[i].EffectSlot);
                        }
                    }
                    _effectSlots = null;
                }

                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        if (_instanceChildren[i].InstanceSlot != System.IntPtr.Zero)
                        {
                            NativeMethods.ss_instance_slot_destroy((void*)_instanceChildren[i].InstanceSlot);
                        }
                        // A child's Dispose is recursive managed code that can throw; isolate it so a
                        // failure does not stop sibling releases.
                        try { _instanceChildren[i].Player?.Dispose(); }
                        catch (Exception e) { Debug.LogException(e); }
                    }
                    _instanceChildren = null;
                }
            }
        }

        /// <summary>
        /// Build the per-part tables and the effect / instance slots for a freshly loaded binary.
        /// Precondition: the caller has already released the previous load's derived handles
        /// (<see cref="ReleaseDerivedHandles"/>) and reloaded the native context.
        /// False when nothing could be set up because the native resource is missing; the caller must then
        /// leave HasSsab false rather than report the load as successful.
        /// </summary>
        private bool SetupSlots(ss.format.SsAnimeBinary binary)
        {
            _hasMaskParts = false;
            _maskPartData = null;
            _packHash = binary.NameHash;
            _geometryBuilder._lastBuiltDrawFrameNo = float.NaN; // always rebuild on reload

            // Drop the previous load's part tables up front, before the early returns below: a stale
            // name <-> index map would resolve event part names and part trackers against the parts of the
            // asset that is no longer loaded.
            _partNameToIndex?.Clear();
            if (_partNames != null) System.Array.Clear(_partNames, 0, _partNames.Length);
            if (_partSkinnedMesh != null) System.Array.Clear(_partSkinnedMesh, 0, _partSkinnedMesh.Length);
            _partCount = 0;

            // The ancestor chain is only needed while this subtree is being built. Take it and drop the
            // field on every exit path, so a leaf player does not pin one HashSet per level for its
            // whole lifetime.
            var ancestors = _ancestorPackHashes;
            _ancestorPackHashes = null;

            unsafe
            {
                if (_resourceHandle == null) return false;

                int partsLength = binary.PartsLength;
                if (partsLength == 0) return true; // a valid pack with nothing to set up

                _effectSlots = new EffectSlotState[partsLength];
                _instanceChildren = new InstanceChildState[partsLength];
                _effectPartIndices = new int[partsLength];
                _instancePartIndices = new int[partsLength];
                _maskPartData = new MaskPartData[partsLength];

                // Part-name -> index map for part tracking (resolve user-specified part names to the
                // index used by the world-matrix array). Built once per load; static for the asset.
                // (Both tables were emptied above; only capacity is grown here.)
                if (_partNameToIndex == null) _partNameToIndex = new Dictionary<string, int>(partsLength);
                if (_partNames == null || _partNames.Length < partsLength) _partNames = new string[partsLength];
                if (_partSkinnedMesh == null || _partSkinnedMesh.Length < partsLength) _partSkinnedMesh = new bool[partsLength];
                _partCount = partsLength;

                // What an Instance child may not resolve to: this pack, or any pack above it. Handed to
                // every child so the chain grows one link per level.
                var chain = ancestors == null ? new HashSet<uint>() : new HashSet<uint>(ancestors);
                chain.Add(_packHash);

                for (int i = 0; i < partsLength; i++)
                {
                    var pObj = binary.Parts(i);
                    if (!pObj.HasValue) continue;
                    var p = pObj.Value;

                    // Decode the FlatBuffer name once and cache it both by-index (event path) and
                    // by-name (part tracking). First occurrence wins on the (unexpected) chance of
                    // duplicate names.
                    string partName = p.Name;
                    _partNames[i] = partName;
                    if (!string.IsNullOrEmpty(partName) && !_partNameToIndex.ContainsKey(partName))
                        _partNameToIndex[partName] = i;

                    // Cache whether this is a bone-skinned mesh (see the field / SpriteStudioSsabFormat): the
                    // part-tracking path warns on these because a skinned mesh's own node is not where its art draws.
                    _partSkinnedMesh[i] = SpriteStudioSsabFormat.IsSkinnedMeshPart(p);

                    // Cache the static mask flags for every part (any Normal/Mesh/Shape/Mask part can
                    // be a writer or a target). Derivation mirrors _build_mask_writers exactly.
                    {
                        bool isMaskPart = p.PartTypeType == ss.format.PartType.PartTypeMask;
                        bool drawAsMask = p.DrawAsMask;
                        bool maskWrite = p.MaskWrite;
                        bool pureMask = isMaskPart || drawAsMask;
                        bool writes = pureMask || maskWrite;
                        // Geometry kind for the mesh job. PartTypeMask -> Normal quad (no colour batch).
                        byte drawKind = SsDrawBatchKind.Normal;
                        if (p.PartTypeType == ss.format.PartType.PartTypeShape) drawKind = SsDrawBatchKind.Shape;
                        else if (p.PartTypeType == ss.format.PartType.PartTypeMesh) drawKind = SsDrawBatchKind.Mesh;
                        _maskPartData[i] = new MaskPartData
                        {
                            IsMaskPart = isMaskPart,
                            DrawAsMask = drawAsMask,
                            MaskWrite = maskWrite,
                            MaskInfluence = p.MaskInfluence,
                            VisibleInsideMask = p.VisibleInsideMask,
                            PureMask = pureMask,
                            Writes = writes,
                            IsClipping = maskWrite && !pureMask,
                            DrawKind = drawKind,
                        };
                        if (writes) _hasMaskParts = true;
                    }

                    if (p.PartTypeType == ss.format.PartType.PartTypeEffect)
                    {
                        var pt = p.PartTypeAsPartTypeEffect();
                        uint refHash = pt.RefEffectNameHash;
                        uint seed = (uint)i ^ refHash;
                        void* slot = NativeMethods.ss_effect_slot_create(_resourceHandle, refHash, seed);
                        _effectSlots[i].EffectSlot = (System.IntPtr)slot;
                        if (slot != null)
                        {
                            _effectPartIndices[_effectPartCount++] = i;
                            _hasEffectParts = true;
                        }
                    }
                    else if (p.PartTypeType == ss.format.PartType.PartTypeInstance)
                    {
                        var pt = p.PartTypeAsPartTypeInstance();
                        uint packHash = pt.RefAnimePackHash;
                        uint nameHash = pt.RefAnimeNameHash;

                        // Resolve the child animation's SSAB. Within the same pack this returns our own
                        // SSAB; for another pack it returns the externally referenced SSAB (see
                        // SpriteStudioAnimeAsset.ResolveExternalSsab). At runtime no file is read; it just
                        // follows the hard references fixed at import time.
                        byte[] childSsab = _assetResolver?.Invoke(packHash, nameHash);
                        if (childSsab == null)
                        {
                            UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance part {i}: referenced animation (pack=0x{packHash:X8}, name=0x{nameHash:X8}) could not be resolved. Check the AnimeAsset's external references / pack hash.");
                            continue;
                        }

                        // Cycle check on the child's own pack hash rather than on `byte[]` identity: a
                        // resolver that materialises fresh bytes per call (Addressables / AssetBundle /
                        // File.ReadAllBytes) hands back a different array for the same pack every time,
                        // and an identity check would let that recurse until the stack dies.
                        if (!SpriteStudioSsabFormat.TryGetPackHash(childSsab, out uint childPackHash))
                        {
                            UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance part {i}: referenced animation (pack=0x{packHash:X8}, name=0x{nameHash:X8}) resolved to data that is not an .ssab. Skipping the part.");
                            continue;
                        }

                        if (chain.Contains(childPackHash))
                        {
                            UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance part {i}: referenced animation (pack=0x{packHash:X8}, name=0x{nameHash:X8}) resolved to pack 0x{childPackHash:X8}, which is already being loaded further up the chain — mounting it would recurse forever. Skipping the part. The AnimeAsset's external references are most likely unpopulated, so resolution fell back to its own SSAB — reimport it.");
                            continue;
                        }

                        // childPlayer allocates native runtime/resource handles in its constructor and in
                        // LoadSsab BEFORE it is stored into _instanceChildren below. If any step here
                        // throws, it would never be Disposed — and the finalizer only frees the GCHandle
                        // pin, not the native handles — so it would leak native memory permanently.
                        // Dispose it on any failure, then keep loading the remaining parts.
                        var childPlayer = new SpriteStudioInternalPlayer { SubFrameEnabled = SubFrameEnabled };
                        try
                        {
                            // Inherit the parent's suppression so children (re)created on load match the
                            // Canvas/world choice; the public setter also propagates to existing children.
                            childPlayer._suppressInternalMask = _suppressInternalMask;
                            childPlayer._ancestorPackHashes = chain;
                            childPlayer.LoadSsab(childSsab, _assetResolver);

                            if (!childPlayer.SetupAnimationByHash(nameHash))
                            {
                                UnityEngine.Debug.LogWarning($"[SpriteStudioInternalPlayer] Instance part {i}: animation hash 0x{nameHash:X8} not found in the resolved SSAB.");
                                childPlayer.Dispose();
                                continue;
                            }
                            childPlayer.Stop(); // wait stopped until driven by the parent (step controls visibility)

                            void* slot = NativeMethods.ss_instance_slot_create();
                            _instanceChildren[i] = new InstanceChildState
                            {
                                InstanceSlot = (System.IntPtr)slot,
                                Player = childPlayer
                            };
                            _instancePartIndices[_instancePartCount++] = i;
                            _hasInstanceParts = true;
                        }
                        catch (Exception e)
                        {
                            UnityEngine.Debug.LogException(e);
                            try { childPlayer.Dispose(); } catch (Exception e2) { UnityEngine.Debug.LogException(e2); }
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// The animation currently set up on the native runtime, or null when none is (before the first
        /// setup, and after a <see cref="LoadSsab"/> that replaced the pack). This is what "already set
        /// up" is judged against — never a component's serialized animation name, which holds the value
        /// the host wants rather than the one the runtime has.
        /// </summary>
        public string CurrentAnimationName { get; private set; }

        public bool SetupAnimationByHash(uint animationNameHash)
        {
            if (!_handle.IsValid) return false;
            unsafe
            {
                bool ok = NativeMethods.ss_runtime_setup_animation_by_hash(_handle.ptr, animationNameHash);
                if (ok)
                {
                    // setup_animation resets the native world matrices to identity; force a geometry rebuild.
                    _geometryBuilder?.InvalidateFrameCache();

                    if (_ssab.HasValue)
                    {
                        _currentAnimation = null;
                        var binary = _ssab.Value;
                        for (int i = 0; i < binary.AnimationsLength; i++)
                        {
                            var anim = binary.Animations(i);
                            if (anim.HasValue && anim.Value.NameHash == animationNameHash)
                            {
                                _currentAnimation = anim;
                                break;
                            }
                        }
                    }
                    // Keep the by-name token in step, so a later SetupAnimation(name) for the animation
                    // this hash selected is correctly recognised as already set up.
                    CurrentAnimationName = _currentAnimation?.Name;
                }
                return ok;
            }
        }

        #region Playback Control
        public bool IsPlaying() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_playing(_handle.ptr); } }
        // Whether the current effective playback direction is forward (also reflects ping-pong turns, etc.). Used to decide not to play sounds during reverse playback.
        public bool IsPlayingForward() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_playing_forward(_handle.ptr); } }
        public void Play(float startFrame = -1.0f)
        {
            unsafe
            {
                if (!_handle.IsValid) return;
                // Play() always heads out (rewinds to a start frame); "resume" is Pause()'s toggle,
                // not Play(). So a fresh play must clear each slot's "last started key" memory, else a
                // re-played animation would debounce an already-fired independent effect/instance as
                // the same key and never re-fire it.
                ResetSlotsPlaybackState();
                // Re-arm the OnAnimationFinished edge so replaying a finished animation reports its
                // next completion.
                _prevFinished = false;
                if (startFrame < 0.0f) NativeMethods.ss_runtime_play(_handle.ptr);
                else NativeMethods.ss_runtime_play_with_start_frame(_handle.ptr, startFrame);
            }
        }

        /// <summary>
        /// Reset every effect/instance slot's "last started key" playback memory (recursing into
        /// instance children) so an explicit head-out re-fires independent effects/instances from
        /// scratch instead of debouncing the first key as the same one the slot last started. Called
        /// from Play(). The reset itself lives in the runtime (ss_effect_slot_reset /
        /// ss_instance_slot_reset); the host only iterates the slots it owns.
        /// </summary>
        internal void ResetSlotsPlaybackState()
        {
            unsafe
            {
                if (_effectSlots != null)
                {
                    for (int i = 0; i < _effectSlots.Length; i++)
                    {
                        if (_effectSlots[i].EffectSlot != System.IntPtr.Zero)
                            NativeMethods.ss_effect_slot_reset((void*)_effectSlots[i].EffectSlot);
                    }
                }
                if (_instanceChildren != null)
                {
                    for (int i = 0; i < _instanceChildren.Length; i++)
                    {
                        if (_instanceChildren[i].InstanceSlot != System.IntPtr.Zero)
                            NativeMethods.ss_instance_slot_reset((void*)_instanceChildren[i].InstanceSlot);
                        // Recurse so a re-play also restarts independent effects/instances nested
                        // inside instance children (their slots live on the child).
                        _instanceChildren[i].Player?.ResetSlotsPlaybackState();
                    }
                }
            }
        }
        public bool IsPausing() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_pausing(_handle.ptr); } }
        /// <summary>
        /// Whether every configured loop has played. A <b>state</b>, not a pulse: it latches on
        /// completion and holds until the next play/setup. Never true under an infinite loop count,
        /// and not raised by <c>Stop</c> — a commanded stop is not a completion.
        /// </summary>
        public bool IsFinished() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_finished(_handle.ptr); } }
        /// <summary>
        /// Whether the last update crossed a loop boundary. A <b>pulse</b>: the runtime clears it at the
        /// top of every update, so it only reads true inside the tick that crossed — which is why it is
        /// not named <c>IsLooped</c>. Reading it a tick late reads false.
        /// </summary>
        public bool JustLooped() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_is_looped(_handle.ptr); } }
        public void Pause() { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_pause(_handle.ptr); } }
        public void Resume() { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_resume(_handle.ptr); } }
        public void Stop() { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_stop(_handle.ptr); } }

        public void SetSpeed(float speed) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_animation_speed(_handle.ptr, speed); } }

        /// <summary>
        /// The speed in effect, as a multiple of the frame rate. Read from the runtime rather than from a
        /// copy of what was last set: a zero or negative rate is clamped to a stop there, so a cached value
        /// would report the -1 that was asked for while playback is actually held at 0.
        /// </summary>
        public float GetSpeed() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_animation_speed(_handle.ptr) : 1.0f; } }

        public void SetFrame(float frame) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_frame_no(_handle.ptr, frame); } }
        public float GetFrame() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_frame_no(_handle.ptr) : 0.0f; } }

        /// <summary>
        /// Frames in the range being played, both ends inclusive — the <b>section</b> when one is set, the
        /// whole animation otherwise. Matches every sibling Player's `end - start + 1`; it used to be
        /// `end + 1`, which counts from frame 0 whatever the section starts at.
        /// </summary>
        public int GetTotalFrames()
        {
            unsafe
            {
                if (!_handle.IsValid) return 0;
                return NativeMethods.ss_runtime_get_end_frame(_handle.ptr) - NativeMethods.ss_runtime_get_start_frame(_handle.ptr) + 1;
            }
        }

        public void SetFrameRate(int fps) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_frame_rate(_handle.ptr, fps); } }
        public int GetFrameRate() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_fps(_handle.ptr) : 0; } }

        public void SetAnimationSection(int start, int end) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_animation_section(_handle.ptr, start, end); } }
        public int GetAnimationSectionStart() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_start_frame(_handle.ptr) : 0; } }
        public int GetAnimationSectionEnd() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_end_frame(_handle.ptr) : 0; } }

        /// <summary>Number of animations in the loaded ssab (0 if none loaded).</summary>
        public int GetAnimationCount() => _ssab.HasValue ? _ssab.Value.AnimationsLength : 0;

        /// <summary>Name of the animation at <paramref name="index"/> in the loaded ssab, or null if out of range.</summary>
        public string GetAnimationName(int index)
        {
            if (!_ssab.HasValue || index < 0 || index >= _ssab.Value.AnimationsLength) return null;
            var anim = _ssab.Value.Animations(index);
            return anim.HasValue ? anim.Value.Name : null;
        }

        // --- Part tracking support ---

        /// <summary>Resolve a part name to its index, or -1 if the part is not present in this animation.</summary>
        public int FindPartIndex(string partName)
        {
            if (string.IsNullOrEmpty(partName) || _partNameToIndex == null) return -1;
            return _partNameToIndex.TryGetValue(partName, out int idx) ? idx : -1;
        }

        /// <summary>
        /// Whether the given part is a bone-skinned mesh. Its own transform (what <see cref="TryGetPartLocalMatrix"/>
        /// and part tracking read) is the setup/parent node, not where the skinned vertices draw, so a tracker
        /// pinned to it lands off the visible art — track a null / bone-point (bone or joint) part instead.
        /// Returns false (with <paramref name="isSkinnedMesh"/> = false) when the index is out of range or nothing
        /// is loaded; a rigid (deform-only) mesh reports false because it tracks correctly.
        /// </summary>
        public bool TryGetPartSkinnedMesh(int partIndex, out bool isSkinnedMesh)
        {
            isSkinnedMesh = false;
            if (_partSkinnedMesh == null || partIndex < 0 || partIndex >= _partCount) return false;
            isSkinnedMesh = _partSkinnedMesh[partIndex];
            return true;
        }

        /// <summary>
        /// Part names in part-index order, empty before a pack is set up. A part authored without a
        /// name reads as the empty string rather than being skipped, so the array stays aligned with
        /// the PartIndex every event carries. Length is the part count.
        /// </summary>
        public string[] GetPartNames()
        {
            if (_partNames == null || _partCount == 0) return System.Array.Empty<string>();
            var names = new string[_partCount];
            for (int i = 0; i < _partCount; i++) names[i] = _partNames[i] ?? string.Empty;
            return names;
        }

        /// <summary>Resolve a part index to its cached name, or null if out of range. Avoids decoding a
        /// fresh FlatBuffer string per event in the event dispatch path.</summary>
        private string PartNameAt(int index)
        {
            return (_partNames != null && (uint)index < (uint)_partNames.Length) ? _partNames[index] : null;
        }

        /// <summary>
        /// Player-local transform of the given part for the current frame, matching what the mesh draws:
        /// (1/PPU) * draw_m. Column-major; the draw matrix already folds in inherited Scale and the part's
        /// own localScale (callers that want to ignore localScale divide it out from the extracted scale).
        /// Returns false if the handle is invalid or the index is out of range.
        /// <para>
        /// The matrix carries no skew: SpriteStudio's Skew is a non-inheriting vertex deformation baked into
        /// the drawn vertices, not into the part's coordinate frame, so a pose extracted from this matrix (and
        /// therefore any part tracker driven by it) is skew-free — matching how skew does not propagate to a
        /// part's children in the editor.
        /// </para>
        /// <para>
        /// The basis may be a reflection (negative determinant) — from the root transform's flip, or from a
        /// negative scale on the part or its parents. Callers must not extract a rotation or a scale from it
        /// without making the basis proper first; a quaternion cannot hold a reflection.
        /// </para>
        /// </summary>
        public bool TryGetPartLocalMatrix(int partIndex, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (partIndex < 0) return false;
            unsafe
            {
                if (!_handle.IsValid) return false;
                float* data = null;
                nuint len = 0;
                NativeMethods.ss_runtime_get_world_matrices(_handle.ptr, &data, &len);
                if (data == null) return false;
                int baseIndex = partIndex * 16;
                if ((long)baseIndex + 16 > (long)len) return false;

                // Native layout is column-major 4x4 (draw_m[0..3]=col0 ... [12..15]=col3), which matches
                // Unity Matrix4x4's linear indexer (index = row + column*4).
                var m = new Matrix4x4();
                for (int e = 0; e < 16; e++) m[e] = data[baseIndex + e];

                // Fold in the same root transform the mesh job applies to vertex positions, so the tracked
                // frame stays pixel-aligned with the rendered part — otherwise a tracker pinned to a part
                // detaches the moment the player is flipped or offset. Z keeps the unflipped scale: this is
                // a 2D player, so a flip mirrors within the XY plane rather than through it.
                Vector2 rs = RootScale;
                Vector2 ro = VertexOffset;
                matrix = Matrix4x4.TRS(new Vector3(ro.x, ro.y, 0f), Quaternion.identity,
                                       new Vector3(rs.x, rs.y, VertexScale)) * m;
                return true;
            }
        }

        /// <summary>
        /// Per-part hide / localScale for the current frame (for part tracking). False if unavailable;
        /// outputs default to visible / unit scale.
        /// </summary>
        public bool TryGetPartState(int partIndex, out bool hidden, out float localScaleX, out float localScaleY)
        {
            hidden = false;
            localScaleX = 1f;
            localScaleY = 1f;
            return _geometryBuilder != null
                && _geometryBuilder.TryGetPartState(partIndex, out hidden, out localScaleX, out localScaleY);
        }

        /// <summary>
        /// Resolved colour of a part on the current frame, with any per-part colour override folded in.
        /// False when the part carries no colour this frame. Inspection hook (the render path reads the
        /// colours in bulk).
        /// </summary>
        internal bool TryGetPartColor(int partIndex, out ss.runtime.PartAttributePartColor partColor)
        {
            partColor = default;
            return _geometryBuilder != null && _geometryBuilder.TryGetPartColor(partIndex, out partColor);
        }

        /// <summary>Local UVs per part in the native array: 5 pairs (4 corners + centre).</summary>
        internal const int LocalUvStride = 10;

        /// <summary>
        /// The part's local UVs for the current frame (<see cref="LocalUvStride"/> floats), with any per-part
        /// cell override folded in — this is the sampled image, so a cell swap shows up here. A part that
        /// draws no cell keeps an all-zero block. False when the handle is invalid, the index is out of
        /// range, or the destination is too small. Inspection hook (the mesh job reads the UVs in bulk).
        /// </summary>
        internal bool TryGetPartUvs(int partIndex, float[] destination)
        {
            if (partIndex < 0 || destination == null || destination.Length < LocalUvStride) return false;
            unsafe
            {
                if (!_handle.IsValid) return false;
                float* data = null;
                nuint len = 0;
                NativeMethods.ss_runtime_get_local_uvs(_handle.ptr, &data, &len);
                if (data == null) return false;
                long baseIndex = (long)partIndex * LocalUvStride;
                if (baseIndex + LocalUvStride > (long)len) return false;
                for (int i = 0; i < LocalUvStride; i++) destination[i] = data[baseIndex + i];
                return true;
            }
        }

        public void SetPlaybackDirection(int direction, int style) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_playback_direction(_handle.ptr, direction, style); } }
        public int GetPlaybackDirection() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_playback_direction(_handle.ptr) : 0; } }
        public int GetPlaybackStyle() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_playback_style(_handle.ptr) : 0; } }

        public void SetLoop(int count) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_loop(_handle.ptr, count); } }
        public int GetLoop() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_loops(_handle.ptr) : 0; } }

        /// <summary>
        /// Cycles left of <see cref="GetLoop"/>, counting down as playback completes them (-1 while the
        /// count is infinite). Writing the loop count RE-ARMS this, so anything that pushes the count
        /// back restarts the budget — which is why re-asserting the current animation must not.
        /// </summary>
        public int GetRemainLoops() { unsafe { return _handle.IsValid ? NativeMethods.ss_runtime_get_remain_loops(_handle.ptr) : 0; } }

        // skip_frames (real-time following) state is held on the Rust side
        // (PlaybackController.is_skip_frames). This is a thin pass-through. Rust's setup() resets
        // skip_frames to its default (true) just like the other playback parameters (loop/style/speed),
        // so re-applying the desired value is done together in PlaybackCore.Play.
        public void SetSkipFrames(bool skip) { unsafe { if (_handle.IsValid) NativeMethods.ss_runtime_set_skip_frames(_handle.ptr, skip); } }
        public bool IsFrameSkipEnabled() { unsafe { return _handle.IsValid && NativeMethods.ss_runtime_get_skip_frames(_handle.ptr); } }

        public bool SubFrameEnabled { get; set; } = false;
        #endregion

        #region Part Overrides
        // Per-part runtime overrides (color / cell / visibility): an imperative "make this part this
        // colour / cell / hidden now" that wins over both the keyframe and the blend, applied by the
        // runtime on the resolved pose. The runtime owns the override table and its whole lifecycle
        // (HoldUntilNextAnimation is cleared on setup_animation, Permanent is re-validated, and a
        // reset / new ssab clears everything), so the player keeps no copy and never re-applies.
        //
        // Parts are addressed by index. Indices are positional over the asset's parts and stable
        // within one asset, so callers resolve a name once via ResolvePartIndex and reuse the index.
        // The name-based overloads here do exactly that; an unknown name resolves to -1 and the call
        // is a no-op returning false.
        //
        // A successful set/clear invalidates the frame-skip cache: an override changes the pose
        // without changing the frame number, so a paused (or otherwise unchanged) frame would
        // otherwise reuse the previous geometry and swallow the change.

        /// <summary>
        /// Force-hide a part (<paramref name="hide"/> = true) or revert it to the animation
        /// (false). With <paramref name="cascade"/> the force-hide propagates to the part's
        /// descendants. Visibility has no priority mode. False if the part is unknown or the call
        /// is rejected.
        /// </summary>
        public bool SetPartVisibilityOverride(int partIndex, bool hide, bool cascade = false)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                // force: 0 = clear/revert, 1 = force-hide (2 = force-visible is reserved by the runtime).
                byte force = hide ? (byte)1 : (byte)0;
                if (!NativeMethods.ss_runtime_set_part_visibility_override(_handle.ptr, (uint)partIndex, force, cascade ? (byte)1 : (byte)0))
                    return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="SetPartVisibilityOverride(int, bool, bool)"/>
        public bool SetPartVisibilityOverride(string partName, bool hide, bool cascade = false)
            => SetPartVisibilityOverride(FindPartIndex(partName), hide, cascade);

        /// <summary>Clear a part's visibility override (revert to the animation).</summary>
        public bool ClearPartVisibilityOverride(int partIndex)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                if (!NativeMethods.ss_runtime_clear_part_visibility_override(_handle.ptr, (uint)partIndex)) return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="ClearPartVisibilityOverride(int)"/>
        public bool ClearPartVisibilityOverride(string partName) => ClearPartVisibilityOverride(FindPartIndex(partName));

        /// <summary>
        /// Override a part's colour with a single value (all four corners). Applies to Normal parts
        /// only; other part types silently ignore it (the call still returns true). The colour is
        /// interpreted in the same 8-bit sRGB space as the authored PartColor and the runtime
        /// pre-multiplies alpha itself — do not pre-multiply or gamma-convert it here.
        /// </summary>
        public bool SetPartColorOverride(int partIndex, Color color,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                if (!NativeMethods.ss_runtime_set_part_color_override(_handle.ptr, (uint)partIndex, (byte)blendOp, PackRgba(color), (byte)priority))
                    return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="SetPartColorOverride(int, Color, SpriteStudioBlendOp, SpriteStudioOverridePriority)"/>
        public bool SetPartColorOverride(string partName, Color color,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => SetPartColorOverride(FindPartIndex(partName), color, blendOp, priority);

        /// <summary>
        /// Override a part's colour with four distinct corners (left-top, right-top, left-bottom,
        /// right-bottom). Same contract as the single-colour form.
        /// </summary>
        public bool SetPartColorOverrideCorners(int partIndex, SpriteStudioCornerColors corners,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                // Corner order is the runtime's: lt, rt, lb, rb.
                uint* packed = stackalloc uint[4];
                packed[0] = PackRgba(corners.LeftTop);
                packed[1] = PackRgba(corners.RightTop);
                packed[2] = PackRgba(corners.LeftBottom);
                packed[3] = PackRgba(corners.RightBottom);
                if (!NativeMethods.ss_runtime_set_part_color_override_corners(_handle.ptr, (uint)partIndex, (byte)blendOp, packed, (byte)priority))
                    return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="SetPartColorOverrideCorners(int, SpriteStudioCornerColors, SpriteStudioBlendOp, SpriteStudioOverridePriority)"/>
        public bool SetPartColorOverrideCorners(string partName, SpriteStudioCornerColors corners,
            SpriteStudioBlendOp blendOp = SpriteStudioBlendOp.Mix,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => SetPartColorOverrideCorners(FindPartIndex(partName), corners, blendOp, priority);

        /// <summary>Clear a part's colour override (revert to the animation).</summary>
        public bool ClearPartColorOverride(int partIndex)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                if (!NativeMethods.ss_runtime_clear_part_color_override(_handle.ptr, (uint)partIndex)) return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="ClearPartColorOverride(int)"/>
        public bool ClearPartColorOverride(string partName) => ClearPartColorOverride(FindPartIndex(partName));

        /// <summary>
        /// Make a part draw a different cell. The cell is named by its cellmap (without the .ssce
        /// extension) and its cell name, and is resolved immediately — an unresolvable cell returns
        /// false. Applies to Normal and mask parts only; other part types silently ignore it. Empty
        /// names revert the part to the animation.
        /// </summary>
        public bool SetPartCellOverride(int partIndex, string cellmapName, string cellName,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                // (0, 0) is the runtime's "follow the animation" sentinel; empty names hash to 0.
                if (!NativeMethods.ss_runtime_set_part_cell_override(_handle.ptr, (uint)partIndex,
                        HashName(cellmapName), HashName(cellName), (byte)priority))
                    return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="SetPartCellOverride(int, string, string, SpriteStudioOverridePriority)"/>
        public bool SetPartCellOverride(string partName, string cellmapName, string cellName,
            SpriteStudioOverridePriority priority = SpriteStudioOverridePriority.HoldUntilNextAnimation)
            => SetPartCellOverride(FindPartIndex(partName), cellmapName, cellName, priority);

        /// <summary>Clear a part's cell override (revert to the animation).</summary>
        public bool ClearPartCellOverride(int partIndex)
        {
            unsafe
            {
                if (!_handle.IsValid || partIndex < 0) return false;
                if (!NativeMethods.ss_runtime_clear_part_cell_override(_handle.ptr, (uint)partIndex)) return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <inheritdoc cref="ClearPartCellOverride(int)"/>
        public bool ClearPartCellOverride(string partName) => ClearPartCellOverride(FindPartIndex(partName));

        /// <summary>Clear every per-part override on this player.</summary>
        public bool ClearAllPartOverrides()
        {
            unsafe
            {
                if (!_handle.IsValid) return false;
                if (!NativeMethods.ss_runtime_clear_all_part_overrides(_handle.ptr)) return false;
                _geometryBuilder?.InvalidateFrameCache();
                return true;
            }
        }

        /// <summary>Names of the cellmaps in the loaded ssab (empty if nothing is loaded). For discovering cell-override targets.</summary>
        public string[] GetCellmapNames()
        {
            if (!_ssab.HasValue) return System.Array.Empty<string>();
            var binary = _ssab.Value;
            var names = new string[binary.CellmapsLength];
            for (int i = 0; i < binary.CellmapsLength; i++)
            {
                var cm = binary.Cellmaps(i);
                names[i] = cm.HasValue ? cm.Value.Name : null;
            }
            return names;
        }

        /// <summary>Names of the cells in the named cellmap (empty if unknown). For discovering cell-override targets.</summary>
        public string[] GetCellNames(string cellmapName)
        {
            if (!_ssab.HasValue || string.IsNullOrEmpty(cellmapName)) return System.Array.Empty<string>();
            var binary = _ssab.Value;
            for (int i = 0; i < binary.CellmapsLength; i++)
            {
                var cmObj = binary.Cellmaps(i);
                if (!cmObj.HasValue || cmObj.Value.Name != cellmapName) continue;
                var cm = cmObj.Value;
                var names = new string[cm.CellsLength];
                for (int j = 0; j < cm.CellsLength; j++)
                {
                    var cell = cm.Cells(j);
                    names[j] = cell.HasValue ? cell.Value.Name : null;
                }
                return names;
            }
            return System.Array.Empty<string>();
        }

        /// <summary>Pack a colour into the 0xRRGGBBAA the override C-API expects (8-bit sRGB, no gamma conversion).</summary>
        private static uint PackRgba(Color color)
        {
            uint r = (uint)(Mathf.Clamp01(color.r) * 255f + 0.5f);
            uint g = (uint)(Mathf.Clamp01(color.g) * 255f + 0.5f);
            uint b = (uint)(Mathf.Clamp01(color.b) * 255f + 0.5f);
            uint a = (uint)(Mathf.Clamp01(color.a) * 255f + 0.5f);
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        /// <summary>
        /// FNV-1a hash of a name, via the runtime's own exported hasher so cell overrides key on
        /// exactly the hashes the asset was built with. An empty name hashes to 0 (the sentinel).
        /// </summary>
        internal static uint HashName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0u;
            unsafe
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
                fixed (byte* p = bytes)
                {
                    return NativeMethods.ss_runtime_hash_string(p);
                }
            }
        }
        #endregion

        public bool SetupAnimation(string animationName)
        {
            if (!_handle.IsValid || string.IsNullOrEmpty(animationName)) return false;
            unsafe
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(animationName + "\0");
                fixed (byte* p = bytes)
                {
                    bool ok = NativeMethods.ss_runtime_setup_animation(_handle.ptr, p);
                    if (ok)
                    {
                        // setup_animation resets the native world matrices to identity; force a geometry
                        // rebuild so a same-numbered start frame is not skipped (otherwise trackers read
                        // identity = origin).
                        _geometryBuilder?.InvalidateFrameCache();

                        if (_ssab.HasValue)
                        {
                            _currentAnimation = null;
                            var binary = _ssab.Value;
                            for (int i = 0; i < binary.AnimationsLength; i++)
                            {
                                var anim = binary.Animations(i);
                                if (anim.HasValue && anim.Value.Name == animationName)
                                {
                                    _currentAnimation = anim;
                                    break;
                                }
                            }
                        }
                        CurrentAnimationName = animationName;
                    }
                    return ok;
                }
            }
        }

        public void SetPlaybackStyle(int direction, int style)
        {
            if (!_handle.IsValid) return;
            unsafe { NativeMethods.ss_runtime_set_playback_direction(_handle.ptr, direction, style); }
        }

        /// <summary>
        /// Per-frame animation update.
        /// </summary>
        /// <param name="deltaTime">Elapsed time.</param>
        public void Tick(float deltaTime, bool subFrameEnabled)
        {
            // Reliably wait for the Job scheduled last frame to finish (for a pure zero-copy pipeline).
            _geometryBuilder?.CurrentUpdateJobHandle.Complete();

            if (!_handle.IsValid) return;

            unsafe
            {
                // Tick is executed strictly sequentially on the main thread (state mutability boundary)
                // ss_runtime_update takes delta in "milliseconds" (update_interval = 1000/fps).
                // Unity's Time.deltaTime is in seconds, so multiply by 1000.
                NativeMethods.ss_runtime_update(_handle.ptr, deltaTime * 1000.0f);
                PollEvents();
            }
        }

        /// <summary>
        /// Drain the events accumulated in the Rust runtime and fire them as C#-side events.
        /// Must be called immediately after ss_runtime_update.
        /// </summary>
        public void PollEvents()
        {
            if (!_handle.IsValid) return;
            // A callback fired below can re-enter PollEvents on this same player. Mark the shared
            // signal-param buffer on the way in and restore the mark on the way out, so a nested pass
            // appends ABOVE the slices the outer dispatch is still reading instead of recycling them from
            // zero. Restoring (rather than resetting to 0 on entry) keeps the buffer bounded: a top-level
            // poll always enters at mark 0. Skipping the nested pass instead would drop that pass's whole
            // event batch permanently — the runtime clears its passed-event list on the next update.
            int paramMark = _signalParamCount;
            try
            {
            unsafe
            {
                // is_finished stays true from the moment every configured loop has played until the next
                // play/setup, so fire OnAnimationFinished only on its rising edge. Nothing here early-returns
                // on !is_playing, which makes _prevFinished the sole dedupe -- do not drop it.
                // Commit the edge BEFORE dispatching: a handler that replays from inside the callback
                // re-arms the edge (Play() sets _prevFinished = false), and writing after the callback
                // would clobber that re-arm and swallow the next completion.
                bool finished = NativeMethods.ss_runtime_is_finished(_handle.ptr);
                bool rising = finished && !_prevFinished;
                _prevFinished = finished;
                if (rising)
                {
                    OnAnimationFinished?.Invoke();
                }

                // is_looped is reset to false every update() and becomes true only on an update that
                // crosses a loop boundary (one pulse per update), so it can be fired as-is.
                if (NativeMethods.ss_runtime_is_looped(_handle.ptr))
                {
                    OnAnimationLooped?.Invoke();
                }

                int eventCount = NativeMethods.ss_runtime_get_passed_event_count(_handle.ptr);
                if (eventCount > 0 && _currentAnimation.HasValue)
                {
                    var anim = _currentAnimation.Value;
                    for (int i = 0; i < eventCount; i++)
                    {
                        int eventIdx = NativeMethods.ss_runtime_get_passed_event_index(_handle.ptr, i);
                        var eventsObj = anim.Events(eventIdx);
                        if (!eventsObj.HasValue) continue;
                        var events = eventsObj.Value;

                        if (OnUserData != null && events.UsersLength > 0)
                        {
                            for (int j = 0; j < events.UsersLength; j++)
                            {
                                var userOpt = events.Users(j);
                                if (!userOpt.HasValue || !userOpt.Value.Value.HasValue) continue;
                                var user = userOpt.Value;
                                var val = user.Value.Value;

                                var ev = new SpriteStudioUserData
                                {
                                    PartIndex = user.PartIndex,
                                    FrameNo = events.FrameIndex,
                                    PartName = PartNameAt(user.PartIndex),
                                    HasInt = val.Integer.HasValue,
                                    IntValue = val.Integer.HasValue ? val.Integer.Value.Value : 0,
                                    HasString = val.Str != null,
                                    StringValue = val.Str,
                                    HasRect = val.Rect.HasValue,
                                    RectValue = val.Rect.HasValue ? new UnityEngine.Rect(val.Rect.Value.X1, val.Rect.Value.Y1, val.Rect.Value.X2 - val.Rect.Value.X1, val.Rect.Value.Y2 - val.Rect.Value.Y1) : default,
                                    HasPoint = val.Point.HasValue,
                                    PointValue = val.Point.HasValue ? new UnityEngine.Vector2(val.Point.Value.V1, val.Point.Value.V2) : default
                                };
                                OnUserData.Invoke(ev);
                            }
                        }

                        if (OnSignal != null && events.SignalsLength > 0)
                        {
                            for (int j = 0; j < events.SignalsLength; j++)
                            {
                                var sigOpt = events.Signals(j);
                                if (!sigOpt.HasValue || !sigOpt.Value.Value.HasValue) continue;
                                var sig = sigOpt.Value;
                                var val = sig.Value.Value;
                                // Do not notify inactive signals.
                                if (!val.Active) continue;

                                // Decode the command parameters (union: Bool/Index/Integer/Floating/Text)
                                // into the grow-only per-player buffer as typed values (no boxing). This
                                // signal owns the slice [paramStart, paramStart + written).
                                int paramStart = _signalParamCount;
                                int written = 0;
                                int paramCount = val.ParamsLength;
                                for (int k = 0; k < paramCount; k++)
                                {
                                    var paramOpt = val.Params(k);
                                    if (!paramOpt.HasValue) continue;
                                    var param = paramOpt.Value;
                                    var paramId = param.ParamId;
                                    if (paramId == null) continue;

                                    SpriteStudioSignalParam decoded;
                                    switch (param.ValueType)
                                    {
                                        case ss.format.SignalCommandParamValue.Bool:     decoded = new SpriteStudioSignalParam(paramId, param.ValueAsBool().Value); break;
                                        case ss.format.SignalCommandParamValue.Index:    decoded = new SpriteStudioSignalParam(paramId, SpriteStudioSignalParamType.Index, param.ValueAsIndex().Value); break;
                                        case ss.format.SignalCommandParamValue.Integer:  decoded = new SpriteStudioSignalParam(paramId, SpriteStudioSignalParamType.Integer, param.ValueAsInteger().Value); break;
                                        case ss.format.SignalCommandParamValue.Floating: decoded = new SpriteStudioSignalParam(paramId, param.ValueAsFloating().Value); break;
                                        case ss.format.SignalCommandParamValue.Text:     decoded = new SpriteStudioSignalParam(paramId, param.ValueAsText().Value); break;
                                        default: continue;
                                    }
                                    AppendSignalParam(decoded);
                                    written++;
                                }

                                var ev = new SpriteStudioSignal
                                {
                                    PartIndex = sig.PartIndex,
                                    FrameNo = events.FrameIndex,
                                    PartName = PartNameAt(sig.PartIndex),
                                    Active = val.Active,
                                    CommandId = val.CommandId,
                                    _params = _signalParamBuffer,
                                    _paramStart = paramStart,
                                    ParamCount = written,
                                };
                                OnSignal.Invoke(ev);
                            }
                        }

                        if (OnAudio != null && events.AudiosLength > 0)
                        {
                            for (int j = 0; j < events.AudiosLength; j++)
                            {
                                var audioOpt = events.Audios(j);
                                if (!audioOpt.HasValue || !audioOpt.Value.Value.HasValue) continue;
                                var audio = audioOpt.Value;
                                var val = audio.Value.Value;
                                var ev = new SpriteStudioAudio
                                {
                                    PartIndex = audio.PartIndex,
                                    FrameNo = events.FrameIndex,
                                    PartName = PartNameAt(audio.PartIndex),
                                    SoundListNameHash = val.SoundListNameHash,
                                    SoundNameHash = val.SoundNameHash,
                                    SoundName = val.SoundName,
                                    LoopNum = val.LoopNum
                                };
                                OnAudio.Invoke(ev);
                            }
                        }
                    }
                }
            }
            }
            finally
            {
                _signalParamCount = paramMark;
            }
        }

        // Append one decoded parameter to the grow-only signal-param buffer (see _signalParamBuffer).
        // Struct copy only; the buffer allocates once and grows rarely, so steady state is zero-GC.
        private void AppendSignalParam(in SpriteStudioSignalParam param)
        {
            if (_signalParamBuffer == null)
                _signalParamBuffer = new SpriteStudioSignalParam[8];
            else if (_signalParamCount >= _signalParamBuffer.Length)
                System.Array.Resize(ref _signalParamBuffer, _signalParamBuffer.Length * 2);
            _signalParamBuffer[_signalParamCount++] = param;
        }


        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            System.GC.SuppressFinalize(this);

            // [CRITICAL] Just before freeing native pointers or disposing NativeArrays, always
            // synchronize (wait for completion of) any running Burst Job to prevent a segfault.
            try { _geometryBuilder?.CurrentUpdateJobHandle.Complete(); }
            catch (Exception e) { Debug.LogException(e); }

            // The release groups below are independent of each other. Isolate each so that if one throws
            // the rest are still guaranteed to be released (native handles have no finalizer-side reclaim
            // path, so leaking here is a permanent leak).
            //
            // Order matters: destroy handles derived from the native resource/runtime (effect slots
            // created from the resource, instance children that hold their own contexts) BEFORE the
            // native context that owns the resource/runtime, so a derived handle is never freed after
            // its backing resource. LoadSsab applies the same ordering on a reload.
            try { ReleaseDerivedHandles(); }
            catch (Exception e) { Debug.LogException(e); }

            try { _nativeContext?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }

            try { _geometryBuilder?.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // Read the effect draw_plan (native FlatBuffers) in the same manner as frame-data, and

        public void PrepareFrameData(float deltaTime, bool advance, out System.IntPtr handle, out float drawFrame) => _geometryBuilder.PrepareFrameData(deltaTime, advance, out handle, out drawFrame);
        public void ScheduleMeshUpdate(float deltaTime) => _geometryBuilder.ScheduleMeshUpdate(deltaTime);
        public void ConsumeFrameData(System.IntPtr frameDataPtrRaw, int len, float deltaTime) => _geometryBuilder.ConsumeFrameData(frameDataPtrRaw, len, deltaTime);
        public void ApplyToMesh(UnityEngine.Mesh targetMesh, bool perBatchSubmesh = false, bool use16BitIndices = false) => _geometryBuilder.ApplyToMesh(targetMesh, perBatchSubmesh, use16BitIndices);
    }
}

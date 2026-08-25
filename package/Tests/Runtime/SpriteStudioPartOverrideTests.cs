using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Verifies the per-part Override Layer (colour / cell / visibility) exposed on
    /// SpriteStudioPlayer / SpriteStudioGraphic and the internal player.
    ///
    /// Two layers, as elsewhere in these tests:
    ///   (A) Component layer — the delegation contract: an unknown part / a bad blend op / a bad
    ///       priority / an unresolvable cell all return false, a valid call returns true.
    ///   (B) Internal-player layer — the override actually lands in the resolved pose. The runtime
    ///       applies overrides after the source mix, so the assertions read the frame data back
    ///       (hide flag / resolved part colour / resolved cell identity) after a manual tick + build.
    ///       This is also where the priority modes are pinned down: HoldUntilNextAnimation is dropped
    ///       by a new animation, Permanent survives it.
    ///
    /// The fixture reuses the existing Ringo.
    /// </summary>
    public class SpriteStudioPartOverrideTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _ringo;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator Setup()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
            {
                _ringo = asset.SsabData;
            }
            else
            {
                string p = Path.GetFullPath(RingoSsabPath);
                _ringo = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            foreach (var a in _assets) if (a != null) Object.DestroyImmediate(a);
            _assets.Clear();
        }

        // --- helpers ---

        private SpriteStudioAnimeAsset MakeAsset()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = _ringo;
            _assets.Add(asset);
            return asset;
        }

        private SpriteStudioPlayer MakePlayingPlayer(string anim = "walk")
        {
            var go = new GameObject("PartOverridePlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = MakeAsset();
            player.SetAnimation(anim);
            player.Play();
            return player;
        }

        // An internal player set up on "walk" with its first frame resolved. The frame is never advanced:
        // every assertion below rebuilds the same frame, so the only thing that changes between builds is
        // the override table (which also pins down that an override shows up on a static / paused frame —
        // the frame-skip cache has to be invalidated for that).
        private SpriteStudioInternalPlayer MakeInternalPlayer()
        {
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");
            var player = new SpriteStudioInternalPlayer();
            player.LoadSsab(_ringo);
            Assert.IsTrue(player.SetupAnimation("walk"), "The walk animation should be found");
            player.Play();
            Build(player);
            return player;
        }

        // Rebuild the pose at the current frame (ScheduleMeshUpdate does not advance).
        private static void Build(SpriteStudioInternalPlayer player)
        {
            player.ScheduleMeshUpdate(1f / 60f);
            player.CurrentUpdateJobHandle.Complete();
        }

        // Local UVs of a part on the current frame (a part that draws no cell keeps an all-zero block).
        private static float[] ReadUvs(SpriteStudioInternalPlayer player, int partIndex)
        {
            var uvs = new float[SpriteStudioInternalPlayer.LocalUvStride];
            Assert.IsTrue(player.TryGetPartUvs(partIndex, uvs), $"part {partIndex} should have a UV block");
            return uvs;
        }

        private static bool IsZero(float[] uvs)
        {
            foreach (var f in uvs) if (f != 0f) return false;
            return true;
        }

        // First visible part that draws a cell and is not a mask writer: a Normal part, i.e. one that
        // accepts colour as well as cell overrides (the runtime silently skips the other part types).
        private static int FindDrawingPart(SpriteStudioInternalPlayer player)
        {
            for (int i = 0; player.TryGetPartState(i, out bool hidden, out _, out _); i++)
            {
                if (hidden) continue;
                if (player._maskPartData != null && player._maskPartData[i].Writes) continue;
                var uvs = new float[SpriteStudioInternalPlayer.LocalUvStride];
                if (player.TryGetPartUvs(i, uvs) && !IsZero(uvs)) return i;
            }
            return -1;
        }

        // A cell whose name differs from every cell the animation could be showing — any second cell in
        // the first cellmap does, since we only need the swap to be observable in the UVs.
        private static bool TryFindOtherCell(SpriteStudioInternalPlayer player, int partIndex,
            float[] currentUvs, out string cellmapName, out string cellName)
        {
            cellmapName = null;
            cellName = null;
            var probe = new float[SpriteStudioInternalPlayer.LocalUvStride];

            foreach (var cm in player.GetCellmapNames())
            {
                foreach (var c in player.GetCellNames(cm))
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    if (!player.SetPartCellOverride(partIndex, cm, c, SpriteStudioOverridePriority.HoldUntilNextAnimation)) continue;
                    Build(player);
                    if (player.TryGetPartUvs(partIndex, probe) && !UvsEqual(probe, currentUvs))
                    {
                        player.ClearPartCellOverride(partIndex);
                        Build(player);
                        cellmapName = cm;
                        cellName = c;
                        return true;
                    }
                }
            }
            player.ClearPartCellOverride(partIndex);
            Build(player);
            return false;
        }

        private static bool UvsEqual(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++) if (!Mathf.Approximately(a[i], b[i])) return false;
            return true;
        }

        // The internal player behind a component. The components hold their playback core in a private
        // field, so the only way to read the resolved pose back through the *component* entry points is
        // to reach it here. Used by the component-layer lifecycle tests below, which have to observe the
        // override table rather than just a bool return value.
        private static SpriteStudioInternalPlayer InternalOf(object component)
        {
            var field = component.GetType().GetField("_core", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "the component should hold its playback core in a '_core' field");
            var core = field.GetValue(component) as SpriteStudioPlaybackCore;
            Assert.IsNotNull(core, "the '_core' field should hold a SpriteStudioPlaybackCore");
            Assert.IsNotNull(core.Internal, "the core should have created its internal player");
            return core.Internal;
        }

        // ============================================================
        // (A) Component layer: the delegation / validation contract
        // ============================================================

        [Test]
        public void FindPartIndex_ResolvesKnownPart_AndRejectsUnknown()
        {
            var player = MakePlayingPlayer();
            Assert.GreaterOrEqual(player.FindPartIndex("body"), 0, "'body' should resolve in Ringo");
            Assert.AreEqual(-1, player.FindPartIndex("no_such_part"));
            Assert.AreEqual(-1, player.FindPartIndex(""));
            Assert.AreEqual(-1, player.FindPartIndex(null));
        }

        [Test]
        public void Overrides_OnUnknownPart_ReturnFalse()
        {
            var player = MakePlayingPlayer();
            Assert.IsFalse(player.SetPartVisibilityOverride("no_such_part", true));
            Assert.IsFalse(player.SetPartColorOverride("no_such_part", Color.red, SpriteStudioBlendOp.Mix));
            Assert.IsFalse(player.SetPartCellOverride("no_such_part", "Ringo", "body"));
            Assert.IsFalse(player.SetPartVisibilityOverride(9999, true), "An out-of-range index is rejected by the runtime");
            Assert.IsFalse(player.SetPartVisibilityOverride(-1, true));
        }

        [Test]
        public void ColorOverride_AcceptsEveryBlendOp_AndRejectsInvalidEnumValues()
        {
            var player = MakePlayingPlayer();
            int idx = player.FindPartIndex("body");
            Assume.That(idx, Is.GreaterThanOrEqualTo(0));

            foreach (var op in new[] { SpriteStudioBlendOp.Mix, SpriteStudioBlendOp.Mul, SpriteStudioBlendOp.Add, SpriteStudioBlendOp.Sub })
            {
                Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, op, SpriteStudioOverridePriority.Permanent), $"blend op {op} should be accepted");
            }
            Assert.IsTrue(player.ClearPartColorOverride(idx));

            // Values outside the enum are rejected by the runtime, not silently coerced.
            Assert.IsFalse(player.SetPartColorOverride(idx, Color.red, (SpriteStudioBlendOp)99));
            Assert.IsFalse(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mix, (SpriteStudioOverridePriority)99));
        }

        [Test]
        public void ColorOverride_Corners_And_ClearAll_Succeed()
        {
            var player = MakePlayingPlayer();
            int idx = player.FindPartIndex("body");
            Assume.That(idx, Is.GreaterThanOrEqualTo(0));

            Assert.IsTrue(player.SetPartColorOverrideCorners(idx,
                new SpriteStudioCornerColors(Color.red, Color.green, Color.blue, Color.white),
                SpriteStudioBlendOp.Mix,
                SpriteStudioOverridePriority.HoldUntilNextAnimation));
            Assert.IsTrue(player.ClearAllPartOverrides());
        }

        [Test]
        public void CellOverride_UnknownCell_Fails()
        {
            var player = MakePlayingPlayer();
            int idx = player.FindPartIndex("body");
            Assume.That(idx, Is.GreaterThanOrEqualTo(0));

            // The cell is resolved at set time, so a name that is not in the asset fails immediately.
            Assert.IsFalse(player.SetPartCellOverride(idx, "no_such_cellmap", "no_such_cell"));
        }

        [Test]
        public void CellNames_AreDiscoverable()
        {
            var player = MakePlayingPlayer();
            var cellmaps = player.GetCellmapNames();
            Assert.Greater(cellmaps.Length, 0, "Ringo should expose at least one cellmap");
            Assert.Greater(player.GetCellNames(cellmaps[0]).Length, 0, "The cellmap should expose its cells");
            Assert.AreEqual(0, player.GetCellNames("no_such_cellmap").Length);
        }

        // ============================================================
        // (B) Internal-player layer: the override lands in the pose
        // ============================================================

        [Test]
        public void VisibilityOverride_HidesPart_AndClearReverts()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0), "Ringo should have a drawing part");
                Assert.IsTrue(player.TryGetPartState(idx, out bool hiddenBefore, out _, out _));
                Assume.That(hiddenBefore, Is.False, "The part must start visible for this test to mean anything");

                Assert.IsTrue(player.SetPartVisibilityOverride(idx, true));
                Build(player);
                Assert.IsTrue(player.TryGetPartState(idx, out bool hidden, out _, out _));
                Assert.IsTrue(hidden, "A force-hide override should hide the part");

                Assert.IsTrue(player.ClearPartVisibilityOverride(idx));
                Build(player);
                Assert.IsTrue(player.TryGetPartState(idx, out bool hiddenAfter, out _, out _));
                Assert.IsFalse(hiddenAfter, "Clearing the override should revert the part to the animation");
            }
        }

        [Test]
        public void VisibilityOverride_Cascade_HidesDescendants()
        {
            using (var player = MakeInternalPlayer())
            {
                // "root" is the top of Ringo's hierarchy, so a cascading force-hide there must hide
                // every drawing part below it (a keyframed hide, by contrast, does not cascade).
                int root = player.FindPartIndex("root");
                Assume.That(root, Is.GreaterThanOrEqualTo(0), "Ringo should have a 'root' part");
                int child = FindDrawingPart(player);
                Assume.That(child, Is.GreaterThanOrEqualTo(0));
                Assume.That(child, Is.Not.EqualTo(root));

                Assert.IsTrue(player.SetPartVisibilityOverride(root, true, cascade: true));
                Build(player);
                Assert.IsTrue(player.TryGetPartState(child, out bool hidden, out _, out _));
                Assert.IsTrue(hidden, "A cascading force-hide on the root should hide its descendants");

                // Without cascade the descendant is left to the animation.
                Assert.IsTrue(player.SetPartVisibilityOverride(root, true, cascade: false));
                Build(player);
                Assert.IsTrue(player.TryGetPartState(child, out bool hiddenNoCascade, out _, out _));
                Assert.IsFalse(hiddenNoCascade, "A non-cascading force-hide should not touch descendants");
            }
        }

        [Test]
        public void VisibilityOverride_IsClearedByNewAnimation()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                Assert.IsTrue(player.SetPartVisibilityOverride(idx, true));
                Build(player);
                Assert.IsTrue(player.TryGetPartState(idx, out bool hidden, out _, out _));
                Assume.That(hidden, Is.True);

                // Visibility overrides have no priority mode: a new animation always drops them.
                Assert.IsTrue(player.SetupAnimation("walk"));
                player.Play();
                Build(player);
                Assert.IsTrue(player.TryGetPartState(idx, out bool hiddenAfter, out _, out _));
                Assert.IsFalse(hiddenAfter, "Setting up an animation should clear visibility overrides");
            }
        }

        [Test]
        public void ColorOverride_LandsInResolvedPose_WithoutTouchingRgb()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                    SpriteStudioOverridePriority.HoldUntilNextAnimation));
                Build(player);

                Assert.IsTrue(player.TryGetPartColor(idx, out var color), "The overridden part should carry a colour");
                Assert.AreEqual(ss.runtime.BlendType.Mul, color.BlendType, "The override's blend op should reach the pose");
                // The runtime pre-multiplies alpha only; RGB is passed through as authored (8-bit sRGB).
                Assert.AreEqual(255, color.Lt.Rgba.R);
                Assert.AreEqual(0, color.Lt.Rgba.G);
                Assert.AreEqual(0, color.Lt.Rgba.B);
            }
        }

        [Test]
        public void ColorOverride_Corners_LandInTheirOwnCorners()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                Assert.IsTrue(player.SetPartColorOverrideCorners(idx,
                    new SpriteStudioCornerColors(Color.red, Color.green, Color.blue, Color.white),
                    SpriteStudioBlendOp.Mix,
                    SpriteStudioOverridePriority.HoldUntilNextAnimation));
                Build(player);

                Assert.IsTrue(player.TryGetPartColor(idx, out var color));
                Assert.AreEqual(255, color.Lt.Rgba.R, "left-top = red");
                Assert.AreEqual(255, color.Rt.Rgba.G, "right-top = green");
                Assert.AreEqual(255, color.Lb.Rgba.B, "left-bottom = blue");
                Assert.AreEqual(255, color.Rb.Rgba.R, "right-bottom = white");
                Assert.AreEqual(255, color.Rb.Rgba.G);
                Assert.AreEqual(255, color.Rb.Rgba.B);
            }
        }

        [Test]
        public void ColorOverride_HoldUntilNextAnimation_IsClearedBySetupAnimation()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                    SpriteStudioOverridePriority.HoldUntilNextAnimation));
                Build(player);
                Assume.That(player.TryGetPartColor(idx, out var applied) && applied.BlendType == ss.runtime.BlendType.Mul, Is.True);

                Assert.IsTrue(player.SetupAnimation("walk"));
                player.Play();
                Build(player);

                bool stillThere = player.TryGetPartColor(idx, out var after)
                                  && after.BlendType == ss.runtime.BlendType.Mul
                                  && after.Lt.Rgba.R == 255 && after.Lt.Rgba.G == 0 && after.Lt.Rgba.B == 0;
                Assert.IsFalse(stillThere, "HoldUntilNextAnimation should not survive a new animation setup");
            }
        }

        [Test]
        public void ColorOverride_Permanent_SurvivesSetupAnimation()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                    SpriteStudioOverridePriority.Permanent));

                Assert.IsTrue(player.SetupAnimation("walk"));
                player.Play();
                Build(player);

                Assert.IsTrue(player.TryGetPartColor(idx, out var color), "A Permanent override should still colour the part");
                Assert.AreEqual(ss.runtime.BlendType.Mul, color.BlendType);
                Assert.AreEqual(255, color.Lt.Rgba.R);
                Assert.AreEqual(0, color.Lt.Rgba.G);
            }
        }

        // ============================================================
        // (C) Component layer: switching animation must not re-bind the resource
        // ============================================================
        //
        // Priority handling lives in the runtime and is covered by (B). What can only break above it is
        // the *route*: binding a resource is what tells the runtime "new SSAB, part identity changed",
        // and it drops the whole override table — Permanent included. So an animation switch must go
        // through setup only; the ssab is (re)loaded on an asset swap and nowhere else. These two pin
        // that down from the public entry points, where a stray reload would be introduced.

        [Test]
        public void ColorOverride_Permanent_SurvivesComponentAnimationSwitch()
        {
            var player = MakePlayingPlayer("walk");
            var internalPlayer = InternalOf(player);

            // The component does not build geometry until the update manager ticks it, and the part
            // probes below read the resolved frame — so resolve the first frame by hand.
            Build(internalPlayer);
            int idx = FindDrawingPart(internalPlayer);
            Assume.That(idx, Is.GreaterThanOrEqualTo(0), "Ringo should have a drawing part");

            Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                SpriteStudioOverridePriority.Permanent));
            Build(internalPlayer);
            Assume.That(internalPlayer.TryGetPartColor(idx, out var applied) && applied.BlendType == ss.runtime.BlendType.Mul, Is.True,
                "the override has to land before the switch for the assertion after it to mean anything");

            // The switch the user makes: a different animation inside the same asset.
            Assert.IsTrue(player.SetAnimation("run"), "Ringo should have a 'run' animation");
            player.Play();
            Build(internalPlayer);

            Assert.IsTrue(internalPlayer.TryGetPartColor(idx, out var after),
                "A Permanent override should still colour the part after switching animation through the component");
            Assert.AreEqual(ss.runtime.BlendType.Mul, after.BlendType);
            Assert.AreEqual(255, after.Lt.Rgba.R);
            Assert.AreEqual(0, after.Lt.Rgba.G);
            Assert.AreEqual(0, after.Lt.Rgba.B);
        }

        [Test]
        public void ColorOverride_Permanent_IsDroppedByAssetSwap()
        {
            var player = MakePlayingPlayer("walk");
            var internalPlayer = InternalOf(player);

            Build(internalPlayer);
            int idx = FindDrawingPart(internalPlayer);
            Assume.That(idx, Is.GreaterThanOrEqualTo(0));

            Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                SpriteStudioOverridePriority.Permanent));
            Build(internalPlayer);
            Assume.That(internalPlayer.TryGetPartColor(idx, out var applied) && applied.BlendType == ss.runtime.BlendType.Mul, Is.True);

            // A new asset is a new part identity: the runtime drops every override, Permanent included.
            // The player object is reused, so this is the one route that legitimately clears the table.
            player.AnimeAsset = MakeAsset();
            Assert.IsTrue(player.SetAnimation("walk"));
            player.Play();
            var rebound = InternalOf(player);
            Build(rebound);

            bool stillThere = rebound.TryGetPartColor(idx, out var after)
                              && after.BlendType == ss.runtime.BlendType.Mul
                              && after.Lt.Rgba.R == 255 && after.Lt.Rgba.G == 0 && after.Lt.Rgba.B == 0;
            Assert.IsFalse(stillThere, "Swapping the asset should drop even a Permanent override");
        }

        [Test]
        public void ColorOverride_Permanent_SurvivesGraphicAnimationSwitch()
        {
            var go = new GameObject("PartOverrideGraphic");
            _objects.Add(go);
            go.AddComponent<Canvas>();
            go.AddComponent<CanvasRenderer>();
            var graphic = go.AddComponent<SpriteStudioGraphic>();
            graphic.AnimeAsset = MakeAsset();
            Assert.IsTrue(graphic.SetAnimation("walk"));
            graphic.Play();

            var internalPlayer = InternalOf(graphic);
            Build(internalPlayer);
            int idx = FindDrawingPart(internalPlayer);
            Assume.That(idx, Is.GreaterThanOrEqualTo(0));

            Assert.IsTrue(graphic.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                SpriteStudioOverridePriority.Permanent));
            Build(internalPlayer);
            Assume.That(internalPlayer.TryGetPartColor(idx, out var applied) && applied.BlendType == ss.runtime.BlendType.Mul, Is.True);

            Assert.IsTrue(graphic.SetAnimation("run"));
            graphic.Play();
            Build(internalPlayer);

            Assert.IsTrue(internalPlayer.TryGetPartColor(idx, out var after),
                "A Permanent override should survive an animation switch on the Canvas path too");
            Assert.AreEqual(ss.runtime.BlendType.Mul, after.BlendType);
            Assert.AreEqual(255, after.Lt.Rgba.R);
            Assert.AreEqual(0, after.Lt.Rgba.G);
        }

        [Test]
        public void CellOverride_SwapsTheDrawnCell_AndClearReverts()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                // The swap is observed through the part's UVs: the runtime re-resolves the cell's UV meta
                // at the seam, so the sampled image (not just the quad) follows the override.
                float[] uvsBefore = ReadUvs(player, idx);
                Assume.That(TryFindOtherCell(player, idx, uvsBefore, out string cellmapName, out string cellName), Is.True,
                    "Ringo should have a cell that draws differently from the one the animation shows");

                Assert.IsTrue(player.SetPartCellOverride(idx, cellmapName, cellName,
                    SpriteStudioOverridePriority.HoldUntilNextAnimation));
                Build(player);
                Assert.IsFalse(UvsEqual(ReadUvs(player, idx), uvsBefore), "The overridden cell should change the sampled UVs");

                Assert.IsTrue(player.ClearPartCellOverride(idx));
                Build(player);
                Assert.IsTrue(UvsEqual(ReadUvs(player, idx), uvsBefore), "Clearing the override should revert the part to the animation's cell");
            }
        }

        [Test]
        public void ClearAllPartOverrides_DropsEverything()
        {
            using (var player = MakeInternalPlayer())
            {
                int idx = FindDrawingPart(player);
                Assume.That(idx, Is.GreaterThanOrEqualTo(0));

                Assert.IsTrue(player.SetPartVisibilityOverride(idx, true));
                Assert.IsTrue(player.SetPartColorOverride(idx, Color.red, SpriteStudioBlendOp.Mul,
                    SpriteStudioOverridePriority.Permanent));
                Build(player);
                Assume.That(player.TryGetPartState(idx, out bool hidden, out _, out _) && hidden, Is.True);

                Assert.IsTrue(player.ClearAllPartOverrides());
                Build(player);

                Assert.IsTrue(player.TryGetPartState(idx, out bool hiddenAfter, out _, out _));
                Assert.IsFalse(hiddenAfter, "clear_all should drop the visibility override");
                bool colorStillThere = player.TryGetPartColor(idx, out var color)
                                       && color.BlendType == ss.runtime.BlendType.Mul
                                       && color.Lt.Rgba.R == 255 && color.Lt.Rgba.G == 0;
                Assert.IsFalse(colorStillThere, "clear_all should drop even a Permanent colour override");
            }
        }
    }
}

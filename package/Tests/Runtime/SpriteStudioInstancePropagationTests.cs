using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// End-to-end guard for masking that crosses an instance boundary (SS7.5 InstancePropagation).
    ///
    /// A clipping mask written inside an instance child must keep clipping the parent animation's parts
    /// drawn after the instance - the child's restore is carried past the instance boundary to the parent's
    /// own restore at the very end, not applied at the boundary (see SpriteStudioGeometryBuilder's deferred
    /// closes). And a mask writer's stencil plane follows its EFFECTIVE maskInfluence (own AND the one of
    /// the instance part directly above it): a mask inside a maskInfluence=0 instance moves to the counter
    /// plane so it composes with an outer mask as a union instead of cancelling it on the shared parity
    /// plane (see <see cref="SpriteStudioInternalPlayer.EffectiveMaskInfluence"/>).
    ///
    /// Both are asserted on the real composite submesh list built from the SDK's "overall" InstancePropagation
    /// animation, with its Source01 sub-animation supplied through the instance resolver (as
    /// <see cref="SpriteStudioInstanceCycleTests"/> does). Like the other overall-based tests, the .ssab
    /// fixtures are not committed: they are generated on demand from the SDK submodule in the Editor, so a
    /// clean checkout stays free of test binaries. Outside the Editor (no generated fixture) the test skips.
    /// </summary>
    public class SpriteStudioInstancePropagationTests
    {
        private const string OverallSsabDir = "../../package/Tests/Runtime/Resources/overall";
        private const string ParentSsabPath = OverallSsabDir + "/InstancePropagation.ssab";
        private const string ChildSsabPath = OverallSsabDir + "/InstancePropagation_Source01.ssab";
        private const string ParentAssetPath = "overall/InstancePropagation";                 // for Resources.Load
        private const string ChildAssetPath = "overall/InstancePropagation_Source01";
        private const string OverallSspjPath = "../../SpriteStudio-SDK/tests/overall/overall.sspj";

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // Already importable (Standalone build, or a cached Editor import)?
            var asset = Resources.Load<SpriteStudioAnimeAsset>(ParentAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
                yield break;

            string absoluteParentPath = Path.GetFullPath(ParentSsabPath);
            if (!File.Exists(absoluteParentPath))
            {
#if UNITY_EDITOR
                // Convert the whole "overall" project (which includes InstancePropagation + its Source01).
                string sspjPath = Path.GetFullPath(OverallSspjPath);
                string outDir = Path.GetFullPath(OverallSsabDir);
                if (!File.Exists(sspjPath)) yield break; // SDK submodule not checked out; test body will skip.
                Directory.CreateDirectory(outDir);

                var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
                runnerType?.GetMethod("Enqueue", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] { sspjPath, outDir });

                // Drive the conversion queue until InstancePropagation.ssab appears (or time out).
                float timeout = 30f;
                var pollMethod = runnerType?.GetMethod("Poll", BindingFlags.NonPublic | BindingFlags.Static);
                while (!File.Exists(absoluteParentPath) && timeout > 0f)
                {
                    pollMethod?.Invoke(null, null);
                    yield return null;
                    timeout -= Time.deltaTime;
                }

                var assetDatabaseType = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                assetDatabaseType?.GetMethod("Refresh", new System.Type[0])?.Invoke(null, null);
#endif
            }
            yield return null;
        }

        [Test]
        public void InstanceInternalClippingMask_CarriesPastTheBoundary_AndComposesAsUnion()
        {
            byte[] parent = TryGetSsab(ParentAssetPath, ParentSsabPath);
            byte[] child = TryGetSsab(ChildAssetPath, ChildSsabPath);
            if (parent == null || child == null)
            {
                Assert.Ignore("InstancePropagation fixtures unavailable (uncommitted, SDK 'overall' source not generated) - skipping the cross-instance mask guard.");
            }

            using (var player = new SpriteStudioInternalPlayer())
            {
                // The parent references only Source01 as its instance child, so the resolver can hand it
                // back for every request (mirrors SpriteStudioInstanceCycleTests).
                player.LoadSsab(parent, (packHash, nameHash) => child);
                Assert.IsTrue(player.HasSsab, "the parent ssab must load");
                Assert.Greater(player._instancePartCount, 0,
                    "InstancePropagation must mount its instance children (resolver returned the Source01 sub-animation)");

                bool found = StepUntilCarriedClose(player, out int writeCount, out int deferrableCount, out int counterPlaneWrites);

                Assert.IsTrue(found,
                    "no frame produced an instance-carried clipping restore - the fixture never exercised the cross-instance mask path");
                Assert.Greater(writeCount, 0, "the carry-over frame must contain stencil writers");
                Assert.Greater(deferrableCount, 0,
                    "an instance child's clipping restore must appear in the parent composite as a carried (deferrable) close, " +
                    "not be closed at the instance boundary");
                Assert.Greater(counterPlaneWrites, 0,
                    "a mask inside a maskInfluence=0 instance must write on the counter plane (Incr/Decr), so it composes " +
                    "with an outer mask as a union rather than cancelling it on the parity plane");
            }
        }

        /// <summary>
        /// Steps every animation frame by frame until one builds a composite that carries an instance
        /// child's clipping restore (a Write submesh tagged DeferrableClose), then reports that frame's
        /// writer counts. Returns false if none did.
        /// </summary>
        private static bool StepUntilCarriedClose(SpriteStudioInternalPlayer player,
            out int writes, out int deferrable, out int counter)
        {
            writes = deferrable = counter = 0;
            int animCount = player.GetAnimationCount();
            for (int a = 0; a < animCount; a++)
            {
                string name = player.GetAnimationName(a);
                if (string.IsNullOrEmpty(name) || !player.SetupAnimation(name)) continue;
                player.Play(0f);

                int start = player.GetAnimationSectionStart();
                int end = player.GetAnimationSectionEnd();
                if (end < start) end = start;
                for (int f = start; f <= end; f++)
                {
                    player.SetFrame(f);
                    player.ScheduleMeshUpdate(f == start ? 0f : 1f / 60f);
                    player.CurrentUpdateJobHandle.Complete();

                    CountWriters(player, out int w, out int d, out int c);
                    if (d > 0)
                    {
                        writes = w; deferrable = d; counter = c;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Counts the current composite's stencil writers: total, carried (deferrable) closes, and counter-plane ops.</summary>
        private static void CountWriters(SpriteStudioInternalPlayer player, out int writes, out int deferrable, out int counter)
        {
            writes = deferrable = counter = 0;
            int n = player.SubmeshCount;
            if (n <= 0) return;
            NativeArray<SubmeshInfo> subs = player.Submeshes;
            for (int s = 0; s < n; s++)
            {
                var info = subs[s];
                if (info.Role != StencilRole.Write) continue;
                writes++;
                if (info.DeferrableClose) deferrable++;
                if (info.StencilOp == (byte)StencilOp.IncrementWrap || info.StencilOp == (byte)StencilOp.DecrementWrap)
                    counter++;
            }
        }

        /// <summary>Prefer the imported asset's bytes; fall back to reading the generated .ssab from disk. Null when unavailable.</summary>
        private static byte[] TryGetSsab(string resourcePath, string filePath)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(resourcePath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0) return asset.SsabData;

            string p = Path.GetFullPath(filePath);
            if (File.Exists(p))
            {
                var bytes = File.ReadAllBytes(p);
                if (bytes != null && bytes.Length > 0) return bytes;
            }
            return null;
        }
    }
}

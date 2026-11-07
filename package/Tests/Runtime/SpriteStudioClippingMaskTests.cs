using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;
using SpriteStudio.Runtime;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Guards the clipping-mask stencil restore (ConsumeFrameData's Phase C).
    ///
    /// A clipping mask (MaskWrite &amp;&amp; !PureMask) writes its isolated stencil bits at its own rank to
    /// clip the parts drawn after it. Without an inverse write at the end of the draw, its bits would stay
    /// set past the object's draw and leak into other SpriteStudio objects sharing the stencil buffer that
    /// frame.
    ///
    /// Invariant asserted: every stencil writer (pure or clipping) contributes exactly TWO Write-role
    /// submeshes - a write and its restore - so <c>writeSubmeshCount == 2 * writerCount</c>.
    /// Exercised with the real Mask animation from the SDK's "overall" test project,
    /// which contains 2 clipping writers.
    ///
    /// Note on why a simpler op-parity check is insufficient: those clipping writers use the Invert op
    /// (they are also mask targets) and invert two DIFFERENT regions. Dropping both restores leaves the
    /// total Invert count even, yet the stencil is left non-zero in each region. Counting matched pairs
    /// per writer catches this; counting op parity does not.
    ///
    /// Like <see cref="SpriteStudioRingoPlaybackTests"/>, the .ssab fixture is not committed: it is
    /// generated on demand from the SDK submodule source (overall.sspj) in the Editor, so a clean checkout
    /// stays free of test binaries. Outside the Editor (no generated fixture) the test skips.
    /// </summary>
    public class SpriteStudioClippingMaskTests
    {
        private const string OverallSsabDir = "../../package/Tests/Runtime/Resources/overall";
        private const string MaskSsabPath = OverallSsabDir + "/Mask.ssab";
        private const string MaskAssetPath = "overall/Mask"; // for Resources.Load
        private const string OverallSspjPath = "../../SpriteStudio-SDK/tests/overall/overall.sspj";

        [UnitySetUp]
        public IEnumerator Setup()
        {
            // Already importable (Standalone build, or a cached Editor import)?
            var asset = Resources.Load<SpriteStudioAnimeAsset>(MaskAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0)
                yield break;

            string absoluteSsabPath = Path.GetFullPath(MaskSsabPath);
            if (!File.Exists(absoluteSsabPath))
            {
#if UNITY_EDITOR
                // Convert the whole "overall" project (which includes the Mask animation) from the SDK source.
                string sspjPath = Path.GetFullPath(OverallSspjPath);
                string outDir = Path.GetFullPath(OverallSsabDir);
                if (!File.Exists(sspjPath)) yield break; // SDK submodule not checked out; test body will skip.
                Directory.CreateDirectory(outDir);

                var runnerType = System.Type.GetType("SpriteStudio.Editor.SpriteStudioConversionRunner, SpriteStudio.Editor");
                runnerType?.GetMethod("Enqueue", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] { sspjPath, outDir });

                // Drive the conversion queue until Mask.ssab appears (or time out).
                float timeout = 30f;
                var pollMethod = runnerType?.GetMethod("Poll", BindingFlags.NonPublic | BindingFlags.Static);
                while ((!File.Exists(absoluteSsabPath) || !ConversionRunnerProbe.IsIdle(runnerType)) && timeout > 0f)
                {
                    pollMethod?.Invoke(null, null);
                    yield return null;
                    timeout -= Time.deltaTime;
                }

                // Refresh so Resources.Load can see the freshly written asset.
                var assetDatabaseType = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
                assetDatabaseType?.GetMethod("Refresh", new System.Type[0])?.Invoke(null, null);
#endif
            }
            yield return null;
        }

        [Test]
        public void ClippingMask_EveryWriter_HasAMatchedRestore()
        {
            byte[] ssabBytes = TryGetMaskSsab(out var loadedAsset);
            if (ssabBytes == null)
            {
                Assert.Ignore("Mask.ssab is unavailable (uncommitted fixture and SDK 'overall' source not generated) - skipping clipping-mask guard.");
            }

            // Prefer the imported asset when present; otherwise wrap the raw bytes in a throwaway asset.
            bool createdAsset = loadedAsset == null;
            var asset = loadedAsset != null ? loadedAsset : ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            if (createdAsset) asset.SsabData = ssabBytes;

            var core = new SpriteStudioPlaybackCore();
            try
            {
                core.EnsureCreated(asset, false);
                var ip = core.Internal;
                Assert.IsNotNull(ip, "internal player was not created (FFI / ssab load failed)");

                int writeCount = FindFrameWithMaskWriters(core, ip);
                Assert.Greater(writeCount, 0,
                    "no mask Write submeshes were produced - cannot exercise the mask path");

                // Count static writers and clipping writers from the loaded mask data.
                int writerCount = 0, clippingCount = 0;
                var md = ip._maskPartData;
                Assert.IsNotNull(md, "mask part data was not populated");
                for (int i = 0; i < md.Length; i++)
                {
                    if (md[i].Writes) writerCount++;
                    if (md[i].IsClipping) clippingCount++;
                }

                Assert.Greater(clippingCount, 0,
                    "the Mask animation is expected to contain clipping writers; without them this test cannot guard the fix");

                // The fix makes every writer emit a write + restore pair. Before it, each clipping writer
                // emits only the write, so writeCount would be (2 * writerCount - clippingCount).
                Assert.AreEqual(2 * writerCount, writeCount,
                    $"Expected every one of the {writerCount} writers to emit a matched write+restore pair " +
                    $"(= {2 * writerCount} Write submeshes) but found {writeCount}. A shortfall of " +
                    $"{clippingCount} means the {clippingCount} clipping writer(s) left their stencil bits " +
                    "set past the draw (the Phase C restore is missing).");
            }
            finally
            {
                core.Dispose();
                if (createdAsset && asset != null) Object.DestroyImmediate(asset);
            }
        }

        /// <summary>
        /// Returns the Mask ssab bytes and, when available, the imported asset (so the caller does not
        /// destroy the shared Resources instance). Returns null bytes when the fixture is unavailable.
        /// </summary>
        private static byte[] TryGetMaskSsab(out SpriteStudioAnimeAsset importedAsset)
        {
            importedAsset = Resources.Load<SpriteStudioAnimeAsset>(MaskAssetPath);
            if (importedAsset != null && importedAsset.SsabData != null && importedAsset.SsabData.Length > 0)
                return importedAsset.SsabData;
            importedAsset = null;

            string absoluteSsabPath = Path.GetFullPath(MaskSsabPath);
            if (File.Exists(absoluteSsabPath))
            {
                var bytes = File.ReadAllBytes(absoluteSsabPath);
                if (bytes != null && bytes.Length > 0) return bytes;
            }
            return null;
        }

        /// <summary>
        /// Steps a handful of frames across every animation in the ssab until a frame emits stencil Write
        /// submeshes, then returns that frame's Write-role submesh count. Returns 0 if none produced any.
        /// Enumerates animations from the loaded ssab so it does not depend on the import-populated asset.
        /// </summary>
        private static int FindFrameWithMaskWriters(SpriteStudioPlaybackCore core, SpriteStudioInternalPlayer ip)
        {
            int animCount = ip.GetAnimationCount();
            for (int a = 0; a < animCount; a++)
            {
                string animName = ip.GetAnimationName(a);
                if (string.IsNullOrEmpty(animName) || !core.SetAnimation(animName, null)) continue;
                core.Play(0f);
                for (int f = 0; f < 8; f++)
                {
                    core.TickAndSchedule(f == 0 ? 0f : 1f / 60f, false);
                    ip.CurrentUpdateJobHandle.Complete();
                    int w = CountWrites(ip);
                    if (w > 0) return w;
                }
            }
            return 0;
        }

        /// <summary>Counts the current frame's Write-role submeshes (stencil writers).</summary>
        private static int CountWrites(SpriteStudioInternalPlayer ip)
        {
            int n = ip.SubmeshCount;
            if (n <= 0) return 0;
            NativeArray<SubmeshInfo> subs = ip.Submeshes;
            int w = 0;
            for (int s = 0; s < n; s++)
                if (subs[s].Role == StencilRole.Write) w++;
            return w;
        }
    }
}

using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using SpriteStudio.SsRuntime;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Guard for <see cref="SpriteStudioNativeContext"/> sharing one parsed resource among the contexts loaded
    /// from the same .ssab buffer: the players share it, each keeps playing when another lets go of it, the
    /// last one to let go frees it, and what one player sets on its runtime -- part overrides, blended
    /// tracks -- reaches no other player of the resource.
    /// </summary>
    public class SpriteStudioSharedResourceTests
    {
        private const string RingoAssetPath = "Ringo/Ringo"; // for Resources.Load
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private static byte[] RingoBytes()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0) return asset.SsabData;
            string path = Path.GetFullPath(RingoSsabPath);
            if (!File.Exists(path)) Assert.Ignore("Ringo.ssab is unavailable (converted from the SDK's tests/Ringo).");
            return File.ReadAllBytes(path);
        }

        private static SpriteStudioInternalPlayer Load(byte[] ssab)
        {
            var player = new SpriteStudioInternalPlayer();
            player.LoadSsab(ssab);
            Assert.IsTrue(player.SetupAnimation("walk"), "walk should be found");
            return player;
        }

        private static int BuiltVertices(SpriteStudioInternalPlayer player)
        {
            player.Play();
            player.Tick(1f / 60f, false);
            player.ScheduleMeshUpdate(1f / 60f);
            player.CurrentUpdateJobHandle.Complete();
            return player.VertexCount;
        }

        // First visible part that draws a cell and writes no mask: a Normal part, which takes colour overrides.
        private static int FindDrawingPart(SpriteStudioInternalPlayer player)
        {
            var uvs = new float[SpriteStudioInternalPlayer.LocalUvStride];
            for (int i = 0; player.TryGetPartState(i, out bool hidden, out _, out _); i++)
            {
                if (hidden || (player._maskPartData != null && player._maskPartData[i].Writes)) continue;
                if (!player.TryGetPartUvs(i, uvs)) continue;
                foreach (var f in uvs) if (f != 0f) return i;
            }
            return -1;
        }

        private static unsafe ulong Hash(SpriteStudioInternalPlayer player)
        {
            ulong h = 1469598103934665603UL;
            int count = player.VertexCount;
            if (count <= 0) return h;
            byte* b = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(player.MeshVertices);
            long length = (long)count * sizeof(SsVertex);
            for (long k = 0; k < length; k++) h = (h ^ b[k]) * 1099511628211UL;
            return h;
        }

        [Test]
        public unsafe void PlayersOfOneBuffer_ShareItsResource()
        {
            byte[] ssab = RingoBytes();
            int before = SpriteStudioNativeContext.SharedResourceCount;
            using (var a = Load(ssab))
            using (var b = Load(ssab))
            {
                Assert.IsTrue(a._nativeContext.ResourceHandle == b._nativeContext.ResourceHandle, "one parse for both");
                Assert.AreEqual(before + 1, SpriteStudioNativeContext.SharedResourceCount);
                Assert.Greater(BuiltVertices(a), 0);
                Assert.Greater(BuiltVertices(b), 0);
            }
            Assert.AreEqual(before, SpriteStudioNativeContext.SharedResourceCount, "the last player frees it");
        }

        [Test]
        public void APlayerLettingGo_LeavesTheOthersPlaying()
        {
            byte[] ssab = RingoBytes();
            using (var kept = Load(ssab))
            {
                var gone = Load(ssab);
                Assert.Greater(BuiltVertices(gone), 0);
                gone.Dispose();

                Assert.Greater(BuiltVertices(kept), 0, "the resource must outlive the player that let go");
            }
        }

        [Test]
        public void AnotherPlayerOfTheBuffer_LeavesThisPlayersOverrides()
        {
            byte[] ssab = RingoBytes();
            using (var player = Load(ssab))
            {
                Assert.Greater(BuiltVertices(player), 0);
                int part = FindDrawingPart(player);
                Assume.That(part, Is.GreaterThanOrEqualTo(0));
                Assert.IsTrue(player.SetPartColorOverride(part, Color.red, SpriteStudioBlendOp.Mul,
                    SpriteStudioOverridePriority.Permanent));

                // Another player binds the same resource, overrides the same part its own way, and goes.
                var other = Load(ssab);
                Assert.IsTrue(other.SetPartColorOverride(part, Color.blue, SpriteStudioBlendOp.Mix,
                    SpriteStudioOverridePriority.Permanent));
                BuiltVertices(other);
                Assert.IsTrue(other.TryGetPartColor(part, out var theirs));
                Assert.AreEqual(255, theirs.Lt.Rgba.B, "the other player draws its own override");
                other.Dispose();

                BuiltVertices(player);
                Assert.IsTrue(player.TryGetPartColor(part, out var ours), "the override lives on the player, not the resource");
                Assert.AreEqual(ss.runtime.BlendType.Mul, ours.BlendType);
                Assert.AreEqual(255, ours.Lt.Rgba.R);
                Assert.AreEqual(0, ours.Lt.Rgba.B);
            }
        }

        [Test]
        public unsafe void BlendingOnePlayer_LeavesTheOthersPose()
        {
            byte[] ssab = RingoBytes();
            using (var blended = Load(ssab))
            using (var plain = Load(ssab))
            using (var alone = Load((byte[])ssab.Clone())) // a resource of its own: the unshared pose
            {
                uint source = NativeMethods.ss_runtime_add_track_source_by_hash(
                    blended._handle.ptr, 0, SpriteStudioInternalPlayer.HashName("run"), 1f);
                Assume.That(source, Is.Not.EqualTo(uint.MaxValue), "run should mix into track 0");

                BuiltVertices(alone);
                BuiltVertices(plain);
                BuiltVertices(blended);
                ulong unshared = Hash(alone), shared = Hash(plain);
                Assert.AreNotEqual(unshared, Hash(blended), "the mix should change the pose it is applied to");
                Assert.AreEqual(unshared, shared, "a player sharing the resource draws as if it had its own");
            }
        }

        [Test]
        public unsafe void ADifferentBuffer_ParsesAfresh_AndReloadingLetsGoOfTheOld()
        {
            byte[] ssab = RingoBytes();
            byte[] copy = (byte[])ssab.Clone(); // what a reimport hands over: the same bytes in a new array
            int before = SpriteStudioNativeContext.SharedResourceCount;
            using (var a = Load(ssab))
            using (var b = Load(copy))
            {
                Assert.IsTrue(a._nativeContext.ResourceHandle != b._nativeContext.ResourceHandle, "keyed by the array, not its contents");
                Assert.AreEqual(before + 2, SpriteStudioNativeContext.SharedResourceCount);

                a.LoadSsab(copy);
                Assert.IsTrue(a.SetupAnimation("walk"));
                Assert.IsTrue(a._nativeContext.ResourceHandle == b._nativeContext.ResourceHandle);
                Assert.AreEqual(before + 1, SpriteStudioNativeContext.SharedResourceCount, "nothing uses the first array any more");
                Assert.Greater(BuiltVertices(a), 0);
            }
            Assert.AreEqual(before, SpriteStudioNativeContext.SharedResourceCount);
        }
    }
}

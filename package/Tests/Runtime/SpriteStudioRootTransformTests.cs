using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// FlipX / FlipY / Offset are folded into the vertex positions rather than onto the GameObject's
    /// Transform, so they have two consumers that must agree: the Burst mesh job, and
    /// <see cref="SpriteStudioInternalPlayer.TryGetPartTransform"/>, which is what drives part trackers.
    /// If those ever drift apart, a tracker silently detaches from the part it is pinned to the moment the
    /// player is flipped — which is the bug these tests exist to prevent.
    /// </summary>
    public class SpriteStudioRootTransformTests
    {
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";
        private const string RingoAssetPath = "Ringo/Ringo";
        private const string RingoAnimation = "walk";

        private static byte[] LoadRingoBytes()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(RingoAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0) return asset.SsabData;

            string absolute = Path.GetFullPath(RingoSsabPath);
            return File.Exists(absolute) ? File.ReadAllBytes(absolute) : null;
        }

        /// <summary>
        /// Builds one frame under the given root transform and copies out what the two consumers produced:
        /// the vertex positions, and one part's world matrix.
        /// </summary>
        /// <param name="samplePart">
        /// Which part to sample, or -1 to pick the first one that sits off the origin on both axes (a part at
        /// the origin would mirror onto itself and prove nothing). Comparison tests must resolve the index
        /// from the untransformed build and then pass it back in: a root transform moves parts across that
        /// "off the origin" threshold, so re-scanning per build can silently sample two different parts.
        /// </param>
        private static void BuildFrame(byte[] ssab, bool flipX, bool flipY, Vector2 offset, int samplePart,
            out Vector3[] positions, out Matrix4x4 partMatrix, out int partIndex)
        {
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(ssab);
                Assert.IsTrue(player.SetupAnimation(RingoAnimation), "the walk animation should be found");

                player.FlipX = flipX;
                player.FlipY = flipY;
                player.VertexOffset = offset;

                player.Play();
                player.SetFrame(0f);
                player.Tick(0.016f, false);
                player.ScheduleMeshUpdate(0.016f);
                player.CurrentUpdateJobHandle.Complete();

                int count = player.VertexCount;
                Assert.Greater(count, 0, "the frame should have produced vertices");
                var verts = player.MeshVertices;
                positions = new Vector3[count];
                for (int i = 0; i < count; i++) positions[i] = verts[i].Position;

                partIndex = -1;
                partMatrix = Matrix4x4.identity;
                if (samplePart >= 0)
                {
                    Assert.IsTrue(player.TryGetPartTransform(samplePart, out partMatrix),
                        $"part {samplePart} should resolve in every build");
                    partIndex = samplePart;
                }
                else
                {
                    for (int i = 0; i < 64; i++)
                    {
                        if (!player.TryGetPartTransform(i, out Matrix4x4 m)) continue;
                        if (Mathf.Abs(m.m03) > 1e-3f && Mathf.Abs(m.m13) > 1e-3f)
                        {
                            partIndex = i;
                            partMatrix = m;
                            break;
                        }
                    }
                }
            }
        }

        private static byte[] RequireRingo()
        {
            var ssab = LoadRingoBytes();
            if (ssab == null || ssab.Length == 0)
            {
                Assert.Ignore("Ringo.ssab missing — run the conversion (see SpriteStudioRingoPlaybackTests).");
            }
            return ssab;
        }

        [Test]
        public void RootTransform_DefaultsToIdentity()
        {
            var go = new GameObject("TestPlayer");
            try
            {
                var player = go.AddComponent<SpriteStudioPlayer>();
                Assert.IsFalse(player.FlipX);
                Assert.IsFalse(player.FlipY);
                Assert.AreEqual(Vector2.zero, player.Offset);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void RootScale_NegatesOnlyTheFlippedAxis()
        {
            using (var player = new SpriteStudioInternalPlayer())
            {
                player.VertexScale = 0.01f; // as PixelsPerUnit = 100

                Assert.AreEqual(new Vector2(0.01f, 0.01f), player.RootScale);

                player.FlipX = true;
                Assert.AreEqual(new Vector2(-0.01f, 0.01f), player.RootScale);

                player.FlipY = true;
                Assert.AreEqual(new Vector2(-0.01f, -0.01f), player.RootScale);

                player.FlipX = false;
                Assert.AreEqual(new Vector2(0.01f, -0.01f), player.RootScale);
            }
        }

        [Test]
        public void FlipX_MirrorsTheGeometryAndThePartMatrixTogether()
        {
            var ssab = RequireRingo();

            BuildFrame(ssab, false, false, Vector2.zero, -1, out var plain, out var plainPart, out int plainIdx);
            Assert.AreNotEqual(-1, plainIdx, "expected some part to sit off the origin");
            BuildFrame(ssab, true, false, Vector2.zero, plainIdx, out var flipped, out var flippedPart, out _);

            Assert.AreEqual(plain.Length, flipped.Length, "flipping must not change the vertex count");
            for (int i = 0; i < plain.Length; i++)
            {
                Assert.AreEqual(-plain[i].x, flipped[i].x, 1e-4f, $"vertex {i} x should mirror");
                Assert.AreEqual(plain[i].y, flipped[i].y, 1e-4f, $"vertex {i} y should be untouched");
            }

            // The tracker path has to mirror by exactly the same amount, or an attachment drifts off its part.
            Assert.AreEqual(-plainPart.m03, flippedPart.m03, 1e-4f, "part translation x should mirror");
            Assert.AreEqual(plainPart.m13, flippedPart.m13, 1e-4f, "part translation y should be untouched");
        }

        [Test]
        public void FlipY_MirrorsTheOtherAxis()
        {
            var ssab = RequireRingo();

            BuildFrame(ssab, false, false, Vector2.zero, -1, out var plain, out var plainPart, out int plainIdx);
            Assert.AreNotEqual(-1, plainIdx, "expected some part to sit off the origin");
            BuildFrame(ssab, false, true, Vector2.zero, plainIdx, out var flipped, out var flippedPart, out _);

            Assert.AreEqual(plain.Length, flipped.Length);
            for (int i = 0; i < plain.Length; i++)
            {
                Assert.AreEqual(plain[i].x, flipped[i].x, 1e-4f, $"vertex {i} x should be untouched");
                Assert.AreEqual(-plain[i].y, flipped[i].y, 1e-4f, $"vertex {i} y should mirror");
            }

            Assert.AreEqual(plainPart.m03, flippedPart.m03, 1e-4f);
            Assert.AreEqual(-plainPart.m13, flippedPart.m13, 1e-4f);
        }

        [Test]
        public void Offset_TranslatesTheGeometryAndThePartMatrixTogether()
        {
            var ssab = RequireRingo();
            var offset = new Vector2(3.5f, -1.25f);

            BuildFrame(ssab, false, false, Vector2.zero, -1, out var plain, out var plainPart, out int plainIdx);
            Assert.AreNotEqual(-1, plainIdx, "expected some part to sit off the origin");
            BuildFrame(ssab, false, false, offset, plainIdx, out var shifted, out var shiftedPart, out _);

            Assert.AreEqual(plain.Length, shifted.Length);
            for (int i = 0; i < plain.Length; i++)
            {
                Assert.AreEqual(plain[i].x + offset.x, shifted[i].x, 1e-4f, $"vertex {i} x should shift");
                Assert.AreEqual(plain[i].y + offset.y, shifted[i].y, 1e-4f, $"vertex {i} y should shift");
            }

            Assert.AreEqual(plainPart.m03 + offset.x, shiftedPart.m03, 1e-4f);
            Assert.AreEqual(plainPart.m13 + offset.y, shiftedPart.m13, 1e-4f);
        }
    }
}

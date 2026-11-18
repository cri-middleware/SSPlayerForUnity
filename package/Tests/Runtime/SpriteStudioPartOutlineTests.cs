using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using SpriteStudio.Runtime;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Part outlines (<c>TryGetPartQuad</c>) and the hit test built on them, read from the last frame built.
    /// </summary>
    public class SpriteStudioPartOutlineTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private SpriteStudioPlayer MakePlayer(SpriteStudioAnimeAsset asset, string animation)
        {
            var go = new GameObject("PartOutlinePlayer");
            _objects.Add(go);
            var player = go.AddComponent<SpriteStudioPlayer>();
            player.AnimeAsset = asset;
            Assert.IsTrue(player.SetAnimation(animation));
            return player;
        }

        private SpriteStudioPlayer MakeRingo(string animation = "walk")
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>("Ringo/Ringo");
            Assert.IsNotNull(asset, "Ringo.ssab is required.");
            return MakePlayer(asset, animation);
        }

        private static void Build(SpriteStudioPlayer player, float frame)
        {
            player.InternalPlayer.SetFrame(frame);
            player.InternalPlayer.ScheduleMeshUpdate(0f);
            player.InternalPlayer.CurrentUpdateJobHandle.Complete();
        }

        private static Rect BoxOf(in SpriteStudioPartQuad q)
        {
            float l = Mathf.Min(Mathf.Min(q.TopLeft.x, q.TopRight.x), Mathf.Min(q.BottomLeft.x, q.BottomRight.x));
            float r = Mathf.Max(Mathf.Max(q.TopLeft.x, q.TopRight.x), Mathf.Max(q.BottomLeft.x, q.BottomRight.x));
            float b = Mathf.Min(Mathf.Min(q.TopLeft.y, q.TopRight.y), Mathf.Min(q.BottomLeft.y, q.BottomRight.y));
            float t = Mathf.Max(Mathf.Max(q.TopLeft.y, q.TopRight.y), Mathf.Max(q.BottomLeft.y, q.BottomRight.y));
            return Rect.MinMaxRect(l, b, r, t);
        }

        private static Rect Union(Rect a, Rect b)
            => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        private static Vector2 Centre(in SpriteStudioPartQuad q) => (q.TopLeft + q.TopRight + q.BottomLeft + q.BottomRight) / 4f;

        private static void AssertBoxesEqual(Rect expected, Rect actual, string because)
        {
            Assert.AreEqual(expected.xMin, actual.xMin, 0.01f, because);
            Assert.AreEqual(expected.yMin, actual.yMin, 0.01f, because);
            Assert.AreEqual(expected.xMax, actual.xMax, 0.01f, because);
            Assert.AreEqual(expected.yMax, actual.yMax, 0.01f, because);
        }

        [Test]
        public void Outlines_CoverExactlyWhatIsDrawn([Values(0f, 7f, 15f)] float frame)
        {
            // Ringo draws cell parts and skinned meshes and nothing else, so the box around every drawn vertex
            // is the box around the outlines: a corner left out or a mesh box drawn too small shows here.
            var player = MakeRingo();
            Build(player, frame);
            var ip = player.InternalPlayer;

            Rect? outlines = null;
            int partCount = player.GetPartNames().Length;
            for (int i = 0; i < partCount; i++)
            {
                if (!player.TryGetPartQuad(i, out var quad)) continue;
                outlines = outlines.HasValue ? Union(outlines.Value, BoxOf(quad)) : BoxOf(quad);
            }
            Assert.IsTrue(outlines.HasValue, "Some part has an outline");

            var vertices = ip.MeshVertices;
            Rect drawn = Rect.MinMaxRect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (int v = 0; v < ip.VertexCount; v++)
            {
                Vector3 p = vertices[v].Position;
                drawn = Rect.MinMaxRect(Mathf.Min(drawn.xMin, p.x), Mathf.Min(drawn.yMin, p.y), Mathf.Max(drawn.xMax, p.x), Mathf.Max(drawn.yMax, p.y));
            }
            AssertBoxesEqual(drawn, outlines.Value, $"frame {frame}");
        }

        [Test]
        public void ASkinnedMesh_IsTheBoxAroundItsVertices()
        {
            var player = MakeRingo();
            Build(player, 0f);
            string[] names = player.GetPartNames();
            int checkedParts = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (!player.TryGetPartSkinnedMesh(names[i], out bool skinned) || !skinned) continue;
                if (!player.TryGetPartQuad(i, out var quad)) continue;
                Assert.AreEqual(quad.TopLeft.y, quad.TopRight.y, 1e-4f, names[i]);
                Assert.AreEqual(quad.TopLeft.x, quad.BottomLeft.x, 1e-4f, names[i]);
                Assert.Greater(quad.TopRight.x, quad.TopLeft.x, names[i]);
                Assert.Greater(quad.TopLeft.y, quad.BottomLeft.y, names[i]);
                checkedParts++;
            }
            Assert.Greater(checkedParts, 0, "Ringo has skinned meshes");
        }

        [Test]
        public void TheRootTransform_IsFoldedIn()
        {
            var player = MakeRingo();
            Build(player, 0f);
            int part = FirstPartWithAnOutline(player);
            Assert.IsTrue(player.TryGetPartQuad(part, out var plain));

            player.PixelsPerUnit = 100f;
            player.FlipX = true;
            player.Offset = new Vector2(1f, 2f);
            Build(player, 0f);
            Assert.IsTrue(player.TryGetPartQuad(part, out var placed));

            var expected = new Vector2(-plain.TopLeft.x / 100f + 1f, plain.TopLeft.y / 100f + 2f);
            Assert.AreEqual(expected.x, placed.TopLeft.x, 1e-4f);
            Assert.AreEqual(expected.y, placed.TopLeft.y, 1e-4f);
        }

        [Test]
        public void HitTest_FindsThePartUnderThePoint_AndOnlyAmongTheTargets()
        {
            var player = MakeRingo();
            Build(player, 0f);
            string[] names = player.GetPartNames();
            int tested = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (!player.TryGetPartQuad(i, out var quad)) continue;
                Vector2 c = Centre(quad);

                Assert.AreEqual(i, player.HitTestPart(c, new[] { i }), names[i]);
                Assert.AreEqual(i, player.HitTestPart(c, new[] { names[i] }), names[i]);

                int front = player.HitTestPart(c);
                Assert.GreaterOrEqual(front, 0, names[i]);
                Assert.IsTrue(player.TryGetPartQuad(front, out var frontQuad) && frontQuad.Contains(c),
                    "The part hit with no targets holds the point too");
                tested++;
            }
            Assert.Greater(tested, 0);

            Assert.AreEqual(-1, player.HitTestPart(new Vector2(1e5f, 1e5f)), "Nothing is that far out");
            Assert.AreEqual(-1, player.HitTestPart(Vector2.zero, new[] { "no_such_part" }), "An unknown name is skipped");
            Assert.AreEqual(-1, player.HitTestPart(Vector2.zero, new int[0]), "No targets, nothing hit");
        }

        [Test]
        public void HitTest_TheFrontMostPartWins()
        {
            var player = MakeRingo();
            Build(player, 0f);
            string[] names = player.GetPartNames();
            // Two parts whose outlines share a point: the one the hit test returns over both is drawn later.
            for (int a = 0; a < names.Length; a++)
            {
                if (!player.TryGetPartQuad(a, out var qa)) continue;
                Vector2 c = Centre(qa);
                for (int b = 0; b < names.Length; b++)
                {
                    if (b == a || !player.TryGetPartQuad(b, out var qb) || !qb.Contains(c)) continue;
                    int front = player.HitTestPart(c, new[] { a, b });
                    int alone = player.HitTestPart(c, new[] { front == a ? b : a });
                    Assert.AreEqual(front == a ? b : a, alone);
                    Assert.AreEqual(front, player.HitTestPart(c, new[] { b, a }), "The order of the targets does not matter");
                    return;
                }
            }
            Assert.Inconclusive("No two outlines of Ringo overlap on frame 0");
        }

        [Test]
        public void AHiddenPart_HasNoOutline_AndIsNeverHit()
        {
            var player = MakeRingo();
            Build(player, 0f);
            int part = FirstPartWithAnOutline(player);
            Assert.IsTrue(player.TryGetPartQuad(part, out var quad));
            Vector2 c = Centre(quad);

            Assert.IsTrue(player.SetPartVisibilityOverride(part, true));
            Build(player, 1f);

            Assert.IsFalse(player.TryGetPartQuad(part, out _));
            Assert.AreEqual(-1, player.HitTestPart(c, new[] { part }));
        }

        [Test]
        public void BeforeAFrameIsBuilt_ThereIsNothingToRead()
        {
            var player = MakeRingo();

            Assert.IsFalse(player.TryGetPartQuad(0, out _));
            Assert.AreEqual(-1, player.HitTestPart(Vector2.zero));
        }

        [Test]
        public unsafe void AShape_IsItsSizeAroundItsPivot_WhichIsWhatItDrawsWithoutADeform()
        {
            var bytes = OverallShape();
            var asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = bytes;
            _objects.Add(asset);
            var player = MakePlayer(asset, "anime_1");
            int shape = player.FindPartIndex("Shape");
            Assert.GreaterOrEqual(shape, 0);

            // Frame 0 carries no vertex deform; frame 8 drags the top-left corner out past the size.
            Build(player, 0f);
            Assert.IsTrue(player.TryGetPartQuad(shape, out var quad));
            AssertBoxesEqual(DrawnShapeBox(player, shape), BoxOf(quad), "frame 0");

            Build(player, 8f);
            Assert.IsTrue(player.TryGetPartQuad(shape, out var deformed));
            Rect drawn = DrawnShapeBox(player, shape);
            Assert.IsFalse(BoxOf(deformed).Contains(drawn.min) && BoxOf(deformed).Contains(drawn.max),
                "The deformed corner is not followed");
        }

        private static byte[] OverallShape()
        {
            var loaded = Resources.Load<SpriteStudioAnimeAsset>("overall/Shape");
            if (loaded != null && loaded.SsabData != null && loaded.SsabData.Length > 0) return loaded.SsabData;
            string path = Path.GetFullPath("../../package/Tests/Runtime/Resources/overall/Shape.ssab");
            if (!File.Exists(path)) Assert.Ignore("overall/Shape.ssab is unavailable (converted from the SDK's overall project).");
            return File.ReadAllBytes(path);
        }

        // The box around the vertices the runtime hands over for a shape, placed as the mesh job places them.
        private static unsafe Rect DrawnShapeBox(SpriteStudioPlayer player, int part)
        {
            void* h = (void*)player.InternalPlayer.NativeHandle;
            float* verts = null, world = null;
            int* counts = null;
            nuint vLen = 0, wLen = 0, cLen = 0;
            NativeMethods.ss_runtime_get_shape_vertices(h, &verts, &vLen);
            NativeMethods.ss_runtime_get_shape_vertex_counts(h, &counts, &cLen);
            NativeMethods.ss_runtime_get_world_matrices(h, &world, &wLen);
            Assert.IsTrue(verts != null && counts != null && world != null && (ulong)part < cLen);
            int n = counts[part];
            Assert.GreaterOrEqual(n, 3);
            float* m = world + part * 16;
            float* pv = verts + part * 24;
            Rect box = Rect.MinMaxRect(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (int v = 0; v < n; v++)
            {
                float x = m[0] * pv[2 * v] + m[4] * pv[2 * v + 1] + m[12];
                float y = m[1] * pv[2 * v] + m[5] * pv[2 * v + 1] + m[13];
                box = Rect.MinMaxRect(Mathf.Min(box.xMin, x), Mathf.Min(box.yMin, y), Mathf.Max(box.xMax, x), Mathf.Max(box.yMax, y));
            }
            return box;
        }

        private static int FirstPartWithAnOutline(SpriteStudioPlayer player)
        {
            int count = player.GetPartNames().Length;
            for (int i = 0; i < count; i++) if (player.TryGetPartQuad(i, out _)) return i;
            Assert.Fail("No part has an outline");
            return -1;
        }
    }
}

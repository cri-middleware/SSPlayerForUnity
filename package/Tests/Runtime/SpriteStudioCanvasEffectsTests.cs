using System.Collections;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Guard for the Canvas upload of a graphic drawing particles: their count moves every frame, so the mesh's
    /// vertex and index counts grow and shrink with it, and each frame's upload must carry that frame's
    /// geometry without the Mesh API refusing it (an error log fails the test).
    ///
    /// Uses the "overall/Effect" fixture, which the conversion that produces the clipping-mask fixture writes
    /// alongside it; without it the test is ignored.
    /// </summary>
    public class SpriteStudioCanvasEffectsTests
    {
        private const string EffectAssetPath = "overall/Effect"; // for Resources.Load
        private const string EffectSsabPath = "../../package/Tests/Runtime/Resources/overall/Effect.ssab";
        private const string Animation = "anime_1";
        private const int Graphics = 3; // the update manager's parallel pass needs at least ParallelMinCount
        private const int Frames = 120;

        [UnityTest]
        public IEnumerator Graphics_UploadEachFrameOfTheirParticles()
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(EffectAssetPath);
            bool createdAsset = false;
            if (asset == null || asset.SsabData == null || asset.SsabData.Length == 0)
            {
                string path = Path.GetFullPath(EffectSsabPath);
                if (!File.Exists(path)) Assert.Ignore("Effect.ssab is unavailable (converted with the clipping-mask fixture from the SDK's overall.sspj).");
                asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
                asset.SsabData = File.ReadAllBytes(path);
                createdAsset = true;
            }

            var canvas = new GameObject("CanvasEffects", typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var graphics = new SpriteStudioGraphic[Graphics];
            for (int i = 0; i < Graphics; i++)
            {
                var go = new GameObject("CanvasEffects" + i);
                go.transform.SetParent(canvas.transform, false);
                graphics[i] = go.AddComponent<SpriteStudioGraphic>();
                graphics[i].AnimeAsset = asset;
                graphics[i].CurrentAnimation = Animation;
            }

            int shrank = 0;
            var lastCount = new int[Graphics];
            try
            {
                Time.captureDeltaTime = 1f / 60f;
                for (int f = 0; f < Frames; f++)
                {
                    yield return null;
                    for (int i = 0; i < Graphics; i++)
                    {
                        var ip = graphics[i].InternalPlayer;
                        var mesh = graphics[i].CanvasMesh;
                        int count = ip.VertexCount;
                        Assert.AreEqual(count, mesh.vertexCount, $"graphic {i}, frame {f}: vertex count");
                        var uploaded = mesh.vertices;
                        var built = ip.MeshVertices;
                        for (int v = 0; v < count; v++)
                            Assert.AreEqual(built[v].Position, uploaded[v], $"graphic {i}, frame {f}: vertex {v}");
                        if (count < lastCount[i]) shrank++;
                        lastCount[i] = count;
                    }
                }
            }
            finally
            {
                Time.captureDeltaTime = 0f;
                Object.DestroyImmediate(canvas);
                if (createdAsset) Object.DestroyImmediate(asset);
            }

            Assert.Greater(shrank, 0, $"no frame of '{Animation}' drew fewer vertices than the frame before it");
        }
    }
}

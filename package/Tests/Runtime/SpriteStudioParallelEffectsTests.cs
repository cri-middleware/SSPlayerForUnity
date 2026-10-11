using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// Guard for the update manager stepping effect slots on the worker that fetches each player's frame
    /// data: the players must draw what the serial pass draws, frame for frame, where ConsumeFrameData
    /// steps the slots on the main thread.
    ///
    /// Uses the "overall/Effect" fixture, which the conversion that produces the clipping-mask fixture
    /// writes alongside it; without it the test is ignored.
    /// </summary>
    public class SpriteStudioParallelEffectsTests
    {
        private const string EffectAssetPath = "overall/Effect"; // for Resources.Load
        private const string EffectSsabPath = "../../package/Tests/Runtime/Resources/overall/Effect.ssab";
        private const string Animation = "anime_1";
        private const int Players = 3; // the parallel pass needs at least ParallelMinCount
        private const int Frames = 120;

        [UnityTest]
        public IEnumerator ParallelPass_DrawsWhatTheSerialPassDraws()
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

            bool parallel = SpriteStudioUpdateManager.ParallelFrameData;
            var serial = new List<ulong>();
            var stepped = new List<ulong>();
            bool drewParticles = false;
            try
            {
                // One animation frame per tick, so both passes see the same deltas.
                Time.captureDeltaTime = 1f / 60f;
                SpriteStudioUpdateManager.ParallelFrameData = false;
                yield return Record(asset, serial, _ => { });
                SpriteStudioUpdateManager.ParallelFrameData = true;
                yield return Record(asset, stepped, drew => drewParticles |= drew);
            }
            finally
            {
                SpriteStudioUpdateManager.ParallelFrameData = parallel;
                Time.captureDeltaTime = 0f;
                if (createdAsset) Object.DestroyImmediate(asset);
            }

            Assert.IsTrue(drewParticles, $"no frame drew a particle - '{Animation}' does not exercise the effect step");
            Assert.AreEqual(serial.Count, stepped.Count);
            for (int f = 0; f < serial.Count; f++)
                Assert.AreEqual(serial[f], stepped[f], $"frame {f} differs between the serial and the parallel pass");
        }

        // Plays the animation on fresh players and records, after each frame, a hash of every player's vertices.
        private static IEnumerator Record(SpriteStudioAnimeAsset asset, List<ulong> hashes, Action<bool> drewParticles)
        {
            var players = new SpriteStudioPlayer[Players];
            for (int i = 0; i < Players; i++)
            {
                players[i] = new GameObject("ParallelEffects" + i).AddComponent<SpriteStudioPlayer>();
                players[i].AnimeAsset = asset;
                players[i].SetAnimation(Animation);
            }
            try
            {
                for (int f = 0; f < Frames; f++)
                {
                    yield return null;
                    ulong h = 1469598103934665603UL;
                    for (int i = 0; i < Players; i++)
                    {
                        var ip = players[i].InternalPlayer;
                        h = Hash(ip.MeshVertices, ip.VertexCount, h);
                        drewParticles(DrawsParticles(ip._geometryBuilder));
                    }
                    hashes.Add(h);
                }
            }
            finally
            {
                foreach (var p in players) Object.DestroyImmediate(p.gameObject);
            }
        }

        private static bool DrawsParticles(SpriteStudioGeometryBuilder builder)
        {
            var batches = builder.DrawBatches;
            for (int b = 0; b < builder.DrawBatchCount; b++)
            {
                if (batches[b].Kind == SsDrawBatchKind.Effect && batches[b].EffectQuadCount > 0) return true;
            }
            return false;
        }

        private static unsafe ulong Hash(NativeArray<SsVertex> vertices, int count, ulong h)
        {
            h = (h ^ (ulong)count) * 1099511628211UL;
            if (count <= 0 || !vertices.IsCreated) return h;
            byte* b = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(vertices);
            long length = (long)count * sizeof(SsVertex);
            for (long k = 0; k < length; k++) h = (h ^ b[k]) * 1099511628211UL;
            return h;
        }
    }
}

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

        private static SpriteStudioAnimeAsset LoadEffect(out bool created)
        {
            created = false;
            var asset = Resources.Load<SpriteStudioAnimeAsset>(EffectAssetPath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0) return asset;
            string path = Path.GetFullPath(EffectSsabPath);
            if (!File.Exists(path)) Assert.Ignore("Effect.ssab is unavailable (converted with the clipping-mask fixture from the SDK's overall.sspj).");
            asset = ScriptableObject.CreateInstance<SpriteStudioAnimeAsset>();
            asset.SsabData = File.ReadAllBytes(path);
            created = true;
            return asset;
        }

        [UnityTest]
        public IEnumerator ParallelPass_DrawsWhatTheSerialPassDraws()
        {
            var asset = LoadEffect(out bool createdAsset);

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

        /// <summary>
        /// A hidden effect part steps without building a draw plan, and its clock still moves: shown again, a
        /// player draws what a player that never hid it draws, in the parallel pass and in the serial one. The
        /// particles are checked to be there, so two players drawing none would not pass.
        /// </summary>
        [UnityTest]
        public IEnumerator HiddenEffects_DrawWhatTheNeverHiddenDrawOnceShown()
        {
            var asset = LoadEffect(out bool createdAsset);
            bool parallel = SpriteStudioUpdateManager.ParallelFrameData;
            var objects = new List<GameObject>();
            try
            {
                Time.captureDeltaTime = 1f / 60f;
                foreach (bool pass in new[] { true, false })
                {
                    SpriteStudioUpdateManager.ParallelFrameData = pass;
                    var shown = new GameObject("NeverHidden").AddComponent<SpriteStudioPlayer>();
                    var hidden = new GameObject("HiddenAWhile").AddComponent<SpriteStudioPlayer>();
                    objects.Add(shown.gameObject);
                    objects.Add(hidden.gameObject);
                    shown.AnimeAsset = hidden.AnimeAsset = asset;
                    shown.SetAnimation(Animation);
                    hidden.SetAnimation(Animation);
                    int compared = 0;
                    for (int f = 0; f < 90; f++)
                    {
                        // Part 0 is the root, so cascading hides every effect part with it.
                        if (f == 10) Assume.That(hidden.InternalPlayer.SetPartVisibilityOverride(0, true, true), Is.True);
                        if (f == 50) Assume.That(hidden.InternalPlayer.ClearPartVisibilityOverride(0), Is.True);
                        yield return null;
                        if (f < 60) continue;
                        var a = shown.InternalPlayer;
                        var b = hidden.InternalPlayer;
                        Assert.AreEqual(Hash(a.MeshVertices, a.VertexCount, 0), Hash(b.MeshVertices, b.VertexCount, 0),
                            $"frame {f} ({(pass ? "parallel" : "serial")} pass): the effect lost the time it spent hidden");
                        if (DrawsParticles(b._geometryBuilder)) compared++;
                    }
                    Assert.Greater(compared, 0, $"no particle was drawn after the hidden stretch ({(pass ? "parallel" : "serial")} pass)");
                    foreach (var go in objects) Object.DestroyImmediate(go);
                    objects.Clear();
                    yield return null;
                }
            }
            finally
            {
                foreach (var go in objects) if (go != null) Object.DestroyImmediate(go);
                SpriteStudioUpdateManager.ParallelFrameData = parallel;
                Time.captureDeltaTime = 0f;
                if (createdAsset) Object.DestroyImmediate(asset);
            }
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

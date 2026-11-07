using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

namespace SpriteStudio.Runtime.Tests
{
    /// <summary>
    /// An Instance part whose child SSAB resolves back to a pack already being loaded is skipped, not
    /// recursed into.
    /// </summary>
    public class SpriteStudioInstanceCycleTests
    {
        // Ringo_tyu carries Instance parts that reference the Ringo pack.
        private const string TyuAssetPath = "Ringo/Ringo_tyu";   // for Resources.Load
        private const string RingoAssetPath = "Ringo/Ringo";
        private const string TyuSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo_tyu.ssab";
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _tyu;
        private byte[] _ringo;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _tyu = LoadSsabBytes(TyuAssetPath, TyuSsabPath);
            _ringo = LoadSsabBytes(RingoAssetPath, RingoSsabPath);
        }

        /// <summary>
        /// The converted fixtures live in Resources; fall back to reading the .ssab from disk when they were
        /// not imported. Same resolution order as SpriteStudioEventTests — resolving by relative path alone
        /// only works when the runner's working directory is examples/UnityTestProject, and these tests must
        /// not quietly skip themselves anywhere else (a regression here crashes the runner rather than
        /// reporting a red assertion, so silence would be the worst outcome).
        /// </summary>
        private static byte[] LoadSsabBytes(string resourcePath, string filePath)
        {
            var asset = Resources.Load<SpriteStudioAnimeAsset>(resourcePath);
            if (asset != null && asset.SsabData != null && asset.SsabData.Length > 0) return asset.SsabData;

            string p = Path.GetFullPath(filePath);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        /// <summary>
        /// A resolver that hands back the asking SSAB — what <see cref="SpriteStudioAnimeAsset"/>'s
        /// resolution degrades to once its external references are gone. Without the cycle guard this
        /// recurses until the process dies.
        /// </summary>
        [Test]
        public void SelfResolvingInstancePartsDoNotRecurseForever()
        {
            Assert.IsNotNull(_tyu, "Ringo_tyu.ssab is required.");

            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_tyu, (packHash, nameHash) => _tyu);

                Assert.That(player.HasSsab, Is.True, "the load must still complete");
                Assert.That(player._instancePartCount, Is.Zero, "a self-resolving Instance part is skipped, not mounted");
            }
        }

        /// <summary>
        /// The guard keys on the pack hash, not on <c>byte[]</c> identity, so a resolver that materialises
        /// fresh bytes per call (Addressables / AssetBundle / File IO) is caught the same way.
        /// </summary>
        [Test]
        public void SelfResolvingInstancePartsAreCaughtAcrossDistinctBuffers()
        {
            Assert.IsNotNull(_tyu, "Ringo_tyu.ssab is required.");

            using (var player = new SpriteStudioInternalPlayer())
            {
                // A fresh copy per call: reference-identity would never match, only the pack hash does.
                player.LoadSsab(_tyu, (packHash, nameHash) => (byte[])_tyu.Clone());

                Assert.That(player.HasSsab, Is.True, "the load must still complete");
                Assert.That(player._instancePartCount, Is.Zero, "the cycle must be detected by pack hash, not by array identity");
            }
        }

        /// <summary>
        /// The guard must not cost a correctly resolved child.
        /// </summary>
        [Test]
        public void CorrectlyResolvedInstancePartsStillLoad()
        {
            Assert.IsNotNull(_tyu, "Ringo_tyu.ssab is required.");
            Assert.IsNotNull(_ringo, "Ringo.ssab is required.");

            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_tyu, (packHash, nameHash) => _ringo);

                Assert.That(player.HasSsab, Is.True);
                Assert.That(player._instancePartCount, Is.GreaterThan(0), "the Instance parts must still get their children");
            }
        }
    }
}

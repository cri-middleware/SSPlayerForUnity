using System.IO;
using NUnit.Framework;
using SpriteStudio.Runtime;

namespace SpriteStudio.Tests
{
    /// <summary>
    /// An Instance part whose child SSAB resolves back to an SSAB already being loaded used to make
    /// LoadSsab -> SetupSlots recurse until the stack died, taking the Editor with it. Resolution
    /// reaches that state on its own: when an import leaves <c>ExternalReferences</c> unpopulated the
    /// pack-hash lookup misses, and the animation-name-hash fallback then finds the requested name in
    /// the *asking* SSAB whenever both packs happen to share an animation name.
    /// </summary>
    public class SpriteStudioInstanceCycleTests
    {
        // Ringo_tyu carries Instance parts that reference the Ringo pack.
        private const string TyuSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo_tyu.ssab";
        private const string RingoSsabPath = "../../package/Tests/Runtime/Resources/Ringo/Ringo.ssab";

        private byte[] _tyu;
        private byte[] _ringo;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var tyu = Path.GetFullPath(TyuSsabPath);
            var ringo = Path.GetFullPath(RingoSsabPath);
            if (File.Exists(tyu)) _tyu = File.ReadAllBytes(tyu);
            if (File.Exists(ringo)) _ringo = File.ReadAllBytes(ringo);
        }

        /// <summary>
        /// A resolver that hands back the asking SSAB — what <see cref="SpriteStudioAnimeAsset"/>'s
        /// resolution degrades to once its external references are gone. Without the cycle guard this
        /// recurses until the process dies, so a regression here crashes the runner rather than
        /// reporting a red assertion.
        /// </summary>
        [Test]
        public void SelfResolvingInstancePartsDoNotRecurseForever()
        {
            if (_tyu == null) Assert.Ignore("Ringo_tyu.ssab fixture not present.");

            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_tyu, (packHash, nameHash) => _tyu);

                Assert.That(player.HasSsab, Is.True, "the load must still complete");
                Assert.That(player._instancePartCount, Is.Zero, "a self-resolving Instance part is skipped, not mounted");
            }
        }

        /// <summary>
        /// The guard must not cost a correctly resolved child.
        /// </summary>
        [Test]
        public void CorrectlyResolvedInstancePartsStillLoad()
        {
            if (_tyu == null) Assert.Ignore("Ringo_tyu.ssab fixture not present.");
            if (_ringo == null) Assert.Ignore("Ringo.ssab fixture not present.");

            using (var player = new SpriteStudioInternalPlayer())
            {
                player.LoadSsab(_tyu, (packHash, nameHash) => _ringo);

                Assert.That(player.HasSsab, Is.True);
                Assert.That(player._instancePartCount, Is.GreaterThan(0), "the Instance parts must still get their children");
            }
        }
    }
}

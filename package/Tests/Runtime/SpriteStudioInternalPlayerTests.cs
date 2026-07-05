using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace SpriteStudio.Runtime.Tests
{
    public class SpriteStudioInternalPlayerTests
    {
        [Test]
        public void CreateAndDispose_DoesNotThrow()
        {
            // Basic test that the FFI context is created and destroyed correctly.
            // Verifies that the native library loads properly and does not crash.
            using (var player = new SpriteStudioInternalPlayer())
            {
                Assert.IsNotNull(player);
                // Initialization of JobHandle, GCHandle, etc. should have succeeded
            }
        }

        [Test]
        public void ConsumeFrameData_WithZeroPtr_DoesNotCrash()
        {
            using (var player = new SpriteStudioInternalPlayer())
            {
                Assert.DoesNotThrow(() => player.ConsumeFrameData(System.IntPtr.Zero, 0, 0.016f));
            }
        }

        [Test]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            // Dispose must be idempotent (a double Dispose must not double-free native memory or crash).
            var player = new SpriteStudioInternalPlayer();
            Assert.DoesNotThrow(() =>
            {
                player.Dispose();
                player.Dispose();
            });
        }

        [Test]
        public void Dispose_DoesNotLogLeakError()
        {
            // A clean Dispose should emit neither the finalizer's leak-detection log ("Memory leak detected")
            // nor any other Error log. Confirms the Dispose path (including the release-ordering change) is clean.
            var player = new SpriteStudioInternalPlayer();
            player.Dispose();
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Setters_WithoutAnimation_DoesNotThrow()
        {
            using (var player = new SpriteStudioInternalPlayer())
            {
                Assert.DoesNotThrow(() => player.SetSpeed(1.5f));
                Assert.DoesNotThrow(() => player.SetLoop(2));
                Assert.DoesNotThrow(() => player.SetSkipFrames(false));
                Assert.DoesNotThrow(() => player.SubFrameEnabled = true);
            }
        }
    }
}

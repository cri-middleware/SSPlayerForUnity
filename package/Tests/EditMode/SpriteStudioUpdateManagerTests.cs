using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using SpriteStudio.Runtime;
using Is = UnityEngine.TestTools.Constraints.Is; // for AllocatingGCMemory (aliased because it collides with NUnit's Is)

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// Deterministically verifies the documented invariants of <see cref="SpriteStudioUpdateManager"/> in EditMode.
    /// In EditMode <c>Application.isPlaying == false</c>, so EnsureRunner does not create a runner GameObject,
    /// letting the tests drive Schedule/Apply "manually, one at a time" and inspect the internal state directly.
    /// </summary>
    public class SpriteStudioUpdateManagerTests
    {
        /// <summary>
        /// A test double that stays fully managed because it never calls get_frame_data (handle=Zero).
        /// Hooks into PrepareFrame/ApplyAndRender let it reproduce dynamic unregistration during tick iteration.
        /// </summary>
        private sealed class FakeRenderable : ISpriteStudioRenderable
        {
            public int PrepareCount;
            public int ConsumeCount;
            public int ApplyCount;
            public Action OnPrepare;
            public Action OnApply;

            public void PrepareFrame(float deltaTime, out IntPtr handle, out float drawFrame)
            {
                PrepareCount++;
                handle = IntPtr.Zero; // Zero → excluded from parallel get_frame_data / ConsumeFrame (no native dependency)
                drawFrame = 0f;
                OnPrepare?.Invoke();
            }

            public void ConsumeFrame(IntPtr frameDataPtr, int len, float deltaTime) => ConsumeCount++;

            public void ApplyAndRender()
            {
                ApplyCount++;
                OnApply?.Invoke();
            }

            // handle is always Zero, so this renderable is never in the parallel get_frame_data set and
            // the update manager's post-prepare re-validation never needs a real live handle from it.
            public IntPtr GetLiveHandle() => IntPtr.Zero;
        }

        [SetUp]
        public void SetUp() => SpriteStudioUpdateManager.ResetStaticState();

        [TearDown]
        public void TearDown() => SpriteStudioUpdateManager.ResetStaticState();

        [Test]
        public void Schedule_Apply_DrivesAllRegistered()
        {
            var a = new FakeRenderable();
            var b = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.Register(b);

            SpriteStudioUpdateManager.Schedule(0.016f);
            SpriteStudioUpdateManager.Apply();

            Assert.AreEqual(1, a.PrepareCount);
            Assert.AreEqual(1, b.PrepareCount);
            Assert.AreEqual(1, a.ApplyCount);
            Assert.AreEqual(1, b.ApplyCount);
        }

        [Test]
        public void Register_IsIdempotent()
        {
            var a = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.Register(a); // duplicate registration should be ignored

            SpriteStudioUpdateManager.Schedule(0.016f);
            SpriteStudioUpdateManager.Apply();

            Assert.AreEqual(1, a.PrepareCount, "PrepareCount would become 2 if duplicate registration were allowed");
        }

        [Test]
        public void Unregister_DuringPrepare_DoesNotThrow()
        {
            // Verify the documented invariant (snapshot _tickCount iteration) that other elements being
            // destroyed/unregistered during tick (Schedule) iteration does not cause an NRE.
            var a = new FakeRenderable();
            var b = new FakeRenderable();
            var c = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.Register(b);
            SpriteStudioUpdateManager.Register(c);

            // Unregister b/c during a's PrepareFrame (simulates dynamic destruction mid-frame).
            a.OnPrepare = () =>
            {
                SpriteStudioUpdateManager.Unregister(b);
                SpriteStudioUpdateManager.Unregister(c);
            };

            Assert.DoesNotThrow(() =>
            {
                SpriteStudioUpdateManager.Schedule(0.016f);
                SpriteStudioUpdateManager.Apply();
            });
        }

        [Test]
        public void Unregister_DuringApply_DoesNotThrow()
        {
            var a = new FakeRenderable();
            var b = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.Register(b);

            // Should not crash on dynamic unregistration during the Apply phase either (snapshot-iteration guarantee).
            a.OnApply = () => SpriteStudioUpdateManager.Unregister(b);

            Assert.DoesNotThrow(() =>
            {
                SpriteStudioUpdateManager.Schedule(0.016f);
                SpriteStudioUpdateManager.Apply();
            });
        }

        [Test]
        public void ResetStaticState_ClearsRegistrations()
        {
            // Equivalent to "Enter Play Mode without Domain Reload": registrations from the previous session don't carry over.
            var a = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.ResetStaticState();

            SpriteStudioUpdateManager.Schedule(0.016f);
            SpriteStudioUpdateManager.Apply();

            Assert.AreEqual(0, a.PrepareCount, "Registrations should be cleared after ResetStaticState");
        }

        [Test]
        public void Schedule_Apply_SteadyState_DoesNotAllocate()
        {
            // Guard for the documented "zero allocation" invariant. Verify that steady-state Schedule/Apply
            // does not trigger GC (buffers/capacity are pre-reserved during warm-up).
            var renderables = new FakeRenderable[4];
            for (int i = 0; i < renderables.Length; i++)
            {
                renderables[i] = new FakeRenderable();
                SpriteStudioUpdateManager.Register(renderables[i]);
            }

            // Use "the same delegate instance" as the measurement target and call it several times before
            // measuring to (a) reserve capacity for _tickBuffer / the job's NativeArray and (b) JIT the lambda
            // body and the UpdateManager methods. AllocatingGCMemory does not warm up the delegate, so this
            // ordering is essential to avoid picking up first-call JIT allocations.
            TestDelegate steadyTick = () =>
            {
                SpriteStudioUpdateManager.Schedule(0.016f);
                SpriteStudioUpdateManager.Apply();
            };
            steadyTick();
            steadyTick();
            steadyTick();

            Assert.That(steadyTick, Is.Not.AllocatingGCMemory());
        }
    }
}

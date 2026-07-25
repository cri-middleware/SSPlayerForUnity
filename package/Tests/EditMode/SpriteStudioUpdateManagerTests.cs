using System;
using NUnit.Framework;
using Unity.Collections;
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

            /// <summary>
            /// Handle reported by PrepareFrame. Zero (the default) keeps this renderable out of the
            /// parallel get_frame_data set entirely, so most tests stay fully managed. A non-zero value is
            /// a dummy pointer that must never reach get_frame_data — set it only in tests that also make
            /// GetLiveHandle disagree with it, so the re-validation drops the entry first.
            /// </summary>
            public IntPtr Handle = IntPtr.Zero;

            /// <summary>Handle reported by GetLiveHandle. Set it to Zero (or to a different value) to
            /// simulate a player disposed / recreated by another player's event callback mid-pass.</summary>
            public IntPtr LiveHandle = IntPtr.Zero;

            public void PrepareFrame(float deltaTime, out IntPtr handle, out float drawFrame)
            {
                PrepareCount++;
                handle = Handle;
                drawFrame = 0f;
                OnPrepare?.Invoke();
            }

            public void ConsumeFrame(IntPtr frameDataPtr, int len, float deltaTime) => ConsumeCount++;

            public void ApplyAndRender()
            {
                ApplyCount++;
                OnApply?.Invoke();
            }

            public IntPtr GetLiveHandle() => LiveHandle;
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
        public void RevalidateAndCompact_DropsInvalidatedPlayers_AndKeepsRowsAligned()
        {
            // The handles here are dummy values: RevalidateAndCompact only compares them, never dereferences.
            var a = new FakeRenderable { LiveHandle = (IntPtr)1 };                 // still live
            var b = new FakeRenderable { LiveHandle = IntPtr.Zero };               // disposed mid-prepare
            var c = new FakeRenderable { LiveHandle = (IntPtr)3 };                 // still live
            var d = new FakeRenderable { LiveHandle = (IntPtr)99 };                // recreated: different context

            var handles = new NativeArray<IntPtr>(new[] { (IntPtr)1, (IntPtr)2, (IntPtr)3, (IntPtr)4 }, Allocator.Temp);
            var drawFrames = new NativeArray<float>(new[] { 1f, 2f, 3f, 4f }, Allocator.Temp);
            var pending = new ISpriteStudioRenderable[] { a, b, c, d };
            try
            {
                int n = SpriteStudioUpdateManager.RevalidateAndCompact(handles, drawFrames, pending, 4);

                Assert.AreEqual(2, n, "only the players whose handle is still live may stay in the job set");
                // The three arrays are consumed by index, so a compaction that shuffles them out of step
                // would hand a player another player's frame data.
                Assert.AreSame(a, pending[0]);
                Assert.AreEqual((IntPtr)1, handles[0]);
                Assert.AreEqual(1f, drawFrames[0]);
                Assert.AreSame(c, pending[1]);
                Assert.AreEqual((IntPtr)3, handles[1]);
                Assert.AreEqual(3f, drawFrames[1]);
                Assert.IsNull(pending[2], "the dropped tail must not keep snapshot references alive");
                Assert.IsNull(pending[3], "the dropped tail must not keep snapshot references alive");
            }
            finally
            {
                handles.Dispose();
                drawFrames.Dispose();
            }
        }

        [Test]
        public void RevalidateAndCompact_KeepsEveryStillLivePlayer()
        {
            // The guard must not cost a player that was never touched.
            var a = new FakeRenderable { LiveHandle = (IntPtr)1 };
            var b = new FakeRenderable { LiveHandle = (IntPtr)2 };

            var handles = new NativeArray<IntPtr>(new[] { (IntPtr)1, (IntPtr)2 }, Allocator.Temp);
            var drawFrames = new NativeArray<float>(new[] { 1f, 2f }, Allocator.Temp);
            var pending = new ISpriteStudioRenderable[] { a, b };
            try
            {
                Assert.AreEqual(2, SpriteStudioUpdateManager.RevalidateAndCompact(handles, drawFrames, pending, 2));
                Assert.AreSame(a, pending[0]);
                Assert.AreSame(b, pending[1]);
            }
            finally
            {
                handles.Dispose();
                drawFrames.Dispose();
            }
        }

        [Test]
        public void Schedule_DropsPlayersInvalidatedDuringPrepare()
        {
            // A user event callback fired from PollEvents during one player's PrepareFrame can destroy
            // players that were already prepared this pass. Every fake is invalidated here, so the whole
            // set is dropped and Schedule returns before get_frame_data — if the re-validation regressed,
            // it would call the native get_frame_data with these dummy handles instead.
            var a = new FakeRenderable { Handle = (IntPtr)1, LiveHandle = (IntPtr)1 };
            var b = new FakeRenderable { Handle = (IntPtr)2, LiveHandle = (IntPtr)2 };
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.Register(b);

            a.OnPrepare = () =>
            {
                a.LiveHandle = IntPtr.Zero;
                b.LiveHandle = IntPtr.Zero;
            };

            SpriteStudioUpdateManager.Schedule(0.016f);

            Assert.AreEqual(1, a.PrepareCount);
            Assert.AreEqual(1, b.PrepareCount);
            Assert.AreEqual(0, a.ConsumeCount, "a player whose handle was freed mid-pass must not be consumed");
            Assert.AreEqual(0, b.ConsumeCount, "a player whose handle was freed mid-pass must not be consumed");
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

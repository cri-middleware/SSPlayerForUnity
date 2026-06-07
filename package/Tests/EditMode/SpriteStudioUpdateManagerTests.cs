using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using SpriteStudio.Runtime;
using Is = UnityEngine.TestTools.Constraints.Is; // AllocatingGCMemory 用 (NUnit の Is と衝突するためエイリアス)

namespace SpriteStudio.Runtime.EditModeTests
{
    /// <summary>
    /// <see cref="SpriteStudioUpdateManager"/> の文書化済み不変条件を EditMode で決定的に検証する。
    /// EditMode では <c>Application.isPlaying == false</c> のため EnsureRunner が runner GameObject を
    /// 生成せず、テストが Schedule/Apply を「手動で1回ずつ」駆動して内部状態を直接確認できる。
    /// </summary>
    public class SpriteStudioUpdateManagerTests
    {
        /// <summary>
        /// get_frame_data を呼ばない (handle=Zero) ので純マネージドで完結するテストダブル。
        /// PrepareFrame/ApplyAndRender にフックを刺し、tick 反復中の動的解除を再現できる。
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
                handle = IntPtr.Zero; // Zero → 並列 get_frame_data / ConsumeFrame の対象外 (ネイティブ非依存)
                drawFrame = 0f;
                OnPrepare?.Invoke();
            }

            public void ConsumeFrame(IntPtr frameDataPtr, int len, float deltaTime) => ConsumeCount++;

            public void ApplyAndRender()
            {
                ApplyCount++;
                OnApply?.Invoke();
            }
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
            SpriteStudioUpdateManager.Register(a); // 二重登録は無視されるはず

            SpriteStudioUpdateManager.Schedule(0.016f);
            SpriteStudioUpdateManager.Apply();

            Assert.AreEqual(1, a.PrepareCount, "重複登録が許されると PrepareCount が 2 になってしまう");
        }

        [Test]
        public void Unregister_DuringPrepare_DoesNotThrow()
        {
            // tick (Schedule) 反復中に他要素が破棄/解除されても NRE を起こさない、という
            // 文書化済み不変条件 (スナップショット _tickCount 反復) を検証する。
            var a = new FakeRenderable();
            var b = new FakeRenderable();
            var c = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.Register(b);
            SpriteStudioUpdateManager.Register(c);

            // a の PrepareFrame 中に b/c を解除する (フレーム途中の動的破棄をシミュレート)。
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

            // Apply フェーズ中の動的解除でも落ちないこと (スナップショット反復の保証)。
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
            // "Enter Play Mode without Domain Reload" 相当: 前セッションの登録が次へ持ち越されない。
            var a = new FakeRenderable();
            SpriteStudioUpdateManager.Register(a);
            SpriteStudioUpdateManager.ResetStaticState();

            SpriteStudioUpdateManager.Schedule(0.016f);
            SpriteStudioUpdateManager.Apply();

            Assert.AreEqual(0, a.PrepareCount, "ResetStaticState 後は登録が消えているはず");
        }

        [Test]
        public void Schedule_Apply_SteadyState_DoesNotAllocate()
        {
            // 文書化済み「ゼロアロケーション」不変条件のガード。定常状態の Schedule/Apply が
            // GC を発生させないことを検証する (バッファ/容量はウォームアップで確保済みにする)。
            var renderables = new FakeRenderable[4];
            for (int i = 0; i < renderables.Length; i++)
            {
                renderables[i] = new FakeRenderable();
                SpriteStudioUpdateManager.Register(renderables[i]);
            }

            // 計測対象は「同一のデリゲートインスタンス」を使い、計測前に複数回呼んで
            // (a) _tickBuffer / job 用 NativeArray の容量確保、(b) ラムダ本体と UpdateManager
            // メソッドの JIT を済ませておく。AllocatingGCMemory はデリゲートをウォームアップ
            // しないため、初回呼び出しの JIT アロケーションを拾わないようにこの順序が必須。
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

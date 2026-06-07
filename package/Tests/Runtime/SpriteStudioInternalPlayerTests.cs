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
            // FFI コンテキストが正常に作成・破棄されるか確認する基本的なテスト
            // ネイティブライブラリが適切にロードされ、クラッシュしないかを検証します。
            using (var player = new SpriteStudioInternalPlayer())
            {
                Assert.IsNotNull(player);
                // JobHandle や GCHandle などの初期化が成功しているはずです
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
            // Dispose は冪等であること (二重 Dispose で native の二重解放やクラッシュを起こさない)。
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
            // 正常に Dispose すれば、ファイナライザのリーク検出ログ ("Memory leak detected") も
            // その他の Error ログも出ない。Dispose 経路 (解放順序変更含む) が綺麗であることを確認。
            var player = new SpriteStudioInternalPlayer();
            player.Dispose();
            LogAssert.NoUnexpectedReceived();
        }
    }
}

using NUnit.Framework;
using SpriteStudio.Runtime;
using UnityEngine;

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
    }
}

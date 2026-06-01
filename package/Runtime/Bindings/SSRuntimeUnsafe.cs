using System;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// ECS (Entities) の IComponentData や Burst Job 内から安全に
    /// ネイティブポインタ (void*) を扱うためのラッパー構造体です。
    /// </summary>
    public unsafe struct SsPlayerHandle
    {
        [NativeDisableUnsafePtrRestriction]
        public void* ptr;

        public bool IsValid => ptr != null;
    }
}

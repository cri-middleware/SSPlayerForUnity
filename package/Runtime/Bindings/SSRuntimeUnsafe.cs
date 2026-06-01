using Unity.Collections.LowLevel.Unsafe;

namespace SpriteStudio.Runtime.Bindings
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

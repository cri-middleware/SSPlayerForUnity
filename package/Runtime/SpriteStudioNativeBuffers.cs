using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// ゼロアロケーションのための NativeArray 永続バッファ操作と、FlatBuffer の scalar vector を
    /// 生バッファから一括コピーするヘルパ。<see cref="SpriteStudioGeometryBuilder"/> とその
    /// コラボレータ (Effect/Mask など) が共有する。
    /// </summary>
    internal static class SpriteStudioNativeBuffers
    {
        /// <summary>必要件数を満たすまで grow-only で確保する。容量が足りていれば再利用する。</summary>
        public static void EnsureCapacity<T>(ref NativeArray<T> arr, int needed) where T : struct
        {
            if (arr.IsCreated && arr.Length >= needed) return;
            if (arr.IsCreated) arr.Dispose();
            arr = new NativeArray<T>(needed < 1 ? 1 : needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>grow 時に既存内容を保持する版 (結合バッファを slot ごとに追記するため)。</summary>
        public static void EnsureCapacityPreserve<T>(ref NativeArray<T> arr, int needed) where T : struct
        {
            if (arr.IsCreated && arr.Length >= needed) return;
            int newLen = UnityEngine.Mathf.Max(needed, arr.IsCreated ? arr.Length * 2 : 256);
            var newArr = new NativeArray<T>(newLen, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (arr.IsCreated)
            {
                NativeArray<T>.Copy(arr, newArr, arr.Length);
                arr.Dispose();
            }
            arr = newArr;
        }

        /// <summary>
        /// FlatBuffer の scalar vector (vtable offset vtoff) を生バッファ fb から dst[dstOffset..] へ
        /// 一括 memcpy する。n は generated accessor 由来の要素数。vector データは fb 内に連続配置され、
        /// 位置は Table の public API で取る。
        /// </summary>
        public static unsafe void CopyFbVector<T>(Google.FlatBuffers.Table t, int vtoff, byte* fb, NativeArray<T> dst, int dstOffset, int n) where T : unmanaged
        {
            if (n <= 0) return;
            int o = t.__offset(vtoff);
            if (o == 0) return; // フィールド欠落 (required のはずだが防御的に)
            UnityEngine.Debug.Assert(t.__vector_len(o) == n, "CopyFbVector: vtable offset と要素数が不一致 (offset 定義ズレの疑い)");
            int pos = t.__vector(o); // vector 先頭要素の絶対 byte 位置
            int elem = UnsafeUtility.SizeOf<T>();
            byte* d = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(dst) + (long)dstOffset * elem;
            UnsafeUtility.MemCpy(d, fb + pos, (long)n * elem);
        }
    }
}

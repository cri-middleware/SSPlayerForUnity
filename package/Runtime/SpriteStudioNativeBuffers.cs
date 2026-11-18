using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Helpers for zero-allocation NativeArray persistent-buffer operations and for bulk-copying a FlatBuffer
    /// scalar vector out of a raw buffer. Shared by <see cref="SpriteStudioGeometryBuilder"/> and its
    /// collaborators (Effect/Mask, etc.).
    /// </summary>
    internal static class SpriteStudioNativeBuffers
    {
        /// <summary>Allocate grow-only until the required count is met. Reuse if capacity is sufficient.</summary>
        public static void EnsureCapacity<T>(ref NativeArray<T> arr, int needed) where T : struct
        {
            if (arr.IsCreated && arr.Length >= needed) return;
            if (arr.IsCreated) arr.Dispose();
            arr = new NativeArray<T>(needed < 1 ? 1 : needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Variant that preserves existing contents on grow (for appending to the merged buffer slot by slot).</summary>
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
        /// The vector at vtable offset <paramref name="vtoff"/> of a FlatBuffer's root table, read straight from
        /// the buffer's bytes as the generated accessor reads it (root offset, table, vtable, field, vector), so a
        /// worker can read it without a managed ByteBuffer. Null with a zero count when the field is absent.
        /// </summary>
        public static unsafe byte* RootVector(byte* buffer, int vtoff, out int count)
        {
            count = 0;
            byte* table = buffer + *(int*)buffer;
            byte* vtable = table - *(int*)table;
            ushort field = vtoff < *(ushort*)vtable ? *(ushort*)(vtable + vtoff) : (ushort)0;
            if (field == 0) return null;
            byte* vector = table + field;
            vector += *(int*)vector;
            count = *(int*)vector;
            return vector + sizeof(int);
        }

        /// <summary>
        /// Bulk memcpy a FlatBuffer scalar vector (vtable offset vtoff) from the raw buffer fb into
        /// dst[dstOffset..]. n is the element count from the generated accessor. The vector data is laid out
        /// contiguously within fb, and its position is obtained via the Table's public API.
        /// </summary>
        public static unsafe void CopyFbVector<T>(Google.FlatBuffers.Table t, int vtoff, byte* fb, NativeArray<T> dst, int dstOffset, int n) where T : unmanaged
        {
            if (n <= 0) return;
            int o = t.__offset(vtoff);
            if (o == 0) return; // field missing (should be required, but defensively)
            UnityEngine.Debug.Assert(t.__vector_len(o) == n, "CopyFbVector: vtable offset and element count mismatch (suspected offset definition drift)");
            int pos = t.__vector(o); // absolute byte position of the vector's first element
            int elem = UnsafeUtility.SizeOf<T>();
            byte* d = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(dst) + (long)dstOffset * elem;
            UnsafeUtility.MemCpy(d, fb + pos, (long)n * elem);
        }
    }
}

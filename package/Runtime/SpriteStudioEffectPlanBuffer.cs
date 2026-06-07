using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// エフェクトの draw_plan (ネイティブ FlatBuffer) を frame-data と同じ流儀で読み、全エフェクトパートの
    /// ジオメトリ (verts/uvs/colors/indices) を連結バッファへ詰める責務を
    /// <see cref="SpriteStudioGeometryBuilder"/> から分離したコラボレータ。Burst Job は連結バッファを読む。
    ///
    /// ゼロアロケーションのためバッファは永続確保し grow-only で再利用する。論理件数は *_Count で保持する。
    /// </summary>
    internal sealed class SpriteStudioEffectPlanBuffer : IDisposable
    {
        // パーツ index で引く draw_plan の結果 (CommandStart/Count/QuadBase)。MemClear 済 default = 非可視。
        private NativeArray<EffectDrawPlanData> _effectPlans;

        private Google.FlatBuffers.ByteBuffer _planByteBuffer;
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        // ゼロコピー: ネイティブ draw_plan ポインタを直接 ByteBuffer に載せる読み取り専用アロケータ。
        private NativeByteBufferAllocator _planAllocator;
#else
        private byte[] _planBytes;
#endif
        private NativeArray<EffectDrawCommandData> _effectCommands;
        private NativeArray<float> _effectVerts;
        private NativeArray<float> _effectUvs;
        private NativeArray<float> _effectColors;
        private NativeArray<int> _effectIndices;
        private int _effectCommandCount;
        private int _effectVertFloatCount;
        private int _effectUvFloatCount;
        private int _effectColorFloatCount;
        private int _effectIndexCount;

        /// <summary>結合ジオメトリ (Burst Job が読む)。</summary>
        public NativeArray<float> Verts { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectVerts; }
        public NativeArray<float> Uvs { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectUvs; }
        public NativeArray<float> Colors { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectColors; }
        public NativeArray<int> Indices { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _effectIndices; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EffectDrawPlanData GetPlan(int partIndex) => _effectPlans[partIndex];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPlan(int partIndex, EffectDrawPlanData plan) => _effectPlans[partIndex] = plan;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EffectDrawCommandData GetCommand(int index) => _effectCommands[index];

        /// <summary>
        /// フレーム頭で plan テーブルを partsLength ぶん確保し、全 plan を default (非可視) へ MemClear、
        /// 結合バッファの論理件数を 0 へリセットする。
        /// </summary>
        public unsafe void BeginFrame(int partsLength)
        {
            SpriteStudioNativeBuffers.EnsureCapacity(ref _effectPlans, partsLength);
            _effectCommandCount = 0;
            _effectVertFloatCount = 0;
            _effectUvFloatCount = 0;
            _effectColorFloatCount = 0;
            _effectIndexCount = 0;
            if (partsLength > 0)
            {
                UnsafeUtility.MemClear(
                    NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(_effectPlans),
                    (long)partsLength * UnsafeUtility.SizeOf<EffectDrawPlanData>());
            }
        }

        /// <summary>
        /// エフェクトが無くても Job が参照するため、結合配列が最低 1 要素を持つことを保証する。
        /// </summary>
        public void EnsureGeometryMinimum()
        {
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectVerts, 1);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectUvs, 1);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectColors, 1);
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectIndices, 1);
        }

        /// <summary>
        /// 1 スロットの draw_plan (ネイティブ FlatBuffer fb/len) を読み、ジオメトリを結合バッファへ追記し、
        /// このスロットの plan メタ (CommandStart/Count/QuadBase) を返す。
        /// </summary>
        public unsafe EffectDrawPlanData Append(byte* fb, int len)
        {
            if (fb == null || len <= 0) return default;

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
            // ゼロコピー: ネイティブポインタを直接 ByteBuffer に載せる (allocator/ByteBuffer は使い回し)。
            // draw_plan は本メソッド内で全ジオメトリを結合 NativeArray へコピーし切るため寿命が完結し安全。
            if (_planAllocator == null)
            {
                _planAllocator = new NativeByteBufferAllocator();
                _planByteBuffer = new Google.FlatBuffers.ByteBuffer(_planAllocator, 0);
            }
            _planAllocator.Reset(fb, len);
            _planByteBuffer.Position = 0;
#else
            // ネイティブ → 再利用 byte[] へコピーして ByteBuffer に包む (フォールバック)。
            if (_planBytes == null || _planBytes.Length < len)
            {
                _planBytes = new byte[UnityEngine.Mathf.Max(len, 4096)];
                _planByteBuffer = new Google.FlatBuffers.ByteBuffer(_planBytes);
            }
            System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)fb, _planBytes, 0, len);
            _planByteBuffer.Position = 0;
#endif

            var plan = ss.runtime.EffectDrawPlan.GetRootAsEffectDrawPlan(_planByteBuffer);

            int vLen = plan.VertsLength;
            int uLen = plan.UvsLength;
            int cLen = plan.ColorsLength;
            int iLen = plan.IndicesLength;
            int cmdLen = plan.CommandsLength;

            // このスロットのグローバル quad ベース (= これまでに詰めた quad 数 = indices/6)。
            int quadBase = _effectIndexCount / 6;

            // ジオメトリは fbs で連続 scalar vector。生ポインタ fb から結合バッファへ一括 memcpy する。
            // vtable offset = 4 + field_index*2、fbs 定義順: commands(0)/vert_stride(1)/verts(2)/uvs(3)/colors(4)/indices(5)。
            var t = new Google.FlatBuffers.Table(
                _planByteBuffer.GetInt(_planByteBuffer.Position) + _planByteBuffer.Position, _planByteBuffer);

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectVerts, _effectVertFloatCount + vLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 8, fb, _effectVerts, _effectVertFloatCount, vLen);
            _effectVertFloatCount += vLen;

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectUvs, _effectUvFloatCount + uLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 10, fb, _effectUvs, _effectUvFloatCount, uLen);
            _effectUvFloatCount += uLen;

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectColors, _effectColorFloatCount + cLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 12, fb, _effectColors, _effectColorFloatCount, cLen);
            _effectColorFloatCount += cLen;

            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectIndices, _effectIndexCount + iLen);
            SpriteStudioNativeBuffers.CopyFbVector(t, 14, fb, _effectIndices, _effectIndexCount, iLen);
            _effectIndexCount += iLen;

            // commands を抽出 (描画順ループでテクスチャ/ブレンド/範囲を参照する)。
            SpriteStudioNativeBuffers.EnsureCapacityPreserve(ref _effectCommands, _effectCommandCount + cmdLen);
            int cmdStart = _effectCommandCount;
            for (int c = 0; c < cmdLen; c++)
            {
                var cc = plan.Commands(c);
                // 欠落コマンドは default (QuadCount=0) で埋める。continue で飛ばすと前フレームの値が残り、
                // 誤った quad 範囲を読むため必ず埋める。
                if (!cc.HasValue) { _effectCommands[cmdStart + c] = default; continue; }
                var cmd = cc.Value;
                _effectCommands[cmdStart + c] = new EffectDrawCommandData
                {
                    CellmapHash = cmd.CellmapHash,
                    QuadCount = cmd.QuadCount,
                    ParticleOffset = cmd.ParticleOffset,
                    Blend = cmd.Blend
                };
            }
            _effectCommandCount += cmdLen;

            return new EffectDrawPlanData
            {
                CommandStart = cmdStart,
                CommandCount = cmdLen,
                QuadBase = quadBase
            };
        }

        public void Dispose()
        {
            if (_effectPlans.IsCreated) _effectPlans.Dispose();
            if (_effectCommands.IsCreated) _effectCommands.Dispose();
            if (_effectVerts.IsCreated) _effectVerts.Dispose();
            if (_effectUvs.IsCreated) _effectUvs.Dispose();
            if (_effectColors.IsCreated) _effectColors.Dispose();
            if (_effectIndices.IsCreated) _effectIndices.Dispose();
        }
    }
}

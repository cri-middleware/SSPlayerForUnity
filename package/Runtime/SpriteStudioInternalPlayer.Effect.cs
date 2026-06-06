using System;
using Unity.Collections;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    public partial class SpriteStudioInternalPlayer
    {
        private struct EffectSlotState
        {
            public System.IntPtr EffectSlot;
        }
        private EffectSlotState[] _effectSlots;

        private NativeArray<EffectDrawPlanData> _effectPlans;

        // エフェクト draw_plan を frame-data と同じ流儀で読む (ネイティブ→byte[]→ByteBuffer→生成アクセサ)。
        // 全エフェクトパートのジオメトリを連結した結合バッファ。Burst Job はこれを読む。
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

        // effect/instance スロットを持つパーツ index の疎なリスト (SetupSlots で確定)。
        private int[] _effectPartIndices;
        private int _effectPartCount;

        // この SSAB に Effect パートが 1 つでもあるか (フレーム重複スキップの可否判定に使う)。
        private bool _hasEffectParts;

        // ジオメトリを結合バッファへ追記する。戻り値は結合バッファ上の位置情報。
        // 手書き vtable walk は廃し、生成アクセサ ss.runtime.EffectDrawPlan を使う。
        private unsafe EffectDrawPlanData AppendEffectDrawPlan(byte* fb, int len)
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

            // ジオメトリ (verts/uvs/colors/indices) は fbs で連続 scalar vector。旧実装の per-element 読み
            // (plan.Verts(j) を粒子数×要素ぶんループ) は実測で処理時間の ~49% を占めた。Table の public API で
            // vector 先頭の絶対 byte 位置を取り、生ポインタ fb から結合バッファへ一括 memcpy する (fbs コメントの
            // "one memcpy per command" 設計どおり)。fb と _planByteBuffer は同一バイト列なので位置は共通。
            // vtable offset = 4 + field_index*2、fbs 定義順: commands(0)/vert_stride(1)/verts(2)/uvs(3)/colors(4)/indices(5)。
            var t = new Google.FlatBuffers.Table(
                _planByteBuffer.GetInt(_planByteBuffer.Position) + _planByteBuffer.Position, _planByteBuffer);

            EnsureCapacityPreserve(ref _effectVerts, _effectVertFloatCount + vLen);
            CopyFbVector(t, 8, fb, _effectVerts, _effectVertFloatCount, vLen);
            _effectVertFloatCount += vLen;

            EnsureCapacityPreserve(ref _effectUvs, _effectUvFloatCount + uLen);
            CopyFbVector(t, 10, fb, _effectUvs, _effectUvFloatCount, uLen);
            _effectUvFloatCount += uLen;

            EnsureCapacityPreserve(ref _effectColors, _effectColorFloatCount + cLen);
            CopyFbVector(t, 12, fb, _effectColors, _effectColorFloatCount, cLen);
            _effectColorFloatCount += cLen;

            EnsureCapacityPreserve(ref _effectIndices, _effectIndexCount + iLen);
            CopyFbVector(t, 14, fb, _effectIndices, _effectIndexCount, iLen);
            _effectIndexCount += iLen;

            // commands を抽出 (描画順ループでテクスチャ/ブレンド/範囲を参照する)。
            EnsureCapacityPreserve(ref _effectCommands, _effectCommandCount + cmdLen);
            int cmdStart = _effectCommandCount;
            for (int c = 0; c < cmdLen; c++)
            {
                var cc = plan.Commands(c);
                // Zero-fill a missing command (QuadCount=0 makes the draw loop skip it reliably).
                // Skipping the fill with `continue` would leave last frame's stale value here,
                // causing the wrong quad range to be read.
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

        // FlatBuffer の scalar vector (vtable offset vtoff) を生バッファ fb から dst[dstOffset..] へ一括 memcpy する。
        // n は generated accessor 由来の要素数。vector データは fb 内に連続配置され、位置は Table の public API で取る。
        private static unsafe void CopyFbVector<T>(Google.FlatBuffers.Table t, int vtoff, byte* fb, NativeArray<T> dst, int dstOffset, int n) where T : unmanaged
        {
            if (n <= 0) return;
            int o = t.__offset(vtoff);
            if (o == 0) return; // フィールド欠落 (required のはずだが防御的に)
            UnityEngine.Debug.Assert(t.__vector_len(o) == n, "CopyFbVector: vtable offset と要素数が不一致 (offset 定義ズレの疑い)");
            int pos = t.__vector(o); // vector 先頭要素の絶対 byte 位置
            int elem = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();
            byte* d = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(dst) + (long)dstOffset * elem;
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(d, fb + pos, (long)n * elem);
        }
    }
}

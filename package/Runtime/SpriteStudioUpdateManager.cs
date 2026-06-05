using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// UpdateManager が「全 Schedule → 全 Apply」の2フェーズで駆動する対象。
    /// </summary>
    internal interface ISpriteStudioRenderable
    {
        /// <summary>Pass A (直列・軽量): 前フレーム Job 完了待ち + advance + drawFrame/skip 確定。
        /// out handle が Zero のときは get_frame_data / ConsumeFrame の対象外 (無効 or skip)。</summary>
        void PrepareFrame(float deltaTime, out IntPtr handle, out float drawFrame);

        /// <summary>Pass C (直列): 並列 Pass B が取得した frame-data ポインタを消費し、Burst Job をスケジュール。</summary>
        void ConsumeFrame(IntPtr frameDataPtr, int len, float deltaTime);

        /// <summary>Phase 2: Job 完了を待ち、メッシュへ反映・描画する。</summary>
        void ApplyAndRender();
    }

    /// <summary>
    /// 全 SpriteStudio レンダラーを集中管理し、毎フレームを 3 パス + LateUpdate で駆動する:
    ///   Pass A (Update, 直列・軽量): 各プレイヤーを 1 フレーム進め (advance) + drawFrame/frame-skip 判定。
    ///   Pass B (Update, 並列): 非 skip プレイヤーの get_frame_data を IJobParallelFor でワーカー並列実行する。
    ///       get_frame_data はコンテキスト毎に独立 (グローバル可変状態なし) = 並行安全で、最重量の
    ///       pose/行列/頂点/draw batch 計算をキャラ間で並列化する。完了をバリアで待つ。
    ///   Pass C (Update, 直列): 取得した frame-data ポインタを消費しメッシュ構築 Burst Job を発行。
    ///   Phase 2 (LateUpdate): 全 Job 完了待ち + メッシュ反映。
    ///
    /// play モードのみ集中管理する。edit モード (Graphic の ExecuteAlways プレビュー) は
    /// 各コンポーネントが自前で _core.TickAndSchedule/ApplyAndRender を直列に呼ぶ (並列経路とは分離)。
    /// </summary>
    public static class SpriteStudioUpdateManager
    {
        private static readonly List<ISpriteStudioRenderable> _renderables = new List<ISpriteStudioRenderable>();
        private static ISpriteStudioRenderable[] _tickBuffer = new ISpriteStudioRenderable[16];
        private static SpriteStudioUpdateRunner _runner;

        // Pass B (並列 get_frame_data) 用の永続バッファ (grow-only, 定常ゼロ GC)。
        private static ISpriteStudioRenderable[] _pending = new ISpriteStudioRenderable[16];
        private static NativeArray<IntPtr> _jobHandles;
        private static NativeArray<float> _jobDrawFrames;
        private static NativeArray<IntPtr> _jobOutPtr;
        private static NativeArray<int> _jobOutLen;

        /// <summary>get_frame_data をキャラ間で並列実行するか (false で全直列フォールバック = A/B 比較・回帰切り分け用)。</summary>
        public static bool ParallelFrameData = true;
        /// <summary>並列化する最小プレイヤー数。これ未満はワーカー起床コストを避けて直列実行する。</summary>
        public static int ParallelMinCount = 2;
        /// <summary>IJobParallelFor の innerloopBatchCount。get_frame_data は 1 件が重いので 1 が最適
        /// (各キャラを独立に全コアへ work-steal で分配)。大きくすると少数キャラで並列度が落ちる。</summary>
        public static int ParallelBatch = 1;

        // 計測用 ProfilerMarker (Profiler に "SpriteStudio.PassX" として表示)。Begin/End は非計測時ほぼ無コスト。
        //   PassB(parallel) と PassB(serial=トグル off) の差 = A1' の get_frame_data 並列化の効果。
        //   PassC = 残る直列 (Marshal.Copy + parse + 会計 + effect/instance + Job schedule) = ② で除去する対象。
        private static readonly ProfilerMarker s_passA = new ProfilerMarker("SpriteStudio.PassA.Advance+Skip");
        private static readonly ProfilerMarker s_passB = new ProfilerMarker("SpriteStudio.PassB.GetFrameData");
        private static readonly ProfilerMarker s_passC = new ProfilerMarker("SpriteStudio.PassC.Consume");
        /// <summary>直近フレームの登録レンダラー総数 (診断用)。</summary>
        public static int LastTotalCount;
        /// <summary>直近フレームで get_frame_data を実行した数 (= skip 除く並列対象数, 診断用)。</summary>
        public static int LastParallelCount;

        internal static void Register(ISpriteStudioRenderable renderable)
        {
            if (renderable == null || _renderables.Contains(renderable)) return;
            _renderables.Add(renderable);
            EnsureRunner();
        }

        internal static void Unregister(ISpriteStudioRenderable renderable)
        {
            _renderables.Remove(renderable);
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;        // Unity の null チェック (破棄済みなら再生成)
            if (!Application.isPlaying) return;  // play モードのみ集中管理 (edit は各コンポーネントが自前更新)

            var go = new GameObject("SpriteStudioUpdateRunner") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<SpriteStudioUpdateRunner>();
        }

        internal static void Schedule(float deltaTime)
        {
            int count = _renderables.Count;
            if (count == 0) return;

            // 反復中の Register/Unregister に備え、再利用バッファへスナップショットしてから処理する
            if (_tickBuffer.Length < count) _tickBuffer = new ISpriteStudioRenderable[Mathf.NextPowerOfTwo(count)];
            _renderables.CopyTo(0, _tickBuffer, 0, count);
            EnsureJobCapacity(count);

            LastTotalCount = count;

            // Pass A (直列・軽量): 1 フレーム進め、drawFrame と frame-skip を確定。
            // 非 skip かつ有効なプレイヤーだけを並列 get_frame_data の入力へ詰める。
            int n = 0;
            s_passA.Begin();
            for (int i = 0; i < count; i++)
            {
                _tickBuffer[i].PrepareFrame(deltaTime, out var handle, out var drawFrame);
                if (handle != IntPtr.Zero)
                {
                    _jobHandles[n] = handle;
                    _jobDrawFrames[n] = drawFrame;
                    _pending[n] = _tickBuffer[i];
                    n++;
                }
            }
            s_passA.End();
            LastParallelCount = n;
            if (n == 0) return;

            // Pass B: get_frame_data はコンテキスト毎に独立 (グローバル可変状態なし・共有リソースは不変
            // Send+Sync) のためキャラ間でワーカースレッド並列に実行できる。最重量の pose/行列/頂点/
            // draw batch/FlatBuffer build を並列化する。.Complete() でメインスレッドはバリア待ちするので、
            // この marker の時間 = 並列 get_frame_data の wall-clock (トグル off の serial と比較して効果を測る)。
            s_passB.Begin();
            if (ParallelFrameData && n >= ParallelMinCount)
            {
                new GetFrameDataJob
                {
                    Handles = _jobHandles, Frames = _jobDrawFrames,
                    OutPtr = _jobOutPtr, OutLen = _jobOutLen,
                }.Schedule(n, Mathf.Max(1, ParallelBatch)).Complete();
            }
            else
            {
                // 直列フォールバック (少数 or トグル off)。ワーカー起床コストを避ける / A/B 比較用。
                for (int i = 0; i < n; i++)
                {
                    unsafe
                    {
                        byte* p = null;
                        nuint l = 0;
                        NativeMethods.ss_runtime_get_frame_data((void*)_jobHandles[i], _jobDrawFrames[i], &p, &l);
                        _jobOutPtr[i] = (IntPtr)p;
                        _jobOutLen[i] = (int)l;
                    }
                }
            }
            s_passB.End();

            // Pass C (直列): 取得した frame-data ポインタを消費してメッシュ構築 Job を発行。
            // この marker = 残る直列コスト (Marshal.Copy + FlatBuffers parse + 会計 + effect/instance step
            // + Job schedule)。これが支配的なら A1' の効果は薄く、② (生 SoA ポインタ化) が本命となる。
            s_passC.Begin();
            for (int i = 0; i < n; i++)
            {
                _pending[i].ConsumeFrame(_jobOutPtr[i], _jobOutLen[i], deltaTime);
                _pending[i] = null; // スナップショットの参照を残さない
            }
            s_passC.End();
        }

        private static void EnsureJobCapacity(int count)
        {
            if (_jobHandles.IsCreated && _jobHandles.Length >= count && _pending.Length >= count) return;
            int cap = Mathf.NextPowerOfTwo(Mathf.Max(count, 16));
            if (_jobHandles.IsCreated) _jobHandles.Dispose();
            if (_jobDrawFrames.IsCreated) _jobDrawFrames.Dispose();
            if (_jobOutPtr.IsCreated) _jobOutPtr.Dispose();
            if (_jobOutLen.IsCreated) _jobOutLen.Dispose();
            _jobHandles = new NativeArray<IntPtr>(cap, Allocator.Persistent);
            _jobDrawFrames = new NativeArray<float>(cap, Allocator.Persistent);
            _jobOutPtr = new NativeArray<IntPtr>(cap, Allocator.Persistent);
            _jobOutLen = new NativeArray<int>(cap, Allocator.Persistent);
            if (_pending.Length < cap) _pending = new ISpriteStudioRenderable[cap];
        }

        /// <summary>play モード終了時に永続 NativeArray を破棄する (runner OnDestroy から呼ぶ)。</summary>
        internal static void Teardown()
        {
            if (_jobHandles.IsCreated) _jobHandles.Dispose();
            if (_jobDrawFrames.IsCreated) _jobDrawFrames.Dispose();
            if (_jobOutPtr.IsCreated) _jobOutPtr.Dispose();
            if (_jobOutLen.IsCreated) _jobOutLen.Dispose();
            _runner = null;
        }

        internal static void Apply()
        {
            int count = _renderables.Count;
            if (count == 0) return;

            // Phase 2: 全インスタンスの Job 完了待ち + メッシュ反映
            for (int i = 0; i < count; i++) _tickBuffer[i].ApplyAndRender();

            // スナップショットの参照を残さない
            for (int i = 0; i < count; i++) _tickBuffer[i] = null;
        }
    }

    /// <summary>UpdateManager を毎フレーム駆動するための隠し MonoBehaviour (play モード時に1つだけ生成)。</summary>
    [AddComponentMenu("")]
    internal sealed class SpriteStudioUpdateRunner : MonoBehaviour
    {
        private void Update()
        {
            SpriteStudioUpdateManager.Schedule(Time.deltaTime);
        }

        private void LateUpdate()
        {
            SpriteStudioUpdateManager.Apply();
        }

        private void OnDestroy()
        {
            // play モード終了時に永続 NativeArray を破棄 (リーク検出回避)。
            SpriteStudioUpdateManager.Teardown();
        }
    }

    /// <summary>
    /// Pass B: get_frame_data をキャラ間で並列実行する。get_frame_data はコンテキスト毎に独立
    /// (グローバル可変状態なし・共有リソースは不変 Send+Sync) のため並行呼び出しが安全。
    /// DllImport を呼ぶため [BurstCompile] は付けない (Burst から P/Invoke 不可)。各 index は
    /// 自分のスロットだけを書くため WriteOnly でエイリアスなし。
    /// </summary>
    internal struct GetFrameDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<IntPtr> Handles;
        [ReadOnly] public NativeArray<float> Frames;
        [WriteOnly] public NativeArray<IntPtr> OutPtr;
        [WriteOnly] public NativeArray<int> OutLen;

        public void Execute(int index)
        {
            unsafe
            {
                byte* p = null;
                nuint l = 0;
                NativeMethods.ss_runtime_get_frame_data((void*)Handles[index], Frames[index], &p, &l);
                OutPtr[index] = (IntPtr)p;
                OutLen[index] = (int)l;
            }
        }
    }
}

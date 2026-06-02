using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// UpdateManager が「全 Schedule → 全 Apply」の2フェーズで駆動する対象。
    /// </summary>
    internal interface ISpriteStudioRenderable
    {
        /// <summary>Phase 1: アニメを進め、Burst Job をスケジュールする (完了待ちはしない)。</summary>
        void TickAndSchedule(float deltaTime);

        /// <summary>Phase 2: Job 完了を待ち、メッシュへ反映・描画する。</summary>
        void ApplyAndRender();
    }

    /// <summary>
    /// 全 SpriteStudio レンダラーを集中管理し、Burst Job を「全インスタンス分まとめて Schedule →
    /// まとめて Complete」する。各コンポーネントが個別に Schedule→即 Complete すると Job が直列化するが、
    /// まとめることで Job がワーカースレッドで並列実行される (多数インスタンスで効果大)。
    ///
    /// play モードのみ集中管理する。edit モード (Graphic の ExecuteAlways プレビュー) は
    /// 各コンポーネントが自前で TickAndSchedule/ApplyAndRender を呼ぶ。
    /// </summary>
    public static class SpriteStudioUpdateManager
    {
        private static readonly List<ISpriteStudioRenderable> _renderables = new List<ISpriteStudioRenderable>();
        private static ISpriteStudioRenderable[] _tickBuffer = new ISpriteStudioRenderable[16];
        private static SpriteStudioUpdateRunner _runner;

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
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<SpriteStudioUpdateRunner>();
        }

        internal static void Schedule(float deltaTime)
        {
            int count = _renderables.Count;
            if (count == 0) return;

            // 反復中の Register/Unregister に備え、再利用バッファへスナップショットしてから処理する
            if (_tickBuffer.Length < count) _tickBuffer = new ISpriteStudioRenderable[Mathf.NextPowerOfTwo(count)];
            _renderables.CopyTo(0, _tickBuffer, 0, count);

            // Phase 1: 全インスタンスの Tick + Job スケジュール (ワーカースレッドへ投入)
            // この後 LateUpdate までの間、メインスレッドと並行してワーカースレッドで Job が実行されます。
            for (int i = 0; i < count; i++) _tickBuffer[i].TickAndSchedule(deltaTime);
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
    }
}

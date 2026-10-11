using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using SpriteStudio.SsRuntime;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// A target driven by the UpdateManager in two phases: "Schedule all -> Apply all".
    /// </summary>
    internal interface ISpriteStudioRenderable
    {
        /// <summary>Wait for the previous frame's Job + advance + settle drawFrame/skip.
        /// When out handle is Zero this is excluded from get_frame_data / ConsumeFrame (invalid or skip).</summary>
        void PrepareFrame(float deltaTime, out IntPtr handle, out float drawFrame);

        /// <summary>Consume the frame-data pointer obtained in parallel and schedule the Burst Job.</summary>
        void ConsumeFrame(IntPtr frameDataPtr, int len, float deltaTime);

        /// <summary>Phase 2: wait for the Job to finish, then apply to the mesh and render.</summary>
        void ApplyAndRender();

        /// <summary>The player ConsumeFrame builds, or null. The parallel pass steps its effect slots on the
        /// worker that fetches its frame data, and ConsumeFrame draws those steps.</summary>
        SpriteStudioInternalPlayer InternalPlayer { get; }

        /// <summary>The delta ConsumeFrame hands the player for its effects and instances.</summary>
        float ConsumeDeltaTime(float deltaTime);

        /// <summary>The player's current native handle, or Zero if it has since been disposed. Re-checked
        /// after the prepare pass so a player disposed by another player's event callback (fired from
        /// PollEvents during PrepareFrame) is dropped from the parallel get_frame_data set instead of
        /// leaving a freed handle for the job to dereference.</summary>
        IntPtr GetLiveHandle();
    }

    /// <summary>
    /// Centrally manages all SpriteStudio renderers and drives the per-frame update and render, all in
    /// one LateUpdate:
    ///   1. Serial: advance each player by 1 frame + decide drawFrame/frame-skip.
    ///   2. Parallel: run get_frame_data for non-skip players across workers via IJobParallelFor, and step
    ///      each player's effect slots on the same worker right after it. get_frame_data is independent per
    ///      context (no global mutable state) = concurrency-safe, and so is a slot's step, so the heaviest
    ///      pose/matrix/vertex/draw batch computation and the particle simulation are parallelized across
    ///      characters. Wait for completion at a barrier.
    ///   3. Serial: consume the obtained frame-data pointers and issue the mesh-build Burst Jobs, which
    ///      run on the workers while the main thread issues the rest.
    ///   4. Wait for all Jobs + apply to the meshes.
    /// LateUpdate, not Update: every Update, the Timeline (PlayableDirector, evaluated after Update) and
    /// the Animator have run by then, so what they set -- an animation switched, a frame sought, a
    /// keyframed property -- is drawn in the same frame, as it is on an Animator-driven SpriteRenderer.
    ///
    /// Central management runs in play mode only. In edit mode (the ExecuteAlways previews), each
    /// component builds its preview serially on its own, with ScheduleMeshUpdate and ApplyAndRender (separate from
    /// the parallel path).
    /// </summary>
    public static class SpriteStudioUpdateManager
    {
        private static readonly List<ISpriteStudioRenderable> _renderables = new List<ISpriteStudioRenderable>();
        private static ISpriteStudioRenderable[] _tickBuffer = new ISpriteStudioRenderable[32];
        private static int _tickCount = 0;
        private static SpriteStudioUpdateRunner _runner;

        // Persistent buffers for parallel get_frame_data (grow-only, steady-state zero GC).
        private static ISpriteStudioRenderable[] _pending = new ISpriteStudioRenderable[16];
        private static NativeArray<IntPtr> _jobHandles;
        private static NativeArray<float> _jobDrawFrames;
        private static NativeArray<IntPtr> _jobOutPtr;
        private static NativeArray<int> _jobOutLen;
        // The effect slots the parallel pass steps (grow-only): each pending player's first slot and slot
        // count, and every slot's input and result.
        private static NativeArray<int> _jobEffectStart;
        private static NativeArray<int> _jobEffectCount;
        private static NativeArray<SpriteStudioGeometryBuilder.EffectStep> _jobEffectSteps;
        private static NativeArray<ss_effect_step_result> _jobEffectResults;

        /// <summary>Whether to run get_frame_data in parallel across characters (false = full serial fallback = for A/B comparison / regression bisection).</summary>
        public static bool ParallelFrameData = true;
        /// <summary>Minimum number of players to parallelize. Below this, run serially to avoid worker wake-up cost.</summary>
        public static int ParallelMinCount = 2;
        /// <summary>IJobParallelFor's innerloopBatchCount. Each get_frame_data is heavy, so 1 is optimal
        /// (distributes each character independently across all cores via work-stealing). A larger value
        /// reduces parallelism with few characters.</summary>
        public static int ParallelBatch = 1;

        /// <summary>
        /// When "Enter Play Mode without Domain Reload" is enabled, static fields carry their values from
        /// the previous play session into the next play. Always reset at play start
        /// (SubsystemRegistration) so no stale renderable / tick snapshot from the prior session remains.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetStaticState()
        {
            _renderables.Clear();
            if (_tickBuffer != null) Array.Clear(_tickBuffer, 0, _tickBuffer.Length);
            if (_pending != null) Array.Clear(_pending, 0, _pending.Length);
            _tickCount = 0;
            _runner = null;
            // The NativeArrays are normally already disposed in Teardown (runner OnDestroy). If domain
            // reload is disabled and one somehow carried over still IsCreated, dispose it here so it is
            // rebuilt next frame.
            if (_jobHandles.IsCreated) _jobHandles.Dispose();
            if (_jobDrawFrames.IsCreated) _jobDrawFrames.Dispose();
            if (_jobOutPtr.IsCreated) _jobOutPtr.Dispose();
            if (_jobOutLen.IsCreated) _jobOutLen.Dispose();
            DisposeEffectStepBuffers();
        }

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
            if (_runner != null) return;        // Unity null check (recreate if destroyed)
            if (!Application.isPlaying) return;  // central management in play mode only (edit updates per component)

            var go = new GameObject("SpriteStudioUpdateRunner") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<SpriteStudioUpdateRunner>();
        }

        internal static void Schedule(float deltaTime)
        {
            int count = _renderables.Count;
            _tickCount = count;
            if (count == 0) return;

            // Guard against Register/Unregister during iteration: snapshot into a reused buffer first.
            if (_tickBuffer.Length < count) _tickBuffer = new ISpriteStudioRenderable[Mathf.NextPowerOfTwo(count)];
            _renderables.CopyTo(0, _tickBuffer, 0, count);
            EnsureJobCapacity(count);

            // Advance by 1 frame and settle drawFrame and frame-skip.
            // Pack only the non-skip, valid players into the parallel get_frame_data input.
            int n = 0;
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
            if (n == 0) return;

            // Now that the whole prepare pass (and all its callbacks) has run, drop any player whose
            // handle was invalidated in the middle of it.
            n = RevalidateAndCompact(_jobHandles, _jobDrawFrames, _pending, n);
            if (n == 0) return;

            // get_frame_data is independent per context (no global mutable state; shared resources are
            // immutable Send+Sync), so it can run on worker threads in parallel across characters. This
            // parallelizes the heaviest pose/matrix/vertex/draw batch/FlatBuffer build. The same worker then
            // steps the player's effect slots, each of which only its own player touches: particle simulation
            // is otherwise most of the main thread's time in a frame with effects. .Complete() makes the main
            // thread wait at the barrier.
            if (ParallelFrameData && n >= ParallelMinCount)
            {
                LayOutEffectSteps(n, deltaTime);
                new GetFrameDataJob
                {
                    Handles = _jobHandles, Frames = _jobDrawFrames,
                    OutPtr = _jobOutPtr, OutLen = _jobOutLen,
                    EffectStart = _jobEffectStart, EffectCount = _jobEffectCount,
                    EffectSteps = _jobEffectSteps, EffectResults = _jobEffectResults,
                }.Schedule(n, Mathf.Max(1, ParallelBatch)).Complete();
                for (int i = 0; i < n; i++)
                {
                    if (_jobEffectCount[i] > 0)
                        _pending[i].InternalPlayer._geometryBuilder.UseSteppedEffects(_jobEffectResults, _jobEffectStart[i]);
                }
            }
            else
            {
                // Serial fallback (few players or toggle off). Avoids worker wake-up cost / for A/B comparison.
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
            // Consume the obtained frame-data pointers and issue the mesh-build Jobs.
            for (int i = 0; i < n; i++)
            {
                _pending[i].ConsumeFrame(_jobOutPtr[i], _jobOutLen[i], deltaTime);
                _pending[i] = null; // do not keep the snapshot reference alive
            }
        }

        /// <summary>
        /// Drop from the parallel get_frame_data input set every player whose captured handle no longer
        /// matches its current live handle, keeping the survivors packed and index-aligned across the three
        /// parallel arrays (handle / drawFrame / renderable). Returns the surviving count; the dropped tail
        /// of <paramref name="pending"/> is nulled so no stale reference is kept alive.
        ///
        /// Why it is needed: a user event callback fired during a player's PrepareFrame (PollEvents) can
        /// disable or destroy a DIFFERENT player that was already prepared earlier in the pass, freeing its
        /// native handle while that handle still sits in the job set — get_frame_data would then
        /// dereference freed memory (use-after-free).
        /// </summary>
        internal static int RevalidateAndCompact(NativeArray<IntPtr> handles, NativeArray<float> drawFrames,
                                                 ISpriteStudioRenderable[] pending, int n)
        {
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                if (pending[i].GetLiveHandle() != handles[i]) continue;   // disposed / recreated: drop it
                if (valid != i)
                {
                    handles[valid] = handles[i];
                    drawFrames[valid] = drawFrames[i];
                    pending[valid] = pending[i];
                }
                valid++;
            }
            for (int i = valid; i < n; i++) pending[i] = null;
            return valid;
        }

        /// <summary>
        /// Lay out the effect slots of the first <paramref name="n"/> pending players for the parallel pass:
        /// each player's first slot and count, and every slot as ConsumeFrame would step it.
        /// </summary>
        private static void LayOutEffectSteps(int n, float deltaTime)
        {
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                var player = _pending[i].InternalPlayer;
                int count = player != null ? player._effectPartCount : 0;
                _jobEffectStart[i] = total;
                _jobEffectCount[i] = count;
                total += count;
            }
            if (!_jobEffectSteps.IsCreated || _jobEffectSteps.Length < total)
            {
                if (_jobEffectSteps.IsCreated) _jobEffectSteps.Dispose();
                if (_jobEffectResults.IsCreated) _jobEffectResults.Dispose();
                int cap = Mathf.NextPowerOfTwo(Mathf.Max(total, 16));
                _jobEffectSteps = new NativeArray<SpriteStudioGeometryBuilder.EffectStep>(cap, Allocator.Persistent);
                _jobEffectResults = new NativeArray<ss_effect_step_result>(cap, Allocator.Persistent);
            }
            for (int i = 0; i < n; i++)
            {
                if (_jobEffectCount[i] == 0) continue;
                _pending[i].InternalPlayer._geometryBuilder.WriteEffectSteps(
                    _pending[i].ConsumeDeltaTime(deltaTime), _jobEffectSteps, _jobEffectStart[i]);
            }
        }

        private static void EnsureJobCapacity(int count)
        {
            if (_jobHandles.IsCreated && _jobHandles.Length >= count && _pending.Length >= count) return;
            int cap = Mathf.NextPowerOfTwo(Mathf.Max(count, 16));
            if (_jobHandles.IsCreated) _jobHandles.Dispose();
            if (_jobDrawFrames.IsCreated) _jobDrawFrames.Dispose();
            if (_jobOutPtr.IsCreated) _jobOutPtr.Dispose();
            if (_jobOutLen.IsCreated) _jobOutLen.Dispose();
            if (_jobEffectStart.IsCreated) _jobEffectStart.Dispose();
            if (_jobEffectCount.IsCreated) _jobEffectCount.Dispose();
            _jobHandles = new NativeArray<IntPtr>(cap, Allocator.Persistent);
            _jobDrawFrames = new NativeArray<float>(cap, Allocator.Persistent);
            _jobOutPtr = new NativeArray<IntPtr>(cap, Allocator.Persistent);
            _jobOutLen = new NativeArray<int>(cap, Allocator.Persistent);
            _jobEffectStart = new NativeArray<int>(cap, Allocator.Persistent);
            _jobEffectCount = new NativeArray<int>(cap, Allocator.Persistent);
            if (_pending.Length < cap) _pending = new ISpriteStudioRenderable[cap];
        }

        private static void DisposeEffectStepBuffers()
        {
            if (_jobEffectStart.IsCreated) _jobEffectStart.Dispose();
            if (_jobEffectCount.IsCreated) _jobEffectCount.Dispose();
            if (_jobEffectSteps.IsCreated) _jobEffectSteps.Dispose();
            if (_jobEffectResults.IsCreated) _jobEffectResults.Dispose();
        }

        /// <summary>Dispose the persistent NativeArrays when play mode ends (called from the runner's OnDestroy).</summary>
        internal static void Teardown()
        {
            if (_jobHandles.IsCreated) _jobHandles.Dispose();
            if (_jobDrawFrames.IsCreated) _jobDrawFrames.Dispose();
            if (_jobOutPtr.IsCreated) _jobOutPtr.Dispose();
            if (_jobOutLen.IsCreated) _jobOutLen.Dispose();
            DisposeEffectStepBuffers();
            // Also release the managed-side snapshot / registrations so no stale reference carries over
            // (a safeguard when domain reload is disabled; paired with ResetStaticState).
            _renderables.Clear();
            if (_tickBuffer != null) Array.Clear(_tickBuffer, 0, _tickBuffer.Length);
            if (_pending != null) Array.Clear(_pending, 0, _pending.Length);
            _tickCount = 0;
            _runner = null;
        }

        internal static void Apply()
        {
            if (_tickCount == 0) return;

            // Phase 2: wait for every instance's Job + apply to the meshes.
            for (int i = 0; i < _tickCount; i++) _tickBuffer[i]?.ApplyAndRender();

            // Do not keep the snapshot references alive.
            for (int i = 0; i < _tickCount; i++) _tickBuffer[i] = null;
            _tickCount = 0;
        }
    }

    /// <summary>Hidden MonoBehaviour that drives the UpdateManager every frame (exactly one is created in play mode).</summary>
    // Run early so the tick, the apply and the part-tracker mirror complete before default-order user
    // scripts' LateUpdate: a user script reading a tracked Transform in its own LateUpdate then sees this
    // frame's value, not a stale one.
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("")]
    internal sealed class SpriteStudioUpdateRunner : MonoBehaviour
    {
        private void LateUpdate()
        {
            SpriteStudioUpdateManager.Schedule(Time.deltaTime);
            SpriteStudioUpdateManager.Apply();
        }

        private void OnDestroy()
        {
            // Dispose the persistent NativeArrays when play mode ends (avoids leak detection).
            SpriteStudioUpdateManager.Teardown();
        }
    }

    /// <summary>
    /// Runs get_frame_data in parallel across characters, then steps each character's effect slots.
    /// get_frame_data is independent per context (no global mutable state; shared resources are immutable
    /// Send+Sync), so concurrent calls are safe, and a slot's step touches only that slot and reads its own
    /// context. [BurstCompile] is not applied because it calls a DllImport (P/Invoke is not allowed from
    /// Burst). Each index writes only its own slot and its own range of effect results, so there is no
    /// aliasing.
    /// </summary>
    internal struct GetFrameDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<IntPtr> Handles;
        [ReadOnly] public NativeArray<float> Frames;
        [WriteOnly] public NativeArray<IntPtr> OutPtr;
        [WriteOnly] public NativeArray<int> OutLen;
        [ReadOnly] public NativeArray<int> EffectStart;
        [ReadOnly] public NativeArray<int> EffectCount;
        [ReadOnly] public NativeArray<SpriteStudioGeometryBuilder.EffectStep> EffectSteps;
        // Written at [EffectStart[index], +EffectCount[index]), not at [index].
        [NativeDisableParallelForRestriction] public NativeArray<ss_effect_step_result> EffectResults;

        public void Execute(int index)
        {
            unsafe
            {
                void* context = (void*)Handles[index];
                byte* p = null;
                nuint l = 0;
                NativeMethods.ss_runtime_get_frame_data(context, Frames[index], &p, &l);
                OutPtr[index] = (IntPtr)p;
                OutLen[index] = (int)l;

                // A frame with no data draws nothing, and ConsumeFrameData stops before it would step.
                int count = EffectCount[index];
                if (count == 0 || p == null || l == 0) return;
                int start = EffectStart[index];
                bool parentLooped = NativeMethods.ss_runtime_just_looped(context);
                RawPartState* parts = SpriteStudioGeometryBuilder.PartsOf(p, out int partCount);
                for (int k = 0; k < count; k++)
                {
                    EffectResults[start + k] = SpriteStudioGeometryBuilder.StepEffectSlot(
                        context, EffectSteps[start + k], Frames[index], parentLooped, parts, partCount);
                }
            }
        }
    }
}

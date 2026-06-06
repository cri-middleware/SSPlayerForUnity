using System;
using Unity.Collections;
using Unity.Jobs;

namespace SpriteStudio.Runtime
{
    public partial class SpriteStudioInternalPlayer
    {
        private struct InstanceChildState
        {
            public System.IntPtr InstanceSlot;
            public SpriteStudioInternalPlayer Player;
            public float PreviousFrame;
        }
        private InstanceChildState[] _instanceChildren;
        private NativeArray<InstanceMeshPointer> _instanceMeshes;
        // Instance 子の UpdateMeshJob ハンドルを束ねる依存配列。永続 grow-only (A3: 毎フレ Temp 確保を回避)。
        private NativeArray<JobHandle> _childHandles;
        // effect/instance スロットを持つパーツ index の疎なリスト (SetupSlots で確定)。毎フレーム partsLength
        // 全スキャンせず、これらだけを処理することで通常パーツ多数時 (例 448 中 effect 数個) の空回りを避ける。
        private int[] _instancePartIndices;
        private int _instancePartCount;
        // この SSAB に Instance パートが 1 つでもあるか。
        private bool _hasInstanceParts;
    }
}

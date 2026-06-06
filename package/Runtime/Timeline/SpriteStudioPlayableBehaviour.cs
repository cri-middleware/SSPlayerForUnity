using System;
using UnityEngine.Playables;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// SpriteStudioTrack の 1 クリップに対応する PlayableBehaviour。
    /// 再生する SpriteStudio アニメーション名を保持する (実際の駆動は SpriteStudioMixerBehaviour が行う)。
    /// </summary>
    [Serializable]
    public class SpriteStudioPlayableBehaviour : PlayableBehaviour
    {
        public string AnimationName;
    }
}

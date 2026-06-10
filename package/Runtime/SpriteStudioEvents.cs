using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace SpriteStudio.Runtime
{
    [Serializable]
    public struct SpriteStudioUserData
    {
        public string PartName;
        public int PartIndex;
        
        public bool HasInt;
        public int IntValue;
        
        public bool HasString;
        public string StringValue;
        
        public bool HasRect;
        public Rect RectValue;
        
        public bool HasPoint;
        public Vector2 PointValue;
    }

    [Serializable]
    public class UserDataUnityEvent : UnityEvent<SpriteStudioUserData> {}

    [Serializable]
    public struct SpriteStudioSignal
    {
        public string PartName;
        public int PartIndex;
        public bool Active;
        public string CommandId;

        /// <summary>
        /// Signal コマンドのパラメータ (param_id -> 値)。値の実体は bool / int / float / string のいずれかで、
        /// FlatBuffers の SignalCommandParamValue union に対応する。パラメータが無い場合は null。
        /// Godot 実装 (onSignal が渡す Dictionary) と等価。UnityEvent ではシリアライズされず実行時のみ有効。
        /// </summary>
        [NonSerialized] public Dictionary<string, object> Params;
    }

    [Serializable]
    public class SignalUnityEvent : UnityEvent<SpriteStudioSignal> {}

    [Serializable]
    public struct SpriteStudioAudio
    {
        public string PartName;
        public int PartIndex;
        public uint SoundListNameHash;
        public uint SoundNameHash;
        public string SoundName;
        public int LoopNum;
    }

    [Serializable]
    public class AudioUnityEvent : UnityEvent<SpriteStudioAudio> {}
}

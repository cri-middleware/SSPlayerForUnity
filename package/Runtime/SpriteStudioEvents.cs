using System;
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

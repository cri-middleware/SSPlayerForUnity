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
        /// Signal command parameters (param_id -> value). Each value is one of bool / int / float / string,
        /// corresponding to the FlatBuffers SignalCommandParamValue union. Null when there are no parameters.
        /// Not serialized by UnityEvent; valid only at runtime.
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

    /// <summary>
    /// Represents ssab sound metadata (equivalent to ssse) with the minimum needed for playback. Opt-in
    /// information that supplements what the SpriteStudioAudio (firing payload) does not carry, such as
    /// file_path / duration. Returned by AnimeAsset.TryGetSoundInfo() under the same hash key as SpriteStudioAudio.
    /// </summary>
    public struct SpriteStudioSoundInfo
    {
        public string Alias;        // SoundFile.name (alias)
        public string FilePath;     // SoundFile.file_path  e.g. "sound/foo.acb"
        public uint   FilePathHash; // SoundFile.file_path_hash
        public uint   TimeTotalMs;  // SoundFile.time_total
    }

}

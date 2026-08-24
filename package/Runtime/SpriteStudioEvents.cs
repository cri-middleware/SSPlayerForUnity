using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace SpriteStudio.Runtime
{
    [Serializable]
    public struct SpriteStudioUserData
    {
        /// <summary>
        /// The frame the key is authored on — not necessarily the frame it was noticed on,
        /// since one tick can step across several.
        /// </summary>
        public int FrameNo;
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

    /// <summary>Type tag for a <see cref="SpriteStudioSignalParam"/> value (mirrors the FlatBuffers
    /// SignalCommandParamValue union).</summary>
    public enum SpriteStudioSignalParamType : byte { Bool, Index, Integer, Floating, Text }

    /// <summary>
    /// A single Signal command parameter: an id and a typed value, stored without boxing. Read the value
    /// through the accessor matching <see cref="Type"/> (e.g. <see cref="AsInteger"/> when Type is Integer).
    /// </summary>
    public readonly struct SpriteStudioSignalParam
    {
        public readonly string Id;
        public readonly SpriteStudioSignalParamType Type;
        private readonly bool _bool;
        private readonly int _int;      // Index or Integer
        private readonly float _float;
        private readonly string _text;

        internal SpriteStudioSignalParam(string id, bool value) { Id = id; Type = SpriteStudioSignalParamType.Bool; _bool = value; _int = 0; _float = 0f; _text = null; }
        internal SpriteStudioSignalParam(string id, SpriteStudioSignalParamType type, int value) { Id = id; Type = type; _int = value; _bool = false; _float = 0f; _text = null; }
        internal SpriteStudioSignalParam(string id, float value) { Id = id; Type = SpriteStudioSignalParamType.Floating; _float = value; _bool = false; _int = 0; _text = null; }
        internal SpriteStudioSignalParam(string id, string value) { Id = id; Type = SpriteStudioSignalParamType.Text; _text = value; _bool = false; _int = 0; _float = 0f; }

        /// <summary>Value when <see cref="Type"/> is Bool.</summary>
        public bool AsBool => _bool;
        /// <summary>Value when <see cref="Type"/> is Index.</summary>
        public int AsIndex => _int;
        /// <summary>Value when <see cref="Type"/> is Integer.</summary>
        public int AsInteger => _int;
        /// <summary>Value when <see cref="Type"/> is Floating.</summary>
        public float AsFloating => _float;
        /// <summary>Value when <see cref="Type"/> is Text.</summary>
        public string AsText => _text;
    }

    [Serializable]
    public struct SpriteStudioSignal
    {
        /// <summary>
        /// The frame the key is authored on — not necessarily the frame it was noticed on,
        /// since one tick can step across several.
        /// </summary>
        public int FrameNo;
        public string PartName;
        public int PartIndex;
        public bool Active;
        public string CommandId;

        // Backing store for the parameters, owned and reused by the player (grow-only, no per-signal
        // allocation, no boxing). Access parameters via ParamCount / GetParam / TryGetParam — never these
        // fields directly. A signal's slice is valid until the next event poll on the same player; copy any
        // values you need to keep beyond the OnSignal callback.
        [NonSerialized] internal SpriteStudioSignalParam[] _params;
        [NonSerialized] internal int _paramStart;

        /// <summary>
        /// Number of command parameters carried by this signal (0 if none). Not serialized.
        /// Read-only from outside the package: the count indexes a player-owned buffer, so a signal that
        /// did not come from an event poll always reports 0 rather than pointing at nothing.
        /// </summary>
        public int ParamCount { get; internal set; }

        /// <summary>
        /// The command parameter at <paramref name="index"/> (0 &lt;= index &lt; <see cref="ParamCount"/>).
        /// Zero-allocation and un-boxed. Valid until the next event poll on this player.
        /// </summary>
        public SpriteStudioSignalParam GetParam(int index)
        {
            if (_params == null || (uint)index >= (uint)ParamCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _params[_paramStart + index];
        }

        /// <summary>
        /// Find a parameter by its id. Returns false when this signal has no parameter with that id.
        /// If several parameters share an id, the first one is returned; use <see cref="GetParam"/> with
        /// <see cref="ParamCount"/> to enumerate all parameters positionally.
        /// </summary>
        public bool TryGetParam(string id, out SpriteStudioSignalParam param)
        {
            if (_params == null)
            {
                param = default;
                return false;
            }
            for (int i = 0; i < ParamCount; i++)
            {
                var p = _params[_paramStart + i];
                if (p.Id == id) { param = p; return true; }
            }
            param = default;
            return false;
        }
    }

    [Serializable]
    public class SignalUnityEvent : UnityEvent<SpriteStudioSignal> {}

    [Serializable]
    public struct SpriteStudioAudio
    {
        /// <summary>
        /// The frame the key is authored on — not necessarily the frame it was noticed on,
        /// since one tick can step across several.
        /// </summary>
        public int FrameNo;
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

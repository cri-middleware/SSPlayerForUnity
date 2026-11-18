using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace SpriteStudio.Runtime
{
    /// <summary>A user-data key the playhead reached, as <c>OnUserData</c> reports it.</summary>
    [Serializable]
    public struct SpriteStudioUserData
    {
        /// <summary>
        /// The frame the key is authored on — not necessarily the frame it was noticed on,
        /// since one tick can step across several.
        /// </summary>
        public int FrameNo;
        /// <summary>The part the key is on.</summary>
        public string PartName;
        /// <summary>That part's index, as <c>FindPartIndex</c> returns it.</summary>
        public int PartIndex;
        /// <summary>
        /// The source that fired the event: a handle as <c>GetTrackMainSource</c> and <c>AddTrackSource</c>
        /// return them. Without the track stack in use it is always track 0's main source.
        /// </summary>
        public int Source;

        /// <summary>Whether the key carries an integer, in <see cref="IntValue"/>.</summary>
        public bool HasInt;
        /// <summary>The integer, when <see cref="HasInt"/> is true.</summary>
        public int IntValue;

        /// <summary>Whether the key carries a string, in <see cref="StringValue"/>.</summary>
        public bool HasString;
        /// <summary>The string, when <see cref="HasString"/> is true.</summary>
        public string StringValue;

        /// <summary>Whether the key carries a rectangle, in <see cref="RectValue"/>.</summary>
        public bool HasRect;
        /// <summary>The rectangle, when <see cref="HasRect"/> is true.</summary>
        public Rect RectValue;

        /// <summary>Whether the key carries a point, in <see cref="PointValue"/>.</summary>
        public bool HasPoint;
        /// <summary>The point, when <see cref="HasPoint"/> is true.</summary>
        public Vector2 PointValue;
    }

    /// <summary>The <see cref="SpriteStudioUserData"/> event, for wiring up in the Inspector.</summary>
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

    /// <summary>A signal key the playhead reached, as <c>OnSignal</c> reports it.</summary>
    [Serializable]
    public struct SpriteStudioSignal
    {
        /// <summary>
        /// The frame the key is authored on — not necessarily the frame it was noticed on,
        /// since one tick can step across several.
        /// </summary>
        public int FrameNo;
        /// <summary>The part the key is on.</summary>
        public string PartName;
        /// <summary>That part's index, as <c>FindPartIndex</c> returns it.</summary>
        public int PartIndex;
        /// <summary>
        /// The source that fired the event: a handle as <c>GetTrackMainSource</c> and <c>AddTrackSource</c>
        /// return them. Without the track stack in use it is always track 0's main source.
        /// </summary>
        public int Source;
        /// <summary>Always true: an inactive signal is not reported.</summary>
        public bool Active;
        /// <summary>The command id set on the key.</summary>
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

    /// <summary>The <see cref="SpriteStudioSignal"/> event, for wiring up in the Inspector.</summary>
    [Serializable]
    public class SignalUnityEvent : UnityEvent<SpriteStudioSignal> {}

    /// <summary>An audio key the playhead reached, as <c>OnAudio</c> reports it.</summary>
    [Serializable]
    public struct SpriteStudioAudio
    {
        /// <summary>
        /// The frame the key is authored on — not necessarily the frame it was noticed on,
        /// since one tick can step across several.
        /// </summary>
        public int FrameNo;
        /// <summary>The part the key is on.</summary>
        public string PartName;
        /// <summary>That part's index, as <c>FindPartIndex</c> returns it.</summary>
        public int PartIndex;
        /// <summary>
        /// The source that fired the event: a handle as <c>GetTrackMainSource</c> and <c>AddTrackSource</c>
        /// return them. Without the track stack in use it is always track 0's main source.
        /// </summary>
        public int Source;
        /// <summary>Hash of the sound list's name.</summary>
        public uint SoundListNameHash;
        /// <summary>Hash of the sound's name.</summary>
        public uint SoundNameHash;
        /// <summary>The sound's name.</summary>
        public string SoundName;
        /// <summary>How many times the sound plays.</summary>
        public int LoopNum;
    }

    /// <summary>The <see cref="SpriteStudioAudio"/> event, for wiring up in the Inspector.</summary>
    [Serializable]
    public class AudioUnityEvent : UnityEvent<SpriteStudioAudio> {}

}

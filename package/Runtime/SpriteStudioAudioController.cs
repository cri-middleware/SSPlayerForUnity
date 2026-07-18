using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Built-in audio playback helper embedded in Player / Graphic (a plain C# class, not a MonoBehaviour).
    /// Routes one audio event to the override hook (SpriteStudioAudioBackend) if present, otherwise to
    /// built-in AudioClip playback.
    ///
    /// Resolution order: per-object backend > global backend > built-in (ResolveAudioClip).
    /// Built-in playback **manages active sounds per part**. The identity key = (PartIndex,
    /// SoundListNameHash+SoundNameHash), with at most one voice per key. If the same key fires again,
    /// the previous one is stopped and replayed (to prevent duplicate playback). loop_num is the play count:
    ///   * 1        : one-shot (play once).
    ///   * &gt;= 2  : play N times.
    ///   * &lt;= 0  : cannot occur per spec (SS cannot specify an infinite loop). Defensively does not play.
    /// AudioSource is added to the owner on demand and reused from a pool after it finishes.
    /// Does not play during reverse playback (the forward-playback filter is the caller's responsibility, i.e. Player/Graphic).
    /// </summary>
    public sealed class SpriteStudioAudioController
    {
        private readonly GameObject _owner;

        // Active sounds per part: identity key -> voice. At most one voice per key.
        private readonly Dictionary<VoiceKey, Voice> _voices = new Dictionary<VoiceKey, Voice>();
        private readonly Stack<AudioSource> _pool = new Stack<AudioSource>();   // Reuse of returned AudioSources
        private List<VoiceKey> _scratch;                                        // Temp buffer to avoid removing while enumerating

        public SpriteStudioAudioController(GameObject owner)
        {
            _owner = owner;
        }

        /// <summary>Plays one audio event (the built-in path dedups per part).</summary>
        public void Play(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset,
                         SpriteStudioAudioBackend backend, float volume)
        {
            // Override hook: per-object > global. If either exists, built-in playback is skipped (the backend also owns play-count management).
            var be = backend != null ? backend : SpriteStudioAudioBackend.Global;
            if (be != null)
            {
                be.PlayAudio(in ev, asset, _owner);
                return;
            }

            // loop_num is the play count. Per spec <= 0 cannot occur (SS cannot specify an infinite loop) -> defensively do not play.
            if (ev.LoopNum <= 0) return;

            // Built-in: resolve the imported AudioClip. Unsupported/unresolved is safely silent.
            var clip = asset != null ? asset.ResolveAudioClip(ev.SoundListNameHash, ev.SoundNameHash) : null;
            if (clip == null) return;

            ReclaimFinished();   // Lazily reclaim finished voices (no per-frame tick needed)

            var key = new VoiceKey(ev.PartIndex, ev.SoundListNameHash, ev.SoundNameHash);
            if (_voices.TryGetValue(key, out var voice))
            {
                // Refire (same key): stop the previous one and replay (prevents duplicate playback).
                StartVoice(voice, clip, volume, ev.LoopNum);
                return;
            }

            var v = new Voice { Source = Rent() };
            StartVoice(v, clip, volume, ev.LoopNum);
            _voices[key] = v;
        }

        /// <summary>Stops loop sounds (call on animation switch). One-shots are left to end on their own and are not stopped.</summary>
        public void StopLoops() => Sweep(loopsOnly: true);

        /// <summary>Stops all sounds (call on disable / destroy).</summary>
        public void StopAll() => Sweep(loopsOnly: false);

        /// <summary>Pauses all playing sounds (synced with animation pause).</summary>
        public void PauseAll()
        {
            foreach (var kv in _voices) kv.Value.Source.Pause();
        }

        /// <summary>Resumes paused sounds (synced with animation resume / play).</summary>
        public void ResumeAll()
        {
            foreach (var kv in _voices) kv.Value.Source.UnPause();
        }

        // --- Internal ---------------------------------------------------------

        // (Re)starts a voice according to loop_num. Stops the existing Source, then replays it.
        private static void StartVoice(Voice v, AudioClip clip, float volume, int loopNum)
        {
            var src = v.Source;
            src.Stop();
            src.clip = clip;
            src.volume = volume;
            double dsp = AudioSettings.dspTime;

            if (loopNum == 1)
            {
                // One-shot (play once). Self-terminating.
                src.loop = false;
                src.PlayScheduled(dsp);
                v.PlaysOnce = true;
                v.EndDsp = dsp + (double)clip.length;
            }
            else
            {
                // Play N times (N >= 2). Schedule the end at N lengths. Self-terminating.
                src.loop = true;
                src.PlayScheduled(dsp);
                double end = dsp + (double)clip.length * loopNum;
                src.SetScheduledEndTime(end);
                v.PlaysOnce = false;
                v.EndDsp = end;
            }
        }

        // Reclaims voices (one-shot / finite) whose scheduled end has passed and returns them to the pool. Swept only on demand, so there is no idle cost.
        private void ReclaimFinished()
        {
            if (_voices.Count == 0) return;
            EnsureScratch();

            double now = AudioSettings.dspTime;
            foreach (var kv in _voices)
            {
                if (now >= kv.Value.EndDsp)   // All voices self-terminate (none are infinite)
                    _scratch.Add(kv.Key);
            }
            FreeScratch();
        }

        // If loopsOnly=true, stops only loops (non-one-shot); if false, stops all voices and returns them to the pool.
        private void Sweep(bool loopsOnly)
        {
            if (_voices.Count == 0) return;
            EnsureScratch();

            foreach (var kv in _voices)
            {
                if (loopsOnly && kv.Value.PlaysOnce) continue;   // Keep one-shots (self-terminating)
                _scratch.Add(kv.Key);
            }
            FreeScratch();
        }

        private void EnsureScratch()
        {
            if (_scratch == null) _scratch = new List<VoiceKey>();
            _scratch.Clear();
        }

        private void FreeScratch()
        {
            for (int i = 0; i < _scratch.Count; i++)
            {
                var k = _scratch[i];
                Free(_voices[k].Source);
                _voices.Remove(k);
            }
        }

        private AudioSource Rent()
        {
            AudioSource src = _pool.Count > 0 ? _pool.Pop() : null;
            if (src == null)
            {
                src = _owner.AddComponent<AudioSource>();
                src.playOnAwake = false;
            }
            return src;
        }

        private void Free(AudioSource src)
        {
            if (src == null) return;
            src.Stop();
            src.clip = null;
            _pool.Push(src);
        }

        // Uses (PartIndex, (SoundListNameHash << 32) | SoundNameHash) as the identity key.
        // Implements IEquatable to avoid boxing when used as a Dictionary key (zero GC).
        private readonly struct VoiceKey : System.IEquatable<VoiceKey>
        {
            private readonly int _part;
            private readonly ulong _sound;

            public VoiceKey(int part, uint listHash, uint nameHash)
            {
                _part = part;
                _sound = ((ulong)listHash << 32) | nameHash;
            }

            public bool Equals(VoiceKey o) => _part == o._part && _sound == o._sound;
            public override bool Equals(object o) => o is VoiceKey k && Equals(k);
            public override int GetHashCode() => (_part * 397) ^ _sound.GetHashCode();
        }

        private sealed class Voice
        {
            public AudioSource Source;
            public bool PlaysOnce;   // loop_num == 1 (one-shot. Not stopped by StopLoops)
            public double EndDsp;    // Scheduled end dspTime (all voices self-terminate).
        }
    }
}

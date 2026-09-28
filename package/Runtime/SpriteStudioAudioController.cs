using System.Collections.Generic;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Built-in audio playback for Player / Graphic (a plain C# class, not a MonoBehaviour): plays the
    /// AudioClip an audio event names, loop_num times, on an AudioSource of the owner's.
    ///
    /// A sound that has started plays to its end. A key crossed again while its sound still plays starts
    /// another voice rather than cutting the first, and only <see cref="StopAll"/> -- the owner's teardown,
    /// or the host asking for silence -- stops one. loop_num is the play count (SpriteStudio has no infinite
    /// audio loop), so every voice ends by itself; &lt;= 0 cannot occur and does not play. Whether an event
    /// plays at all (forward playback, not a seek's landing frame) is the caller's decision.
    /// AudioSources are added to the owner on demand and reused once their sound has ended.
    /// </summary>
    internal sealed class SpriteStudioAudioController
    {
        private readonly GameObject _owner;
        private readonly List<Voice> _voices = new List<Voice>();
        private readonly Stack<AudioSource> _pool = new Stack<AudioSource>();

        public SpriteStudioAudioController(GameObject owner)
        {
            _owner = owner;
        }

        /// <summary>Plays one audio event's sound, loop_num times.</summary>
        public void Play(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset, float volume)
        {
            if (ev.LoopNum <= 0) return;

            // Resolve the imported AudioClip. Unsupported / unresolved is silent.
            var clip = asset != null ? asset.ResolveAudioClip(ev.SoundListNameHash, ev.SoundNameHash) : null;
            if (clip == null) return;

            ReclaimFinished();   // lazily, so no per-frame tick is needed

            var src = Rent();
            src.clip = clip;
            src.volume = volume;
            double start = AudioSettings.dspTime;
            double end = start + (double)clip.length * ev.LoopNum;
            // An AudioSource has no play count: loop the clip and end it after loop_num lengths.
            src.loop = ev.LoopNum > 1;
            src.PlayScheduled(start);
            if (src.loop) src.SetScheduledEndTime(end);
            _voices.Add(new Voice { Source = src, EndDsp = end });
        }

        /// <summary>
        /// Stops every sound. Teardown and the host's call for silence use it; the transport does not --
        /// Pause / Resume / Stop and an animation switch leave a sound that has started to play out.
        /// </summary>
        public void StopAll()
        {
            for (int i = 0; i < _voices.Count; i++) Free(_voices[i].Source);
            _voices.Clear();
        }

        private void ReclaimFinished()
        {
            double now = AudioSettings.dspTime;
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                if (now < _voices[i].EndDsp) continue;
                Free(_voices[i].Source);
                _voices.RemoveAt(i);
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

        private struct Voice
        {
            public AudioSource Source;
            public double EndDsp;    // when the last play ends
        }
    }
}

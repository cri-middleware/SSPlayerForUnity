using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Override hook for audio playback (ScriptableObject). By default Player/Graphic performs built-in
    /// AudioClip one-shot playback, but to route to a custom audio backend (such as audio middleware),
    /// derive from this class, implement PlayAudio, and swap it in via one of the following:
    ///   - Whole project: assign to <see cref="Global"/> at startup (applies to all Player/Graphic).
    ///   - Per object:    assign to the "Audio Backend" field of a Player/Graphic.
    ///
    /// Designers need to configure nothing (leave null for built-in playback). Advanced users only need to swap in one place.
    /// If file_path / alias etc. are needed, they can be looked up via asset.TryGetSoundInfo().
    /// </summary>
    public abstract class SpriteStudioAudioBackend : ScriptableObject
    {
        /// <summary>
        /// Project-wide default backend. Used when the per-object side has no explicit assignment.
        /// Intended to be set once at startup (reset by the Editor's domain reload).
        /// </summary>
        public static SpriteStudioAudioBackend Global { get; set; }

        /// <summary>
        /// Plays one audio event.
        /// </summary>
        /// <param name="ev">The fired audio event (alias / hash / loop_num).</param>
        /// <param name="asset">The originating AnimeAsset. Usable with ResolveAudioClip / TryGetSoundInfo.</param>
        /// <param name="source">The originating GameObject (usable as the anchor for 3D positioning or pool management).</param>
        public abstract void PlayAudio(in SpriteStudioAudio ev, SpriteStudioAnimeAsset asset, GameObject source);
    }
}

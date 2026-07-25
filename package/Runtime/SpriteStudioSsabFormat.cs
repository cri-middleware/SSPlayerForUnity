namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Checks on a raw .ssab buffer, shared by the importer (Editor) and the runtime loader so the two can
    /// never disagree about what is loadable.
    /// </summary>
    internal static class SpriteStudioSsabFormat
    {
        // Reading the FlatBuffers file identifier needs the leading root offset (4 bytes) + the magic
        // (4 bytes), so a shorter buffer cannot carry one.
        private const int MinimumLength = 8;

        /// <summary>
        /// Whether the buffer carries the "SSAB" FlatBuffers file identifier the converter writes — i.e. it
        /// is converter output rather than an unrelated file that merely has the .ssab extension.
        /// A format sanity check only, not a guarantee that the payload is internally well-formed: a full
        /// structural verification is deliberately not used because the bundled FlatBuffers verifier
        /// mis-handles buffers larger than ~32 KB and would reject valid assets.
        /// </summary>
        internal static bool IsSsabBuffer(byte[] ssabData)
        {
            return ssabData != null
                && ssabData.Length >= MinimumLength
                && ss.format.SsAnimeBinary.SsAnimeBinaryBufferHasIdentifier(new Google.FlatBuffers.ByteBuffer(ssabData));
        }

        /// <summary>
        /// Read the pack name hash (SsAnimeBinary.name_hash — the same value the importer stores as
        /// <see cref="SpriteStudioAnimeAsset.PackHash"/>) out of a raw .ssab buffer.
        /// False when the buffer is not an .ssab.
        /// </summary>
        internal static bool TryGetPackHash(byte[] ssabData, out uint packHash)
        {
            packHash = 0;
            if (!IsSsabBuffer(ssabData)) return false;
            var bb = new Google.FlatBuffers.ByteBuffer(ssabData);
            packHash = ss.format.SsAnimeBinary.GetRootAsSsAnimeBinary(bb).NameHash;
            return true;
        }
    }
}

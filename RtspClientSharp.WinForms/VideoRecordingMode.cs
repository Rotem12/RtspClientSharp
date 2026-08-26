namespace RtspClientSharp.WinForms
{
    /// <summary>
    /// Selects the video recording path used by <see cref="RtspVideoControl"/>.
    /// </summary>
    public enum VideoRecordingMode
    {
        /// <summary>Use the compressed remux path.</summary>
        Auto = 0,

        /// <summary>
        /// Write the received H.264/H.265 access units without decoding or re-encoding.
        /// </summary>
        CompressedRemux,

        /// <summary>
        /// Decode frames and send bitmaps to the configured bitmap recorder.
        /// </summary>
        BitmapFallback,
    }
}

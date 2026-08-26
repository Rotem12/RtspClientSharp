namespace RtspClientSharp.Recording
{
    /// <summary>
    /// Selects the container used by the compressed video recorder.
    /// </summary>
    public enum CompressedVideoRecordingFormat
    {
        /// <summary>Choose MPEG-TS unless an explicit elementary-stream extension is used.</summary>
        Auto = 0,

        /// <summary>Write a transport-neutral MPEG-TS file containing the compressed video.</summary>
        MpegTs,

        /// <summary>Write the source Annex-B bytes without a container.</summary>
        AnnexB
    }
}

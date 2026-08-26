using System;

namespace RtspClientSharp.Recording
{
    public sealed class CompressedVideoRecorderOptions
    {
        /// <summary>
        /// Output format. Auto chooses MPEG-TS for normal paths and Annex-B for
        /// explicit .h264/.h265 elementary-stream paths.
        /// </summary>
        public CompressedVideoRecordingFormat Format { get; set; } =
            CompressedVideoRecordingFormat.Auto;

        /// <summary>
        /// Optional fallback duration used when source timestamps are absent or
        /// repeat. The normal RTP/RTSP/TS timestamp is preferred.
        /// </summary>
        public TimeSpan? FrameDuration { get; set; }
    }
}

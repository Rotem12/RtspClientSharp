namespace RtspClientSharp
{
    /// <summary>
    /// Media packet format consumed by the direct RtpClient.
    /// </summary>
    public enum MediaTransportMode
    {
        /// <summary>Detect RTP or raw MPEG-TS from the first received datagram.</summary>
        Auto,

        /// <summary>Datagrams contain RTP packets.</summary>
        Rtp,

        /// <summary>Datagrams contain raw MPEG-TS data.</summary>
        MpegTs
    }
}

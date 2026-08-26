namespace RtspClientSharp.WinForms
{
    /// <summary>
    /// Selects how a video control obtains media. Auto selects RTSP for rtsp/http
    /// URIs and direct UDP for udp URIs.
    /// </summary>
    public enum VideoSourceType
    {
        Auto,
        Rtsp,
        DirectUdp
    }
}

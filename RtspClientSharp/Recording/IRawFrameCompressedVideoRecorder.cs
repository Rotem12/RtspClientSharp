using RtspClientSharp.RawFrames;

namespace RtspClientSharp.Recording
{
    /// <summary>
    /// Optional fast path for recorders that can consume a raw frame while the
    /// transport-owned frame buffer is still valid. Implementations must finish
    /// reading the frame before this call returns.
    /// </summary>
    public interface IRawFrameCompressedVideoRecorder
    {
        void WriteRawFrame(RawFrame frame);
    }
}

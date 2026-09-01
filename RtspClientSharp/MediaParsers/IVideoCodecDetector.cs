using RtspClientSharp.Codecs.Video;

namespace RtspClientSharp.MediaParsers
{
    interface IVideoCodecDetector
    {
        CodecInfoType DetectedVideoCodec { get; }
    }
}

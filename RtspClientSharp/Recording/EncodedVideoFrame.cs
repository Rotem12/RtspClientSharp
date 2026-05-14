using System;

namespace RtspClientSharp.Recording
{
    public sealed class EncodedVideoFrame
    {
        public EncodedVideoFrame(DateTime timestamp, EncodedVideoCodec codec, bool isKeyFrame,
            byte[] frameBytes, byte[] codecParametersBytes = null)
        {
            Timestamp = timestamp;
            Codec = codec;
            IsKeyFrame = isKeyFrame;
            FrameBytes = frameBytes ?? throw new ArgumentNullException(nameof(frameBytes));
            CodecParametersBytes = codecParametersBytes ?? Array.Empty<byte>();
        }

        public DateTime Timestamp { get; }
        public EncodedVideoCodec Codec { get; }
        public bool IsKeyFrame { get; }
        public byte[] FrameBytes { get; }
        public byte[] CodecParametersBytes { get; }
        public bool HasCodecParameters => CodecParametersBytes.Length != 0;
    }
}

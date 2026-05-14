using System;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.Recording
{
    public static class EncodedVideoFrameFactory
    {
        public static bool TryCreate(RawFrame rawFrame, out EncodedVideoFrame encodedFrame)
        {
            if (rawFrame == null)
                throw new ArgumentNullException(nameof(rawFrame));

            encodedFrame = null;

            if (!(rawFrame is RawVideoFrame videoFrame))
                return false;

            EncodedVideoCodec codec;
            bool isKeyFrame;
            byte[] codecParametersBytes = Array.Empty<byte>();

            switch (videoFrame)
            {
                case RawH264IFrame h264IFrame:
                    codec = EncodedVideoCodec.H264;
                    isKeyFrame = true;
                    codecParametersBytes = CopySegment(h264IFrame.SpsPpsSegment);
                    break;
                case RawH264Frame _:
                    codec = EncodedVideoCodec.H264;
                    isKeyFrame = false;
                    break;
                case RawH265IFrame h265IFrame:
                    codec = EncodedVideoCodec.H265;
                    isKeyFrame = true;
                    codecParametersBytes = CopySegment(h265IFrame.ParametersBytesSegment);
                    break;
                case RawH265Frame _:
                    codec = EncodedVideoCodec.H265;
                    isKeyFrame = false;
                    break;
                case RawJpegFrame _:
                    codec = EncodedVideoCodec.Mjpeg;
                    isKeyFrame = true;
                    break;
                default:
                    return false;
            }

            encodedFrame = new EncodedVideoFrame(videoFrame.Timestamp, codec, isKeyFrame,
                CopySegment(videoFrame.FrameSegment), codecParametersBytes);

            return true;
        }

        private static byte[] CopySegment(ArraySegment<byte> segment)
        {
            if (segment.Array == null || segment.Count == 0)
                return Array.Empty<byte>();

            var bytes = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, bytes, 0, segment.Count);
            return bytes;
        }
    }
}

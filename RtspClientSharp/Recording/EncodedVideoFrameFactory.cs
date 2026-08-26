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

            if (!TryDescribe(rawFrame, out EncodedVideoCodec codec, out bool isKeyFrame,
                out ArraySegment<byte> frameSegment, out ArraySegment<byte> codecParametersSegment))
                return false;

            var videoFrame = (RawVideoFrame)rawFrame;
            encodedFrame = new EncodedVideoFrame(videoFrame.Timestamp, codec, isKeyFrame,
                CopySegment(frameSegment), CopySegment(codecParametersSegment));

            return true;
        }

        /// <summary>
        /// Describes a raw video frame without allocating or copying its payload.
        /// The returned segments are valid only for the duration of the current
        /// raw-frame callback unless the caller owns the frame buffers.
        /// </summary>
        public static bool TryDescribe(RawFrame rawFrame, out EncodedVideoCodec codec,
            out bool isKeyFrame, out ArraySegment<byte> frameSegment,
            out ArraySegment<byte> codecParametersSegment)
        {
            if (rawFrame == null)
                throw new ArgumentNullException(nameof(rawFrame));

            codec = EncodedVideoCodec.Unknown;
            isKeyFrame = false;
            frameSegment = default(ArraySegment<byte>);
            codecParametersSegment = default(ArraySegment<byte>);

            if (!(rawFrame is RawVideoFrame videoFrame))
                return false;

            frameSegment = videoFrame.FrameSegment;
            codecParametersSegment = new ArraySegment<byte>(Array.Empty<byte>());

            switch (videoFrame)
            {
                case RawH264IFrame h264IFrame:
                    codec = EncodedVideoCodec.H264;
                    isKeyFrame = true;
                    codecParametersSegment = h264IFrame.SpsPpsSegment;
                    return true;
                case RawH264Frame _:
                    codec = EncodedVideoCodec.H264;
                    return true;
                case RawH265IFrame h265IFrame:
                    codec = EncodedVideoCodec.H265;
                    isKeyFrame = true;
                    codecParametersSegment = h265IFrame.ParametersBytesSegment;
                    return true;
                case RawH265Frame _:
                    codec = EncodedVideoCodec.H265;
                    return true;
                case RawJpegFrame _:
                    codec = EncodedVideoCodec.Mjpeg;
                    isKeyFrame = true;
                    return true;
                default:
                    codec = EncodedVideoCodec.Unknown;
                    frameSegment = default(ArraySegment<byte>);
                    codecParametersSegment = default(ArraySegment<byte>);
                    return false;
            }
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

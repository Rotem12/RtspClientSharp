using System;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Audio;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.Utils
{
    static class RawFrameCopier
    {
        public static RawFrame Copy(RawFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            ArraySegment<byte> frameSegment = CopySegment(frame.FrameSegment);

            switch (frame)
            {
                case RawH264IFrame h264IFrame:
                    return new RawH264IFrame(h264IFrame.Timestamp, frameSegment,
                        CopySegment(h264IFrame.SpsPpsSegment));
                case RawH264PFrame h264PFrame:
                    return new RawH264PFrame(h264PFrame.Timestamp, frameSegment);
                case RawH265IFrame h265IFrame:
                    return new RawH265IFrame(h265IFrame.Timestamp, frameSegment,
                        CopySegment(h265IFrame.ParametersBytesSegment));
                case RawH265PFrame h265PFrame:
                    return new RawH265PFrame(h265PFrame.Timestamp, frameSegment);
                case RawJpegFrame jpegFrame:
                    return new RawJpegFrame(jpegFrame.Timestamp, frameSegment);
                case RawAACFrame aacFrame:
                    return new RawAACFrame(aacFrame.Timestamp, frameSegment,
                        CopySegment(aacFrame.ConfigSegment));
                case RawG711AFrame g711AFrame:
                    return CopyG711Frame(g711AFrame, frameSegment, true);
                case RawG711UFrame g711UFrame:
                    return CopyG711Frame(g711UFrame, frameSegment, false);
                case RawG726Frame g726Frame:
                    return new RawG726Frame(g726Frame.Timestamp, frameSegment, g726Frame.BitsPerCodedSample);
                case RawPCMFrame pcmFrame:
                    return new RawPCMFrame(pcmFrame.Timestamp, frameSegment, pcmFrame.SampleRate,
                        pcmFrame.BitsPerSample, pcmFrame.Channels);
                default:
                    // All frames produced by the built-in parsers are handled above. Keep a
                    // custom frame usable rather than changing the public event contract.
                    return frame;
            }
        }

        private static RawFrame CopyG711Frame(RawG711Frame frame, ArraySegment<byte> frameSegment, bool alaw)
        {
            RawG711Frame copiedFrame = alaw
                ? (RawG711Frame)new RawG711AFrame(frame.Timestamp, frameSegment)
                : new RawG711UFrame(frame.Timestamp, frameSegment);

            copiedFrame.SampleRate = frame.SampleRate;
            copiedFrame.Channels = frame.Channels;
            return copiedFrame;
        }

        private static ArraySegment<byte> CopySegment(ArraySegment<byte> segment)
        {
            if (segment.Array == null || segment.Count == 0)
                return new ArraySegment<byte>(Array.Empty<byte>());

            var bytes = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, bytes, 0, segment.Count);
            return new ArraySegment<byte>(bytes);
        }
    }
}

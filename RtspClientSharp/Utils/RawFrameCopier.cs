using System;
using System.Buffers;
using System.Threading;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Audio;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.Utils
{
    static class RawFrameCopier
    {
        internal sealed class RawFrameCopy : IDisposable
        {
            private byte[] _frameBuffer;
            private byte[] _parametersBuffer;

            public RawFrame Frame { get; }

            public RawFrameCopy(RawFrame frame, byte[] frameBuffer, byte[] parametersBuffer)
            {
                Frame = frame ?? throw new ArgumentNullException(nameof(frame));
                _frameBuffer = frameBuffer;
                _parametersBuffer = parametersBuffer;
            }

            public void Dispose()
            {
                byte[] frameBuffer = Interlocked.Exchange(ref _frameBuffer, null);
                if (frameBuffer != null)
                    ArrayPool<byte>.Shared.Return(frameBuffer);

                byte[] parametersBuffer = Interlocked.Exchange(ref _parametersBuffer, null);
                if (parametersBuffer != null)
                    ArrayPool<byte>.Shared.Return(parametersBuffer);
            }
        }

        public static RawFrameCopy Copy(RawFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            byte[] frameBuffer = null;
            byte[] parametersBuffer = null;
            ArraySegment<byte> frameSegment = CopySegment(frame.FrameSegment, out frameBuffer);
            RawFrame copiedFrame;

            try
            {
                switch (frame)
                {
                    case RawH264IFrame h264IFrame:
                        copiedFrame = new RawH264IFrame(h264IFrame.Timestamp, frameSegment,
                            CopySegment(h264IFrame.SpsPpsSegment, out parametersBuffer));
                        break;
                    case RawH264PFrame h264PFrame:
                        copiedFrame = new RawH264PFrame(h264PFrame.Timestamp, frameSegment);
                        break;
                    case RawH265IFrame h265IFrame:
                        copiedFrame = new RawH265IFrame(h265IFrame.Timestamp, frameSegment,
                            CopySegment(h265IFrame.ParametersBytesSegment, out parametersBuffer));
                        break;
                    case RawH265PFrame h265PFrame:
                        copiedFrame = new RawH265PFrame(h265PFrame.Timestamp, frameSegment);
                        break;
                    case RawJpegFrame jpegFrame:
                        copiedFrame = new RawJpegFrame(jpegFrame.Timestamp, frameSegment);
                        break;
                    case RawAACFrame aacFrame:
                        copiedFrame = new RawAACFrame(aacFrame.Timestamp, frameSegment,
                            CopySegment(aacFrame.ConfigSegment, out parametersBuffer));
                        break;
                    case RawG711AFrame g711AFrame:
                        copiedFrame = CopyG711Frame(g711AFrame, frameSegment, true);
                        break;
                    case RawG711UFrame g711UFrame:
                        copiedFrame = CopyG711Frame(g711UFrame, frameSegment, false);
                        break;
                    case RawG726Frame g726Frame:
                        copiedFrame = new RawG726Frame(g726Frame.Timestamp, frameSegment,
                            g726Frame.BitsPerCodedSample);
                        break;
                    case RawPCMFrame pcmFrame:
                        copiedFrame = new RawPCMFrame(pcmFrame.Timestamp, frameSegment, pcmFrame.SampleRate,
                            pcmFrame.BitsPerSample, pcmFrame.Channels);
                        break;
                    default:
                        // All frames produced by the built-in parsers are handled above. Keep a
                        // custom frame usable rather than changing the public event contract.
                        ReturnBuffer(ref frameBuffer);
                        copiedFrame = frame;
                        break;
                }
            }
            catch
            {
                ReturnBuffer(ref frameBuffer);
                ReturnBuffer(ref parametersBuffer);
                throw;
            }

            if (copiedFrame is RawVideoFrame copiedVideoFrame)
                copiedVideoFrame.HasDecoderInputPadding = true;

            return new RawFrameCopy(copiedFrame, frameBuffer, parametersBuffer);
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

        private static ArraySegment<byte> CopySegment(ArraySegment<byte> segment, out byte[] rentedBuffer)
        {
            rentedBuffer = null;

            if (segment.Array == null || segment.Count == 0)
                return new ArraySegment<byte>(Array.Empty<byte>());

            byte[] bytes = ArrayPool<byte>.Shared.Rent(segment.Count + RawVideoFramePadding.Size);

            try
            {
                Buffer.BlockCopy(segment.Array, segment.Offset, bytes, 0, segment.Count);
                Array.Clear(bytes, segment.Count, RawVideoFramePadding.Size);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(bytes);
                throw;
            }

            rentedBuffer = bytes;
            return new ArraySegment<byte>(bytes, 0, segment.Count);
        }

        private static void ReturnBuffer(ref byte[] buffer)
        {
            byte[] rentedBuffer = Interlocked.Exchange(ref buffer, null);
            if (rentedBuffer != null)
                ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }
}

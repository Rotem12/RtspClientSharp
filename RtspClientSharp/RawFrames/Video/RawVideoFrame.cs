using System;

using System.IO;

namespace RtspClientSharp.RawFrames.Video
{
    public abstract class RawVideoFrame : RawFrame
    {
        public override FrameType Type => FrameType.Video;

        /// <summary>
        /// Indicates that the frame payload is followed by the zeroed padding
        /// required by FFmpeg's packet parser. The library sets this only on
        /// its owned display copies and eligible parser buffers; callers must
        /// not assume it for arbitrary frames received directly from a transport.
        /// </summary>
        public bool HasDecoderInputPadding { get; internal set; }

        protected RawVideoFrame(DateTime timestamp, ArraySegment<byte> frameSegment)
            : base(timestamp, frameSegment)
        {
        }
    }

    internal static class RawVideoFramePadding
    {
        internal const int Size = 64;

        internal static bool IsZeroed(ArraySegment<byte> segment)
        {
            if (segment.Array == null || segment.Count < 0 ||
                segment.Offset < 0 || segment.Offset > segment.Array.Length - segment.Count)
                return false;

            int paddingOffset = segment.Offset + segment.Count;
            if (paddingOffset > segment.Array.Length - Size)
                return false;

            for (int i = 0; i < Size; i++)
            {
                if (segment.Array[paddingOffset + i] != 0)
                    return false;
            }

            return true;
        }

        internal static bool ClearIfAvailable(ArraySegment<byte> segment)
        {
            if (segment.Array == null || segment.Count < 0 ||
                segment.Offset < 0 || segment.Offset > segment.Array.Length - segment.Count)
                return false;

            int paddingOffset = segment.Offset + segment.Count;
            if (paddingOffset > segment.Array.Length - Size)
                return false;

            Array.Clear(segment.Array, paddingOffset, Size);
            return true;
        }

        internal static void Ensure(MemoryStream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            int offset = checked((int)stream.Position);
            int requiredCapacity = checked(offset + Size);
            if (stream.Capacity < requiredCapacity)
                stream.Capacity = requiredCapacity;

            Array.Clear(stream.GetBuffer(), offset, Size);
        }
    }
}

using System;
using System.IO;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.MediaParsers
{
    /// <summary>
    /// Parses complete or fragmented JPEG frames carried as an MPEG-TS PES payload.
    /// </summary>
    sealed class RawJpegParser
    {
        private const int MaxFrameSize = 16 * 1024 * 1024;

        private readonly Func<DateTime> _timestampProvider;
        private readonly MemoryStream _frameStream = new MemoryStream(64 * 1024);
        private DateTime _frameTimestamp;
        private bool _hasFrame;
        private bool _pendingStartMarkerByte;
        private bool _previousFrameByteWasFf;

        public RawJpegParser(Func<DateTime> timestampProvider)
        {
            _timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
        }

        public Action<RawFrame> FrameGenerated { get; set; }

        public void Parse(ArraySegment<byte> payload)
        {
            if (payload.Array == null || payload.Count == 0)
                return;

            int start = payload.Offset;
            int end = payload.Offset + payload.Count;

            while (start < end)
            {
                if (!_hasFrame)
                {
                    if (_pendingStartMarkerByte)
                    {
                        _pendingStartMarkerByte = false;
                        if (payload.Array[start] == RawJpegFrame.StartMarkerBytes[1])
                        {
                            StartFrame();
                            _frameStream.WriteByte(RawJpegFrame.StartMarkerBytes[0]);
                            _frameStream.WriteByte(payload.Array[start]);
                            _previousFrameByteWasFf = false;
                            start++;
                        }
                    }

                    if (!_hasFrame)
                    {
                        int startMarker = FindMarker(payload.Array, start, end,
                            RawJpegFrame.StartMarkerBytes);
                        if (startMarker < 0)
                        {
                            _pendingStartMarkerByte = payload.Array[end - 1] == 0xFF;
                            return;
                        }

                        StartFrame();
                        start = startMarker;
                    }
                }

                if (_previousFrameByteWasFf)
                {
                    if (payload.Array[start] == RawJpegFrame.EndMarkerBytes[1])
                    {
                        if (!Append(payload.Array, start, 1))
                            return;

                        GenerateFrame();
                        _hasFrame = false;
                        _previousFrameByteWasFf = false;
                        start++;
                        continue;
                    }

                    _previousFrameByteWasFf = false;
                }

                int endMarker = FindMarker(payload.Array, start, end, RawJpegFrame.EndMarkerBytes);
                if (endMarker < 0)
                {
                    if (!Append(payload.Array, start, end - start))
                        return;

                    _previousFrameByteWasFf = payload.Array[end - 1] == 0xFF;
                    return;
                }

                int length = endMarker + RawJpegFrame.EndMarkerBytes.Length - start;
                if (!Append(payload.Array, start, length))
                    return;

                GenerateFrame();
                start = endMarker + RawJpegFrame.EndMarkerBytes.Length;
                _hasFrame = false;
                _previousFrameByteWasFf = false;
            }
        }

        public void ResetState()
        {
            _hasFrame = false;
            _pendingStartMarkerByte = false;
            _previousFrameByteWasFf = false;
            _frameStream.Position = 0;
            _frameStream.SetLength(0);
        }

        private void StartFrame()
        {
            _hasFrame = true;
            _frameTimestamp = _timestampProvider();
            _frameStream.Position = 0;
            _frameStream.SetLength(0);
            _previousFrameByteWasFf = false;
        }

        private bool Append(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return true;

            if (_frameStream.Length + count > MaxFrameSize)
            {
                ResetState();
                return false;
            }

            _frameStream.Write(buffer, offset, count);
            return true;
        }

        private void GenerateFrame()
        {
            if (_frameStream.Length == 0)
                return;

            RawVideoFramePadding.Ensure(_frameStream);
            var frameSegment = new ArraySegment<byte>(_frameStream.GetBuffer(), 0,
                checked((int)_frameStream.Length));
            _frameStream.Position = 0;

            var frame = new RawJpegFrame(_frameTimestamp, frameSegment)
            {
                HasDecoderInputPadding = RawVideoFramePadding.IsZeroed(frameSegment)
            };
            FrameGenerated?.Invoke(frame);
        }

        private static int FindMarker(byte[] buffer, int start, int end, byte[] marker)
        {
            for (int index = start; index + marker.Length <= end; index++)
            {
                bool matches = true;
                for (int markerIndex = 0; markerIndex < marker.Length; markerIndex++)
                {
                    if (buffer[index + markerIndex] != marker[markerIndex])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return index;
            }

            return -1;
        }
    }
}

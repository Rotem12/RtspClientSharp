using System;
using System.Collections.Generic;

namespace RtspClientSharp.Recording
{
    public sealed class CompressedVideoRollingBuffer
    {
        private readonly object _syncRoot = new object();
        private readonly List<EncodedVideoFrame> _frames = new List<EncodedVideoFrame>();
        private readonly TimeSpan _duration;

        public CompressedVideoRollingBuffer(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            _duration = duration;
        }

        public int Count
        {
            get
            {
                lock (_syncRoot)
                    return _frames.Count;
            }
        }

        public void Add(EncodedVideoFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            lock (_syncRoot)
            {
                _frames.Add(frame);
                Prune(frame.Timestamp - _duration);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
                _frames.Clear();
        }

        public IReadOnlyList<EncodedVideoFrame> GetFramesForRecording(DateTime requestedStartTimestamp)
        {
            lock (_syncRoot)
            {
                if (_frames.Count == 0)
                    return Array.Empty<EncodedVideoFrame>();

                int startIndex = FindKeyFrameAtOrBefore(requestedStartTimestamp);

                if (startIndex < 0)
                    startIndex = FindFirstKeyFrameAtOrAfter(requestedStartTimestamp);

                if (startIndex < 0)
                    return Array.Empty<EncodedVideoFrame>();

                return _frames.GetRange(startIndex, _frames.Count - startIndex);
            }
        }

        public bool TryGetLatestCodecParameters(EncodedVideoCodec codec, out byte[] codecParametersBytes)
        {
            lock (_syncRoot)
            {
                for (int i = _frames.Count - 1; i >= 0; i--)
                {
                    EncodedVideoFrame frame = _frames[i];
                    if (frame.Codec == codec && frame.HasCodecParameters)
                    {
                        codecParametersBytes = Copy(frame.CodecParametersBytes);
                        return true;
                    }
                }
            }

            codecParametersBytes = Array.Empty<byte>();
            return false;
        }

        private void Prune(DateTime cutoff)
        {
            int firstInsideWindow = 0;
            while (firstInsideWindow < _frames.Count && _frames[firstInsideWindow].Timestamp < cutoff)
                firstInsideWindow++;

            if (firstInsideWindow <= 0)
                return;

            int firstToKeep = firstInsideWindow;
            for (int i = firstInsideWindow - 1; i >= 0; i--)
            {
                if (_frames[i].IsKeyFrame)
                {
                    firstToKeep = i;
                    break;
                }
            }

            if (firstToKeep > 0)
                _frames.RemoveRange(0, firstToKeep);
        }

        private int FindKeyFrameAtOrBefore(DateTime timestamp)
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                EncodedVideoFrame frame = _frames[i];
                if (frame.Timestamp <= timestamp && frame.IsKeyFrame)
                    return i;
            }

            return -1;
        }

        private int FindFirstKeyFrameAtOrAfter(DateTime timestamp)
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                EncodedVideoFrame frame = _frames[i];
                if (frame.Timestamp >= timestamp && frame.IsKeyFrame)
                    return i;
            }

            return -1;
        }

        private static byte[] Copy(byte[] bytes)
        {
            var copy = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
            return copy;
        }
    }
}

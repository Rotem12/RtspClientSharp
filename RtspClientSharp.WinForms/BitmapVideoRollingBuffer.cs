using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace RtspClientSharp.WinForms
{
    internal sealed class BitmapVideoRollingBuffer : IDisposable
    {
        private readonly TimeSpan _duration;
        private readonly LinkedList<BitmapVideoFrame> _frames =
            new LinkedList<BitmapVideoFrame>();

        public BitmapVideoRollingBuffer(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            _duration = duration;
        }

        public void Add(Bitmap bitmap, DateTime timestamp)
        {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            Bitmap copy = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                PixelFormat.Format24bppRgb);
            _frames.AddLast(new BitmapVideoFrame(copy, timestamp));

            DateTime minimumTimestamp = timestamp - _duration;
            while (_frames.First != null && _frames.First.Value.Timestamp < minimumTimestamp)
            {
                _frames.First.Value.Dispose();
                _frames.RemoveFirst();
            }
        }

        public IReadOnlyList<BitmapVideoFrame> GetFramesForRecording(DateTime startTimestamp)
        {
            var result = new List<BitmapVideoFrame>();
            foreach (BitmapVideoFrame frame in _frames)
            {
                if (frame.Timestamp >= startTimestamp)
                    result.Add(frame);
            }

            return result;
        }

        public void Clear()
        {
            foreach (BitmapVideoFrame frame in _frames)
                frame.Dispose();

            _frames.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }

    internal sealed class BitmapVideoFrame : IDisposable
    {
        public BitmapVideoFrame(Bitmap bitmap, DateTime timestamp)
        {
            Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
            Timestamp = timestamp;
        }

        public Bitmap Bitmap { get; }
        public DateTime Timestamp { get; }

        public void Dispose()
        {
            Bitmap.Dispose();
        }
    }
}

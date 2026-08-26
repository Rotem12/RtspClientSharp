using System;
using System.Drawing;

namespace RtspClientSharp.WinForms
{
    /// <summary>
    /// Adapter for a decoded bitmap recording backend. The control calls this
    /// synchronously; the bitmap is valid only until <see cref="Write"/> returns.
    /// </summary>
    public interface IBitmapVideoRecorder : IDisposable
    {
        string OutputFilePath { get; }
        bool IsOpen { get; }

        void Start(BitmapVideoRecorderOptions options);
        void Write(Bitmap bitmap, TimeSpan timestamp);
        void Stop();
    }
}

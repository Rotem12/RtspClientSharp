using System;

namespace RtspClientSharp.WinForms
{
    public sealed class VideoFrameEventArgs : EventArgs
    {
        public VideoFrameEventArgs(DateTime timestamp, int width, int height, bool hardwareAccelerated,
            MediaTransportMode detectedTransportMode)
        {
            Timestamp = timestamp;
            Width = width;
            Height = height;
            HardwareAccelerated = hardwareAccelerated;
            DetectedTransportMode = detectedTransportMode;
        }

        public DateTime Timestamp { get; }
        public int Width { get; }
        public int Height { get; }
        public bool HardwareAccelerated { get; }
        public MediaTransportMode DetectedTransportMode { get; }
    }
}

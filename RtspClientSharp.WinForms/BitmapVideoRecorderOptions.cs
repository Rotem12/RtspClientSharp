using System;

namespace RtspClientSharp.WinForms
{
    /// <summary>
    /// Describes the decoded bitmap stream supplied to an <see cref="IBitmapVideoRecorder"/>.
    /// </summary>
    public sealed class BitmapVideoRecorderOptions
    {
        public BitmapVideoRecorderOptions(string outputFilePath, int width, int height,
            int frameRate, int bitRate)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("An output file path is required.", nameof(outputFilePath));
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (frameRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameRate));
            if (bitRate < 0)
                throw new ArgumentOutOfRangeException(nameof(bitRate));

            OutputFilePath = outputFilePath;
            Width = width;
            Height = height;
            FrameRate = frameRate;
            BitRate = bitRate;
        }

        public string OutputFilePath { get; }
        public int Width { get; }
        public int Height { get; }
        public int FrameRate { get; }
        public int BitRate { get; }
    }
}

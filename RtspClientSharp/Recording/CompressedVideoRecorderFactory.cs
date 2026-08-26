using System;
using System.IO;

namespace RtspClientSharp.Recording
{
    /// <summary>
    /// Creates the optimized compressed recorder used by the WinForms control
    /// and by external integrations.
    /// </summary>
    public static class CompressedVideoRecorderFactory
    {
        public static ICompressedVideoRecorder Create(string outputFilePath,
            CompressedVideoRecorderOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("Output file path is required.", nameof(outputFilePath));

            CompressedVideoRecordingFormat format = options?.Format ??
                CompressedVideoRecordingFormat.Auto;

            if (format == CompressedVideoRecordingFormat.Auto)
                format = IsElementaryStreamExtension(Path.GetExtension(outputFilePath))
                    ? CompressedVideoRecordingFormat.AnnexB
                    : CompressedVideoRecordingFormat.MpegTs;

            switch (format)
            {
                case CompressedVideoRecordingFormat.MpegTs:
                    return new MpegTsVideoFileRecorder();
                case CompressedVideoRecordingFormat.AnnexB:
                    return new AnnexBVideoFileRecorder();
                default:
                    throw new ArgumentOutOfRangeException(nameof(options),
                        "Unsupported compressed video recording format.");
            }
        }

        private static bool IsElementaryStreamExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return false;

            switch (extension.ToLowerInvariant())
            {
                case ".264":
                case ".h264":
                case ".265":
                case ".h265":
                case ".hevc":
                case ".mjpeg":
                case ".mjpg":
                    return true;
                default:
                    return false;
            }
        }
    }
}

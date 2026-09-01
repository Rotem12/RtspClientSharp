using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RtspClientSharp.WinForms
{
    /// <summary>
    /// Process-local, opt-in diagnostics for display and GPU presentation issues.
    /// Disabled unless RTSPCLIENTSHARP_TRACE is set to a file path, 1, true, or yes.
    /// </summary>
    internal static class RtspVideoTrace
    {
        private static readonly object SyncRoot = new object();
        private static readonly string FilePath = ResolveFilePath();
        private static readonly int ProcessId = Process.GetCurrentProcess().Id;

        public static bool Enabled => !string.IsNullOrEmpty(FilePath);

        public static void Write(string message)
        {
            if (!Enabled)
                return;

            try
            {
                lock (SyncRoot)
                {
                    string directory = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    string line = string.Format("{0:O} pid={1} tid={2} {3}{4}",
                        DateTime.UtcNow, ProcessId, Environment.CurrentManagedThreadId,
                        message, Environment.NewLine);
                    File.AppendAllText(FilePath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never affect playback or presentation.
            }
        }

        private static string ResolveFilePath()
        {
            string value = Environment.GetEnvironmentVariable("RTSPCLIENTSHARP_TRACE");
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(Path.GetTempPath(), "RtspClientSharp", "rtsp-video-trace.log");
            }

            return value;
        }
    }
}

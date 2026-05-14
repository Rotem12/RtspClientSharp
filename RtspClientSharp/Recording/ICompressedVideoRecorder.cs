using System;
using System.Collections.Generic;

namespace RtspClientSharp.Recording
{
    public interface ICompressedVideoRecorder : IDisposable
    {
        string OutputFilePath { get; }
        bool IsOpen { get; }

        void Start(string outputFilePath, IEnumerable<EncodedVideoFrame> preRecordFrames = null,
            CompressedVideoRecorderOptions options = null);

        void Write(EncodedVideoFrame frame);
        void Stop();
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace RtspClientSharp.Recording
{
    public sealed class AnnexBVideoFileRecorder : ICompressedVideoRecorder
    {
        private readonly object _syncRoot = new object();
        private FileStream _stream;
        private EncodedVideoCodec _codec = EncodedVideoCodec.Unknown;
        private byte[] _lastCodecParameters = Array.Empty<byte>();
        private bool _waitingForKeyFrame;
        private string _requestedOutputFilePath;

        public string OutputFilePath { get; private set; }

        public bool IsOpen
        {
            get
            {
                lock (_syncRoot)
                    return _stream != null || _requestedOutputFilePath != null;
            }
        }

        public void Start(string outputFilePath, IEnumerable<EncodedVideoFrame> preRecordFrames = null,
            CompressedVideoRecorderOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("Output file path is required.", nameof(outputFilePath));

            lock (_syncRoot)
            {
                if (_stream != null)
                    throw new InvalidOperationException("Recorder is already open.");

                _requestedOutputFilePath = outputFilePath;
                OutputFilePath = null;
                _codec = EncodedVideoCodec.Unknown;
                _lastCodecParameters = Array.Empty<byte>();
                _waitingForKeyFrame = true;

                if (preRecordFrames == null)
                    return;

                foreach (EncodedVideoFrame frame in preRecordFrames)
                    WriteCore(frame);
            }
        }

        public void Write(EncodedVideoFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            lock (_syncRoot)
                WriteCore(frame);
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                _stream?.Dispose();
                _stream = null;
                _requestedOutputFilePath = null;
                _codec = EncodedVideoCodec.Unknown;
                _lastCodecParameters = Array.Empty<byte>();
                _waitingForKeyFrame = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void WriteCore(EncodedVideoFrame frame)
        {
            if (_stream == null)
                return;

            if (!IsSupported(frame.Codec))
                return;

            if (_codec == EncodedVideoCodec.Unknown)
                _codec = frame.Codec;

            if (frame.Codec != _codec)
                return;

            if (_waitingForKeyFrame)
            {
                if (!frame.IsKeyFrame)
                    return;

                _waitingForKeyFrame = false;
            }

            EnsureOpen(frame.Codec);

            if (frame.HasCodecParameters && !SequenceEqual(_lastCodecParameters, frame.CodecParametersBytes))
            {
                WriteBytes(frame.CodecParametersBytes);
                _lastCodecParameters = Copy(frame.CodecParametersBytes);
            }

            WriteBytes(frame.FrameBytes);
        }

        private void EnsureOpen(EncodedVideoCodec codec)
        {
            if (_stream != null)
                return;

            if (_requestedOutputFilePath == null)
                return;

            OutputFilePath = WithBestExtension(_requestedOutputFilePath, codec);
            string directory = Path.GetDirectoryName(OutputFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _stream = new FileStream(OutputFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        private void WriteBytes(byte[] bytes)
        {
            if (bytes.Length != 0)
                _stream.Write(bytes, 0, bytes.Length);
        }

        private static string WithBestExtension(string outputFilePath, EncodedVideoCodec codec)
        {
            switch (codec)
            {
                case EncodedVideoCodec.H264:
                    return Path.ChangeExtension(outputFilePath, ".h264");
                case EncodedVideoCodec.H265:
                    return Path.ChangeExtension(outputFilePath, ".h265");
                default:
                    return outputFilePath;
            }
        }

        private static bool IsSupported(EncodedVideoCodec codec)
        {
            return codec == EncodedVideoCodec.H264 || codec == EncodedVideoCodec.H265;
        }

        private static byte[] Copy(byte[] bytes)
        {
            var copy = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
            return copy;
        }

        private static bool SequenceEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }
    }
}

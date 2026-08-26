using System;
using System.Collections.Generic;
using System.IO;
using RtspClientSharp.RawFrames;

namespace RtspClientSharp.Recording
{
    /// <summary>
    /// Writes H.264/H.265 Annex-B bytes without decoding or re-encoding them.
    /// The raw-frame overload is the low-allocation path used by RtspVideoControl.
    /// </summary>
    public sealed class AnnexBVideoFileRecorder : ICompressedVideoRecorder,
        IRawFrameCompressedVideoRecorder
    {
        private const int FileBufferSize = 1024 * 1024;

        private readonly object _syncRoot = new object();
        private FileStream _stream;
        private EncodedVideoCodec _codec = EncodedVideoCodec.Unknown;
        private byte[] _lastCodecParameters = Array.Empty<byte>();
        private bool _waitingForKeyFrame;
        private string _requestedOutputFilePath;
        private long _framesWritten;
        private long _bytesWritten;

        public string OutputFilePath { get; private set; }

        public EncodedVideoCodec Codec
        {
            get
            {
                lock (_syncRoot)
                    return _codec;
            }
        }

        public long FramesWritten => System.Threading.Interlocked.Read(ref _framesWritten);
        public long BytesWritten => System.Threading.Interlocked.Read(ref _bytesWritten);

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
                if (_stream != null || _requestedOutputFilePath != null)
                    throw new InvalidOperationException("Recorder is already open.");

                _requestedOutputFilePath = outputFilePath;
                OutputFilePath = null;
                _codec = EncodedVideoCodec.Unknown;
                _lastCodecParameters = Array.Empty<byte>();
                _waitingForKeyFrame = true;
                System.Threading.Interlocked.Exchange(ref _framesWritten, 0);
                System.Threading.Interlocked.Exchange(ref _bytesWritten, 0);

                if (preRecordFrames == null)
                    return;

                foreach (EncodedVideoFrame frame in preRecordFrames)
                    WriteCore(frame.Timestamp, frame.Codec, frame.IsKeyFrame,
                        new ArraySegment<byte>(frame.FrameBytes),
                        new ArraySegment<byte>(frame.CodecParametersBytes));
            }
        }

        public void Write(EncodedVideoFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            lock (_syncRoot)
            {
                WriteCore(frame.Timestamp, frame.Codec, frame.IsKeyFrame,
                    new ArraySegment<byte>(frame.FrameBytes),
                    new ArraySegment<byte>(frame.CodecParametersBytes));
            }
        }

        public void WriteRawFrame(RawFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            if (!EncodedVideoFrameFactory.TryDescribe(frame, out EncodedVideoCodec codec,
                out bool isKeyFrame, out ArraySegment<byte> frameSegment,
                out ArraySegment<byte> codecParametersSegment))
                return;

            lock (_syncRoot)
            {
                // This method is called while RawFrameDispatcher still owns the
                // pooled frame buffers. Write directly from those segments and do
                // not create an EncodedVideoFrame or another payload copy.
                WriteCore(frame.Timestamp, codec, isKeyFrame, frameSegment, codecParametersSegment);
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                _stream?.Flush();
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

        private void WriteCore(DateTime timestamp, EncodedVideoCodec codec, bool isKeyFrame,
            ArraySegment<byte> frameSegment, ArraySegment<byte> codecParametersSegment)
        {
            if (!IsSupported(codec))
                return;

            if (_codec == EncodedVideoCodec.Unknown)
                _codec = codec;

            if (codec != _codec)
                return;

            if (_waitingForKeyFrame)
            {
                if (!isKeyFrame)
                    return;

                _waitingForKeyFrame = false;
            }

            EnsureOpen(codec);
            if (_stream == null)
                return;

            if (codecParametersSegment.Count != 0 &&
                !SequenceEqual(_lastCodecParameters, codecParametersSegment))
            {
                WriteBytes(codecParametersSegment);
                _lastCodecParameters = Copy(codecParametersSegment);
            }

            WriteBytes(frameSegment);
            System.Threading.Interlocked.Increment(ref _framesWritten);
        }

        private void EnsureOpen(EncodedVideoCodec codec)
        {
            if (_stream != null || _requestedOutputFilePath == null)
                return;

            OutputFilePath = WithBestExtension(_requestedOutputFilePath, codec);
            string directory = Path.GetDirectoryName(OutputFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _stream = new FileStream(OutputFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
                FileBufferSize, FileOptions.SequentialScan);
        }

        private void WriteBytes(ArraySegment<byte> bytes)
        {
            if (bytes.Array == null || bytes.Count == 0)
                return;

            _stream.Write(bytes.Array, bytes.Offset, bytes.Count);
            System.Threading.Interlocked.Add(ref _bytesWritten, bytes.Count);
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

        private static byte[] Copy(ArraySegment<byte> segment)
        {
            if (segment.Array == null || segment.Count == 0)
                return Array.Empty<byte>();

            var copy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
            return copy;
        }

        private static bool SequenceEqual(byte[] left, ArraySegment<byte> right)
        {
            if (left == null || right.Array == null || left.Length != right.Count)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right.Array[right.Offset + i])
                    return false;
            }

            return true;
        }
    }
}

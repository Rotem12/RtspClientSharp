using System;
using System.Collections.Generic;
using System.IO;
using RtspClientSharp.RawFrames;

namespace RtspClientSharp.Recording
{
    /// <summary>
    /// Remuxes H.264/H.265 access units into a video-only MPEG-TS file. No
    /// decoding, color conversion, bitmap allocation, or re-encoding is used.
    /// </summary>
    public sealed class MpegTsVideoFileRecorder : ICompressedVideoRecorder,
        IRawFrameCompressedVideoRecorder
    {
        private const int TsPacketSize = 188;
        private const int FileBufferSize = 1024 * 1024;
        private const int OutputBufferSize = TsPacketSize * 1024;
        private const ushort PmtPid = 0x0100;
        private const ushort VideoPid = 0x0101;

        private readonly object _syncRoot = new object();
        private readonly byte[] _packetBuffer = new byte[TsPacketSize];
        private readonly byte[] _outputBuffer = new byte[OutputBufferSize];
        private readonly byte[] _pesHeader = new byte[14];

        private FileStream _stream;
        private EncodedVideoCodec _codec = EncodedVideoCodec.Unknown;
        private byte[] _lastCodecParameters = Array.Empty<byte>();
        private bool _waitingForKeyFrame;
        private string _requestedOutputFilePath;
        private int _outputBufferLength;
        private byte _patContinuity;
        private byte _pmtContinuity;
        private byte _videoContinuity;
        private bool _hasBaseTimestamp;
        private DateTime _baseTimestamp;
        private bool _hasPts;
        private long _lastPts;
        private TimeSpan? _frameDuration;
        private long _framesWritten;
        private long _bytesWritten;
        private long _packetsWritten;

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
        public long PacketsWritten => System.Threading.Interlocked.Read(ref _packetsWritten);

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

            if (options?.FrameDuration.HasValue == true && options.FrameDuration.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options),
                    "FrameDuration must be greater than zero.");

            lock (_syncRoot)
            {
                if (_stream != null || _requestedOutputFilePath != null)
                    throw new InvalidOperationException("Recorder is already open.");

                _requestedOutputFilePath = outputFilePath;
                OutputFilePath = null;
                _codec = EncodedVideoCodec.Unknown;
                _lastCodecParameters = Array.Empty<byte>();
                _waitingForKeyFrame = true;
                _outputBufferLength = 0;
                _patContinuity = 0;
                _pmtContinuity = 0;
                _videoContinuity = 0;
                _hasBaseTimestamp = false;
                _baseTimestamp = default(DateTime);
                _hasPts = false;
                _lastPts = 0;
                _frameDuration = options?.FrameDuration;
                System.Threading.Interlocked.Exchange(ref _framesWritten, 0);
                System.Threading.Interlocked.Exchange(ref _bytesWritten, 0);
                System.Threading.Interlocked.Exchange(ref _packetsWritten, 0);

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
                // RawFrameDispatcher owns these pooled buffers until this method
                // returns. The muxer copies only into its fixed 188-byte packet
                // buffer and never creates a second frame-sized allocation.
                WriteCore(frame.Timestamp, codec, isKeyFrame, frameSegment, codecParametersSegment);
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                FlushOutputBuffer();
                _stream?.Flush();
                _stream?.Dispose();
                _stream = null;
                _requestedOutputFilePath = null;
                _codec = EncodedVideoCodec.Unknown;
                _lastCodecParameters = Array.Empty<byte>();
                _waitingForKeyFrame = false;
                _frameDuration = null;
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
                _lastCodecParameters = Copy(codecParametersSegment);
            }

            long pts = GetPresentationTimestamp(timestamp);
            ArraySegment<byte> parametersToWrite = codecParametersSegment;
            if (isKeyFrame && parametersToWrite.Count == 0 && _lastCodecParameters.Length != 0)
                parametersToWrite = new ArraySegment<byte>(_lastCodecParameters);

            WritePes(isKeyFrame, pts, parametersToWrite, frameSegment);
            System.Threading.Interlocked.Increment(ref _framesWritten);
        }

        private void EnsureOpen(EncodedVideoCodec codec)
        {
            if (_stream != null || _requestedOutputFilePath == null)
                return;

            OutputFilePath = Path.ChangeExtension(_requestedOutputFilePath, ".ts");
            string directory = Path.GetDirectoryName(OutputFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _stream = new FileStream(OutputFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
                FileBufferSize, FileOptions.SequentialScan);
            WritePatAndPmt(codec);
        }

        private void WritePatAndPmt(EncodedVideoCodec codec)
        {
            byte[] pat = new byte[16];
            pat[0] = 0x00;
            pat[1] = 0xB0;
            pat[2] = 0x0D;
            pat[3] = 0x00;
            pat[4] = 0x01;
            pat[5] = 0xC1;
            pat[6] = 0x00;
            pat[7] = 0x00;
            pat[8] = 0x00;
            pat[9] = 0x01;
            pat[10] = (byte)(0xE0 | ((PmtPid >> 8) & 0x1F));
            pat[11] = (byte)(PmtPid & 0xFF);
            WriteCrc(pat, 12);
            WritePsiPacket(0, pat, ref _patContinuity);

            byte[] pmt = new byte[21];
            pmt[0] = 0x02;
            pmt[1] = 0xB0;
            pmt[2] = 0x12;
            pmt[3] = 0x00;
            pmt[4] = 0x01;
            pmt[5] = 0xC1;
            pmt[6] = 0x00;
            pmt[7] = 0x00;
            pmt[8] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F));
            pmt[9] = (byte)(VideoPid & 0xFF);
            pmt[10] = 0xF0;
            pmt[11] = 0x00;
            pmt[12] = codec == EncodedVideoCodec.H265 ? (byte)0x24 : (byte)0x1B;
            pmt[13] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F));
            pmt[14] = (byte)(VideoPid & 0xFF);
            pmt[15] = 0xF0;
            pmt[16] = 0x00;
            WriteCrc(pmt, 17);
            WritePsiPacket(PmtPid, pmt, ref _pmtContinuity);
        }

        private void WritePsiPacket(ushort pid, byte[] section, ref byte continuity)
        {
            FillPacket(0x40, pid, (byte)(0x10 | continuity));
            continuity = (byte)((continuity + 1) & 0x0F);
            _packetBuffer[4] = 0x00;
            Buffer.BlockCopy(section, 0, _packetBuffer, 5, section.Length);
            EmitPacket();
        }

        private void WritePes(bool isKeyFrame, long pts, ArraySegment<byte> parameters,
            ArraySegment<byte> frame)
        {
            _pesHeader[0] = 0x00;
            _pesHeader[1] = 0x00;
            _pesHeader[2] = 0x01;
            _pesHeader[3] = 0xE0;
            // A zero PES length is valid for video and avoids a 16-bit size limit
            // for large IDR access units.
            _pesHeader[4] = 0x00;
            _pesHeader[5] = 0x00;
            _pesHeader[6] = 0x84;
            _pesHeader[7] = 0x80;
            _pesHeader[8] = 0x05;
            WritePts(_pesHeader, 9, pts);

            int headerOffset = 0;
            int parametersOffset = 0;
            int frameOffset = 0;
            int remaining = _pesHeader.Length + parameters.Count + frame.Count;
            bool firstPacket = true;

            while (remaining > 0)
            {
                int payloadCapacity = firstPacket ? 176 : 184;
                int payloadLength = Math.Min(remaining, payloadCapacity);
                bool hasAdaptation = firstPacket || payloadLength < 184;

                byte headerFlags = (byte)(firstPacket ? 0x40 : 0x00);
                FillPacket((byte)(firstPacket ? 0x40 : 0x00), VideoPid,
                    (byte)((hasAdaptation ? 0x30 : 0x10) | _videoContinuity));
                _videoContinuity = (byte)((_videoContinuity + 1) & 0x0F);

                int payloadOffset;
                if (hasAdaptation)
                {
                    int adaptationLength = 183 - payloadLength;
                    _packetBuffer[4] = (byte)adaptationLength;
                    payloadOffset = 5 + adaptationLength;

                    if (firstPacket)
                    {
                        _packetBuffer[5] = (byte)(0x10 | headerFlags);
                        WritePcr(_packetBuffer, 6, pts);
                    }
                    else if (adaptationLength > 0)
                    {
                        _packetBuffer[5] = 0x00;
                    }
                }
                else
                {
                    payloadOffset = 4;
                }

                int copied = 0;
                copied += CopySegment(_packetBuffer, payloadOffset + copied, payloadLength - copied,
                    new ArraySegment<byte>(_pesHeader), ref headerOffset);
                copied += CopySegment(_packetBuffer, payloadOffset + copied, payloadLength - copied,
                    parameters, ref parametersOffset);
                copied += CopySegment(_packetBuffer, payloadOffset + copied, payloadLength - copied,
                    frame, ref frameOffset);

                if (copied != payloadLength)
                    throw new InvalidOperationException("MPEG-TS payload accounting failed.");

                remaining -= copied;
                firstPacket = false;
                EmitPacket();
            }
        }

        private long GetPresentationTimestamp(DateTime timestamp)
        {
            if (!_hasBaseTimestamp)
            {
                _hasBaseTimestamp = true;
                _baseTimestamp = timestamp;
            }

            TimeSpan delta = timestamp - _baseTimestamp;
            long pts = delta <= TimeSpan.Zero
                ? 0
                : (long)(delta.TotalSeconds * 90000.0);

            if (_hasPts && pts <= _lastPts)
            {
                long duration = _frameDuration.HasValue
                    ? Math.Max(1L, (long)(_frameDuration.Value.TotalSeconds * 90000.0))
                    : 1L;
                pts = _lastPts + duration;
            }

            _hasPts = true;
            _lastPts = pts;
            return pts;
        }

        private void FillPacket(byte flags, ushort pid, byte header)
        {
            for (int i = 0; i < _packetBuffer.Length; i++)
                _packetBuffer[i] = 0xFF;

            _packetBuffer[0] = 0x47;
            _packetBuffer[1] = (byte)(flags | ((pid >> 8) & 0x1F));
            _packetBuffer[2] = (byte)pid;
            _packetBuffer[3] = header;
        }

        private void EmitPacket()
        {
            if (_outputBufferLength + TsPacketSize > _outputBuffer.Length)
                FlushOutputBuffer();

            Buffer.BlockCopy(_packetBuffer, 0, _outputBuffer, _outputBufferLength, TsPacketSize);
            _outputBufferLength += TsPacketSize;
            System.Threading.Interlocked.Increment(ref _packetsWritten);
            System.Threading.Interlocked.Add(ref _bytesWritten, TsPacketSize);
        }

        private void FlushOutputBuffer()
        {
            if (_stream == null || _outputBufferLength == 0)
                return;

            _stream.Write(_outputBuffer, 0, _outputBufferLength);
            _outputBufferLength = 0;
        }

        private static int CopySegment(byte[] destination, int destinationOffset, int count,
            ArraySegment<byte> source, ref int sourceOffset)
        {
            if (count <= 0 || source.Array == null || sourceOffset >= source.Count)
                return 0;

            int copied = Math.Min(count, source.Count - sourceOffset);
            Buffer.BlockCopy(source.Array, source.Offset + sourceOffset, destination,
                destinationOffset, copied);
            sourceOffset += copied;
            return copied;
        }

        private void WriteCrc(byte[] section, int crcOffset)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < crcOffset; i++)
            {
                crc ^= (uint)section[i] << 24;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 0x80000000) != 0
                        ? (crc << 1) ^ 0x04C11DB7
                        : crc << 1;
            }

            section[crcOffset] = (byte)(crc >> 24);
            section[crcOffset + 1] = (byte)(crc >> 16);
            section[crcOffset + 2] = (byte)(crc >> 8);
            section[crcOffset + 3] = (byte)crc;
        }

        private static void WritePts(byte[] buffer, int offset, long pts)
        {
            long value = pts & 0x1FFFFFFFFL;
            buffer[offset] = (byte)(0x21 | (((value >> 30) & 0x07) << 1));
            buffer[offset + 1] = (byte)(value >> 22);
            buffer[offset + 2] = (byte)(0x01 | (((value >> 15) & 0x7F) << 1));
            buffer[offset + 3] = (byte)(value >> 7);
            buffer[offset + 4] = (byte)(0x01 | ((value & 0x7F) << 1));
        }

        private static void WritePcr(byte[] buffer, int offset, long pts)
        {
            long value = pts & 0x1FFFFFFFFL;
            buffer[offset] = (byte)(value >> 25);
            buffer[offset + 1] = (byte)(value >> 17);
            buffer[offset + 2] = (byte)(value >> 9);
            buffer[offset + 3] = (byte)(value >> 1);
            buffer[offset + 4] = (byte)(((value & 0x01) << 7) | 0x7E);
            buffer[offset + 5] = 0x00;
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

        private static bool IsSupported(EncodedVideoCodec codec)
        {
            return codec == EncodedVideoCodec.H264 || codec == EncodedVideoCodec.H265;
        }
    }
}

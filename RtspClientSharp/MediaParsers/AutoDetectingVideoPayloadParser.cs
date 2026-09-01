using System;
using System.Collections.Generic;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.RawFrames;
using RtspClientSharp.Utils;

namespace RtspClientSharp.MediaParsers
{
    /// <summary>
    /// Selects the RTP video payload parser from the payload structure. Direct RTP
    /// has no SDP, so this keeps only a small startup probe and then uses one parser
    /// for the rest of the session.
    /// </summary>
    sealed class AutoDetectingVideoPayloadParser : IMediaPayloadParser, IVideoCodecDetector
    {
        private const int MaxProbePackets = 8;
        private const int DecisionPacketCount = 4;

        private readonly byte[] _h264SpsPpsBytes;
        private readonly byte[] _h265ParameterBytes;
        private readonly List<PendingPayload> _pendingPayloads = new List<PendingPayload>(MaxProbePackets);

        private IMediaPayloadParser _selectedParser;
        private CodecInfoType _detectedVideoCodec = CodecInfoType.Auto;
        private int _h264Score;
        private int _h265Score;
        private int _mjpegScore;

        public AutoDetectingVideoPayloadParser(byte[] h264SpsPpsBytes = null,
            byte[] h265ParameterBytes = null)
        {
            _h264SpsPpsBytes = h264SpsPpsBytes ?? Array.Empty<byte>();
            _h265ParameterBytes = h265ParameterBytes ?? Array.Empty<byte>();

            if (_h264SpsPpsBytes.Length != 0 && _h265ParameterBytes.Length == 0)
                SelectCodec(CodecInfoType.H264);
            else if (_h265ParameterBytes.Length != 0 && _h264SpsPpsBytes.Length == 0)
                SelectCodec(CodecInfoType.H265);
        }

        public Action<RawFrame> FrameGenerated { get; set; }

        public CodecInfoType DetectedVideoCodec => _detectedVideoCodec;

        public void Parse(TimeSpan timeOffset, ArraySegment<byte> byteSegment, bool markerBit)
        {
            if (_selectedParser != null)
            {
                _selectedParser.Parse(timeOffset, byteSegment, markerBit);
                return;
            }

            if (byteSegment.Array == null || byteSegment.Count == 0)
            {
                ResetState();
                return;
            }

            PendingPayload pendingPayload = PendingPayload.CopyFrom(byteSegment, timeOffset, markerBit);
            _pendingPayloads.Add(pendingPayload);

            RtpVideoCodecDetector.AddScore(byteSegment, ref _h264Score, ref _h265Score, ref _mjpegScore);

            CodecInfoType detectedCodec = ChooseCodec();
            if (detectedCodec != CodecInfoType.Auto)
                SelectCodec(detectedCodec);
            else if (_pendingPayloads.Count >= MaxProbePackets)
                SelectCodec(SelectBestCodec());
        }

        public void ResetState()
        {
            if (_selectedParser != null)
            {
                _selectedParser.ResetState();
                return;
            }

            _pendingPayloads.Clear();
            _h264Score = 0;
            _h265Score = 0;
            _mjpegScore = 0;
        }

        private CodecInfoType ChooseCodec()
        {
            if (_mjpegScore >= 10)
                return CodecInfoType.MJPEG;

            if (_h264Score >= 8 && _h264Score > _h265Score)
                return CodecInfoType.H264;

            if (_h265Score >= 8 && _h265Score > _h264Score)
                return CodecInfoType.H265;

            if (_pendingPayloads.Count < DecisionPacketCount)
                return CodecInfoType.Auto;

            if (_h264Score >= 4 && _h264Score > _h265Score + 1)
                return CodecInfoType.H264;

            if (_h265Score >= 4 && _h265Score > _h264Score + 1)
                return CodecInfoType.H265;

            return CodecInfoType.Auto;
        }

        private CodecInfoType SelectBestCodec()
        {
            if (_mjpegScore > 0 && _mjpegScore >= _h264Score && _mjpegScore >= _h265Score)
                return CodecInfoType.MJPEG;

            if (_h265Score > _h264Score)
                return CodecInfoType.H265;

            return CodecInfoType.H264;
        }

        private void SelectCodec(CodecInfoType codec)
        {
            if (codec == CodecInfoType.Auto)
                codec = CodecInfoType.H264;

            _detectedVideoCodec = codec;
            _selectedParser = CreateParser(codec);
            _selectedParser.FrameGenerated = frame => FrameGenerated?.Invoke(frame);

            foreach (PendingPayload pendingPayload in _pendingPayloads)
                _selectedParser.Parse(pendingPayload.TimeOffset, pendingPayload.Payload, pendingPayload.MarkerBit);

            _pendingPayloads.Clear();
        }

        private IMediaPayloadParser CreateParser(CodecInfoType codec)
        {
            switch (codec)
            {
                case CodecInfoType.H264:
                    return new H264VideoPayloadParser(new H264CodecInfo
                    {
                        SpsPpsBytes = _h264SpsPpsBytes
                    });
                case CodecInfoType.H265:
                    return new H265VideoPayloadParser(new H265CodecInfo
                    {
                        VpsBytes = _h265ParameterBytes
                    });
                case CodecInfoType.MJPEG:
                    return new MJPEGVideoPayloadParser();
                default:
                    throw new ArgumentOutOfRangeException(nameof(codec));
            }
        }

        private sealed class PendingPayload
        {
            private PendingPayload(byte[] payload, TimeSpan timeOffset, bool markerBit)
            {
                Payload = new ArraySegment<byte>(payload);
                TimeOffset = timeOffset;
                MarkerBit = markerBit;
            }

            public ArraySegment<byte> Payload { get; }
            public TimeSpan TimeOffset { get; }
            public bool MarkerBit { get; }

            public static PendingPayload CopyFrom(ArraySegment<byte> payload, TimeSpan timeOffset, bool markerBit)
            {
                var copiedPayload = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array, payload.Offset, copiedPayload, 0, payload.Count);
                return new PendingPayload(copiedPayload, timeOffset, markerBit);
            }
        }

        private static class RtpVideoCodecDetector
        {
            public static void AddScore(ArraySegment<byte> payload, ref int h264Score, ref int h265Score,
                ref int mjpegScore)
            {
                if (LooksLikeRtpJpeg(payload))
                    mjpegScore += 12;

                h264Score += GetH264Score(payload);
                h265Score += GetH265Score(payload);
            }

            private static int GetH264Score(ArraySegment<byte> payload)
            {
                if (payload.Array == null || payload.Count == 0)
                    return 0;

                int offset = payload.Offset + GetAnnexBStartMarkerLength(payload);
                if (offset >= payload.Offset + payload.Count)
                    return 0;

                byte header = payload.Array[offset];
                if ((header & 0x80) != 0)
                    return 0;

                int nalType = header & 0x1F;
                if (nalType == 28)
                {
                    if (offset + 2 > payload.Offset + payload.Count)
                        return 0;

                    int fragmentedType = payload.Array[offset + 1] & 0x1F;
                    return fragmentedType > 0 && fragmentedType < 24 ? 12 : 0;
                }

                if (nalType == 24)
                    return LooksLikeH264Stap(payload, offset + 1) ? 12 : 0;

                if (nalType <= 0 || nalType >= 24)
                    return 0;

                if (nalType == 5 || nalType == 7 || nalType == 8)
                    return 9;

                if (nalType == 1)
                    return 6;

                return 2;
            }

            private static int GetH265Score(ArraySegment<byte> payload)
            {
                if (!TryReadH265Header(payload, out int nalType))
                    return 0;

                if (nalType == 48 || nalType == 49)
                    return 12;

                if (nalType >= 32 && nalType <= 34)
                    return 10;

                if (nalType >= 16 && nalType <= 21)
                    return 8;

                if (nalType <= 15)
                    return 6;

                return 3;
            }

            private static bool TryReadH265Header(ArraySegment<byte> payload, out int nalType)
            {
                nalType = 0;
                if (payload.Array == null || payload.Count < 2)
                    return false;

                int offset = payload.Offset + GetAnnexBStartMarkerLength(payload);
                if (offset + 2 > payload.Offset + payload.Count)
                    return false;

                byte first = payload.Array[offset];
                byte second = payload.Array[offset + 1];

                if ((first & 0x80) != 0)
                    return false;

                int layerId = ((first & 0x01) << 5) | ((second >> 3) & 0x1F);
                if (layerId != 0 || (second & 0x07) == 0)
                    return false;

                nalType = (first >> 1) & 0x3F;
                return nalType <= 49;
            }

            private static bool LooksLikeH264Stap(ArraySegment<byte> payload, int startOffset)
            {
                int offset = startOffset;
                int end = payload.Offset + payload.Count;
                bool hasNal = false;

                while (offset < end)
                {
                    if (end - offset < 2)
                        return false;

                    int length = BigEndianConverter.ReadUInt16(payload.Array, offset);
                    offset += 2;
                    if (length <= 0 || length > end - offset)
                        return false;

                    int nalType = payload.Array[offset] & 0x1F;
                    if ((payload.Array[offset] & 0x80) != 0 || nalType <= 0 || nalType >= 24)
                        return false;

                    hasNal = true;
                    offset += length;
                }

                return hasNal;
            }

            private static bool LooksLikeRtpJpeg(ArraySegment<byte> payload)
            {
                if (payload.Array == null || payload.Count < 8)
                    return false;

                // Annex-B H.264/H.265 payloads are occasionally sent through
                // direct RTP despite the normal RTP payload format. Their first
                // bytes can otherwise look like a JPEG header with type 103.
                if (GetAnnexBStartMarkerLength(payload) != 0)
                    return false;

                int offset = payload.Offset;
                byte typeSpecific = payload.Array[offset];
                int type = payload.Array[offset + 4];
                int width = payload.Array[offset + 6];
                int height = payload.Array[offset + 7];

                if (typeSpecific > 3 || type > 127 || width == 0 || height == 0)
                    return false;

                offset += 8;
                if (type >= 64)
                {
                    if (payload.Count < 12)
                        return false;
                    offset += 4;
                }

                byte q = payload.Array[payload.Offset + 5];
                if (q >= 128)
                {
                    if (payload.Offset + payload.Count - offset < 4)
                        return false;

                    if (payload.Array[offset] != 0)
                        return false;

                    int quantizationLength = BigEndianConverter.ReadUInt16(payload.Array, offset + 2);
                    if (quantizationLength <= 0 || quantizationLength > payload.Offset + payload.Count - offset - 4)
                        return false;
                }

                return true;
            }

            private static int GetAnnexBStartMarkerLength(ArraySegment<byte> payload)
            {
                if (payload.Array == null || payload.Count < 3)
                    return 0;

                int offset = payload.Offset;
                if (payload.Count >= 4 && payload.Array[offset] == 0 && payload.Array[offset + 1] == 0 &&
                    payload.Array[offset + 2] == 0 && payload.Array[offset + 3] == 1)
                    return 4;

                if (payload.Array[offset] == 0 && payload.Array[offset + 1] == 0 &&
                    payload.Array[offset + 2] == 1)
                    return 3;

                return 0;
            }
        }
    }
}

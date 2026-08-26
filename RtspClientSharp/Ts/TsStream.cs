using System;
using System.Collections.Generic;
using RtspClientSharp.MediaParsers;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Audio;

namespace RtspClientSharp.Ts
{
    class TsStream : ITransportStream
    {
        private const double TimestampFrequency = 90000.0;

        private readonly IMediaPayloadParser _frameSink;
        private readonly Dictionary<ushort, ElementaryStream> _streams = new Dictionary<ushort, ElementaryStream>();
        private readonly Dictionary<ushort, Pes> _currentPesByPid = new Dictionary<ushort, Pes>();
        private readonly TsPacketFactory _tsPacketFactory;

        private DateTime _baseTime;
        private TimeSpan _currentTimeOffset;
        private ushort _pmtPid;

        public int PacketsReceivedSinceLastReset { get; private set; }
        public int PacketsLostSinceLastReset { get; private set; }

        public TsStream(IMediaPayloadParser mediaPayloadParser)
        {
            _frameSink = mediaPayloadParser ?? throw new ArgumentNullException(nameof(mediaPayloadParser));
            _tsPacketFactory = new TsPacketFactory();
            _tsPacketFactory.SynchronousPacketReady = ProcessTsPacket;
        }

        public void Process(ArraySegment<byte> payloadSegment)
        {
            // TS packets are consumed synchronously by OnTsPacketReady. Use the
            // factory's pooled path so each UDP datagram does not allocate a
            // managed payload array per TS packet or an EventArgs object per
            // callback.
            _tsPacketFactory.PushDataPooled(payloadSegment);
        }

        public void ResetState()
        {
            PacketsLostSinceLastReset = 0;
            PacketsReceivedSinceLastReset = 0;
            _currentPesByPid.Clear();

            foreach (ElementaryStream stream in _streams.Values)
                stream.Reset();
        }

        private void ProcessTsPacket(TsPacket packet)
        {
            ArraySegment<byte> payload = packet.GetPayloadSegment();
            PacketsReceivedSinceLastReset++;

            if (!packet.ContainsPayload || payload.Array == null || packet.PayloadLen <= 0)
                return;

            if (packet.Pid == (ushort)PidType.PatPid)
            {
                ParsePat(packet, payload);
                return;
            }

            if (_pmtPid != 0 && packet.Pid == _pmtPid)
            {
                ParsePmt(packet, payload);
                return;
            }

            if (!_streams.TryGetValue(packet.Pid, out ElementaryStream stream))
                return;

            if (packet.PayloadUnitStartIndicator)
            {
                FlushPes(packet.Pid, stream);

                var pes = new Pes(packet);
                if (!pes.Dropped)
                    _currentPesByPid[packet.Pid] = pes;

                return;
            }

            if (_currentPesByPid.TryGetValue(packet.Pid, out Pes currentPes))
                currentPes.Add(packet);
        }

        private void ParsePat(TsPacket packet, ArraySegment<byte> payload)
        {
            int payloadStart = GetPsiPayloadStart(packet.PayloadUnitStartIndicator, payload);
            if (payloadStart < 0 || payloadStart + 8 > payload.Count)
                return;

            byte[] buffer = payload.Array;
            int offset = payload.Offset;
            int sectionLength = ((buffer[offset + payloadStart + 1] & 0x0F) << 8) |
                                buffer[offset + payloadStart + 2];
            int sectionEnd = Math.Min(payload.Count, payloadStart + 3 + sectionLength - 4);

            for (int i = payloadStart + 8; i + 3 < sectionEnd; i += 4)
            {
                int programNumber = (buffer[offset + i] << 8) | buffer[offset + i + 1];
                if (programNumber == 0)
                    continue;

                _pmtPid = (ushort)(((buffer[offset + i + 2] & 0x1F) << 8) |
                                   buffer[offset + i + 3]);
                return;
            }
        }

        private void ParsePmt(TsPacket packet, ArraySegment<byte> payload)
        {
            int payloadStart = GetPsiPayloadStart(packet.PayloadUnitStartIndicator, payload);
            if (payloadStart < 0 || payloadStart + 12 > payload.Count)
                return;

            byte[] buffer = payload.Array;
            int offset = payload.Offset;
            int sectionLength = ((buffer[offset + payloadStart + 1] & 0x0F) << 8) |
                                buffer[offset + payloadStart + 2];
            int sectionEnd = Math.Min(payload.Count, payloadStart + 3 + sectionLength - 4);
            int programInfoLength = ((buffer[offset + payloadStart + 10] & 0x0F) << 8) |
                                    buffer[offset + payloadStart + 11];
            int streamIndex = payloadStart + 12 + programInfoLength;

            while (streamIndex + 4 < sectionEnd)
            {
                byte streamType = buffer[offset + streamIndex];
                ushort elementaryPid = (ushort)(((buffer[offset + streamIndex + 1] & 0x1F) << 8) |
                                                buffer[offset + streamIndex + 2]);
                int esInfoLength = ((buffer[offset + streamIndex + 3] & 0x0F) << 8) |
                                   buffer[offset + streamIndex + 4];

                ElementaryStream stream = CreateElementaryStream(streamType);
                if (stream != null)
                {
                    if (!_streams.TryGetValue(elementaryPid, out ElementaryStream existing) ||
                        existing.StreamType != stream.StreamType)
                    {
                        _streams[elementaryPid] = stream;
                    }
                }

                streamIndex += 5 + esInfoLength;
            }
        }

        private static int GetPsiPayloadStart(bool payloadUnitStartIndicator,
            ArraySegment<byte> payload)
        {
            int payloadStart = 0;

            if (payloadUnitStartIndicator)
            {
                if (payload.Count == 0)
                    return -1;

                payloadStart = payload.Array[payload.Offset] + 1;
            }

            return payloadStart < payload.Count ? payloadStart : -1;
        }

        private ElementaryStream CreateElementaryStream(byte streamType)
        {
            switch (streamType)
            {
                case 0x0F:
                    return new ElementaryStream(streamType, ElementaryStreamKind.Aac, RaiseFrame, GetTimestamp);
                case 0x1B:
                    return new ElementaryStream(streamType, ElementaryStreamKind.H264, RaiseFrame, GetTimestamp);
                case 0x24:
                    return new ElementaryStream(streamType, ElementaryStreamKind.H265, RaiseFrame, GetTimestamp);
                default:
                    return null;
            }
        }

        private void FlushPes(ushort pid, ElementaryStream stream)
        {
            if (!_currentPesByPid.TryGetValue(pid, out Pes pes))
                return;

            _currentPesByPid.Remove(pid);

            if (!pes.Decode())
                return;

            int startOfData = 6;
            if (pes.OptionalPesHeader != null && pes.OptionalPesHeader.MarkerBits == 2)
                startOfData += 3 + pes.OptionalPesHeader.PesHeaderLength;

            if (pes.Data == null || startOfData >= pes.Data.Length)
                return;

            TimeSpan timestamp = pes.Timestamp;
            if (timestamp != TimeSpan.MinValue)
                _currentTimeOffset = timestamp;

            stream.Parse(new ArraySegment<byte>(pes.Data, startOfData, pes.Data.Length - startOfData));
        }

        private DateTime GetTimestamp()
        {
            if (_baseTime == default(DateTime))
                _baseTime = DateTime.UtcNow;

            return _baseTime + _currentTimeOffset;
        }

        private void RaiseFrame(RawFrame frame)
        {
            _frameSink.FrameGenerated?.Invoke(frame);
        }

        private enum ElementaryStreamKind
        {
            H264,
            H265,
            Aac
        }

        private sealed class ElementaryStream
        {
            private readonly Action<RawFrame> _frameGenerated;
            private readonly Func<DateTime> _timestampProvider;
            private readonly H264Parser _h264Parser;
            private readonly H265Parser _h265Parser;
            private byte[] _aacConfig = new byte[0];

            public byte StreamType { get; }
            private ElementaryStreamKind Kind { get; }

            public ElementaryStream(byte streamType, ElementaryStreamKind kind, Action<RawFrame> frameGenerated,
                Func<DateTime> timestampProvider)
            {
                StreamType = streamType;
                Kind = kind;
                _frameGenerated = frameGenerated;
                _timestampProvider = timestampProvider;

                if (kind == ElementaryStreamKind.H264)
                    _h264Parser = new H264Parser(timestampProvider) { FrameGenerated = frameGenerated };
                else if (kind == ElementaryStreamKind.H265)
                    _h265Parser = new H265Parser(timestampProvider) { FrameGenerated = frameGenerated };
            }

            public void Parse(ArraySegment<byte> payload)
            {
                switch (Kind)
                {
                    case ElementaryStreamKind.H264:
                        _h264Parser.Parse(payload, true);
                        break;
                    case ElementaryStreamKind.H265:
                        _h265Parser.Parse(payload, true);
                        break;
                    case ElementaryStreamKind.Aac:
                        ParseAdts(payload);
                        break;
                }
            }

            public void Reset()
            {
                _h264Parser?.ResetState();
                _h265Parser?.ResetState();
            }

            private void ParseAdts(ArraySegment<byte> payload)
            {
                int offset = payload.Offset;
                int end = payload.Offset + payload.Count;

                while (offset + 7 <= end)
                {
                    byte[] buffer = payload.Array;
                    if (buffer[offset] != 0xFF || (buffer[offset + 1] & 0xF0) != 0xF0)
                        break;

                    int protectionAbsent = buffer[offset + 1] & 0x01;
                    int headerLength = protectionAbsent == 1 ? 7 : 9;
                    int frameLength = ((buffer[offset + 3] & 0x03) << 11) |
                                      (buffer[offset + 4] << 3) |
                                      ((buffer[offset + 5] & 0xE0) >> 5);

                    if (frameLength < headerLength || offset + frameLength > end)
                        break;

                    UpdateAacConfig(buffer, offset);

                    var frame = new ArraySegment<byte>(buffer, offset + headerLength, frameLength - headerLength);
                    _frameGenerated(new RawAACFrame(_timestampProvider(), frame, new ArraySegment<byte>(_aacConfig)));
                    offset += frameLength;
                }
            }

            private void UpdateAacConfig(byte[] buffer, int offset)
            {
                int profile = ((buffer[offset + 2] & 0xC0) >> 6) + 1;
                int frequencyIndex = (buffer[offset + 2] & 0x3C) >> 2;
                int channelConfig = ((buffer[offset + 2] & 0x01) << 2) | ((buffer[offset + 3] & 0xC0) >> 6);

                byte config0 = (byte)((profile << 3) | (frequencyIndex >> 1));
                byte config1 = (byte)(((frequencyIndex & 0x01) << 7) | (channelConfig << 3));

                if (_aacConfig.Length == 2 && _aacConfig[0] == config0 && _aacConfig[1] == config1)
                    return;

                _aacConfig = new[] { config0, config1 };
            }
        }
    }
}

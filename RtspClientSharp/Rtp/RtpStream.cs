using System;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.MediaParsers;

using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.Rtp
{
    class RtpStream : ITransportStream, IRtpStatisticsProvider, IVideoCodecDetector
    {
        private readonly IRtpSequenceAssembler _rtpSequenceAssembler;
        private readonly IMediaPayloadParser _mediaPayloadParser;
        private readonly int _samplesFrequency;
        private readonly bool _ensureVideoInputPadding;

        private ulong _samplesSum;
        private ushort _previousSeqNumber;
        private uint _previousTimestamp;
        private TimeSpan _currentTimeOffset;
        private bool _isFirstPacket = true;

        public uint SyncSourceId { get; private set; }
        public ushort HighestSequenceNumberReceived { get; private set; }
        public int PacketsReceivedSinceLastReset { get; private set; }
        public int PacketsLostSinceLastReset { get; private set; }
        public uint CumulativePacketLost { get; private set; }
        public ushort SequenceCycles { get; private set; }
        public CodecInfoType DetectedVideoCodec =>
            (_mediaPayloadParser as IVideoCodecDetector)?.DetectedVideoCodec ?? CodecInfoType.Auto;

        public RtpStream(IMediaPayloadParser mediaPayloadParser, int samplesFrequency,
            IRtpSequenceAssembler rtpSequenceAssembler = null, bool ensureVideoInputPadding = false)
        {
            _mediaPayloadParser = mediaPayloadParser ?? throw new ArgumentNullException(nameof(mediaPayloadParser));
            if (samplesFrequency < 0)
                throw new ArgumentOutOfRangeException(nameof(samplesFrequency));

            _samplesFrequency = samplesFrequency;
            _ensureVideoInputPadding = ensureVideoInputPadding;

            if (rtpSequenceAssembler != null)
            {
                _rtpSequenceAssembler = rtpSequenceAssembler;
                _rtpSequenceAssembler.PacketPassed += ProcessImmediately;
            }
        }

        public void Process(ArraySegment<byte> payloadSegment)
        {
            if (!RtpPacket.TryParse(payloadSegment, out RtpPacket rtpPacket))
                return;

            if (_rtpSequenceAssembler != null)
                _rtpSequenceAssembler.ProcessPacket(ref rtpPacket);
            else
                ProcessImmediately(ref rtpPacket);
        }

        private void ProcessImmediately(ref RtpPacket rtpPacket)
        {
            SyncSourceId = rtpPacket.SyncSourceId;

            if (_isFirstPacket)
            {
                _currentTimeOffset = _samplesFrequency != 0
                    ? TimeSpan.Zero
                    : TimeSpan.MinValue;
            }
            else
            {
                int delta = (ushort)(rtpPacket.SeqNumber - _previousSeqNumber);

                if (delta == 0 || delta > ushort.MaxValue / 2)
                    return;

                if (delta != 1)
                {
                    int lostCount = delta - 1;

                    CumulativePacketLost += (uint)lostCount;

                    if (CumulativePacketLost > 0x7FFFFF)
                        CumulativePacketLost = 0x7FFFFF;

                    PacketsLostSinceLastReset += lostCount;

                    _mediaPayloadParser.ResetState();
                }

                if (rtpPacket.SeqNumber < HighestSequenceNumberReceived)
                    ++SequenceCycles;

                if (rtpPacket.Timestamp != _previousTimestamp)
                {
                    _samplesSum += unchecked(rtpPacket.Timestamp - _previousTimestamp);
                    _currentTimeOffset = _samplesFrequency != 0
                        ? TimeSpan.FromSeconds(_samplesSum / (double)_samplesFrequency)
                        : TimeSpan.MinValue;
                }
            }

            HighestSequenceNumberReceived = rtpPacket.SeqNumber;

            _isFirstPacket = false;
            ++PacketsReceivedSinceLastReset;
            _previousSeqNumber = rtpPacket.SeqNumber;
            _previousTimestamp = rtpPacket.Timestamp;

            if (rtpPacket.PayloadSegment.Count == 0)
                return;

            // Direct UDP buffers are owned by the receive loop and are not read
            // again until this synchronous parser callback returns. Zeroing the
            // FFmpeg tail here lets the decoder consume the payload directly.
            // TCP/TPKT callers leave this disabled because bytes after a payload
            // may already contain the next interleaved packet.
            if (_ensureVideoInputPadding)
                RawVideoFramePadding.ClearIfAvailable(rtpPacket.PayloadSegment);

            _mediaPayloadParser.Parse(_currentTimeOffset, rtpPacket.PayloadSegment, rtpPacket.MarkerBit);
        }

        public void ResetState()
        {
            PacketsLostSinceLastReset = 0;
            PacketsReceivedSinceLastReset = 0;
        }
    }
}

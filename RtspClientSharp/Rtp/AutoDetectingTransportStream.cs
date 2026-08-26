using System;
using System.Collections.Generic;
using RtspClientSharp.Ts;

namespace RtspClientSharp.Rtp
{
    sealed class AutoDetectingTransportStream : ITransportStream
    {
        private readonly Func<ITransportStream> _rtpStreamFactory;
        private readonly Func<ITransportStream> _mpegTsStreamFactory;
        private readonly MediaTransportMode _configuredMode;
        private readonly List<byte> _probeData = new List<byte>();

        private ITransportStream _selectedStream;

        public MediaTransportMode DetectedMode { get; private set; } = MediaTransportMode.Auto;

        public AutoDetectingTransportStream(MediaTransportMode configuredMode,
            Func<ITransportStream> rtpStreamFactory, Func<ITransportStream> mpegTsStreamFactory)
        {
            if (configuredMode != MediaTransportMode.Auto && configuredMode != MediaTransportMode.Rtp &&
                configuredMode != MediaTransportMode.MpegTs)
                throw new ArgumentOutOfRangeException(nameof(configuredMode));

            _configuredMode = configuredMode;
            _rtpStreamFactory = rtpStreamFactory ?? throw new ArgumentNullException(nameof(rtpStreamFactory));
            _mpegTsStreamFactory = mpegTsStreamFactory ??
                                   throw new ArgumentNullException(nameof(mpegTsStreamFactory));

            if (configuredMode != MediaTransportMode.Auto)
                SelectStream(configuredMode);
        }

        public void Process(ArraySegment<byte> payloadSegment)
        {
            if (_selectedStream != null)
            {
                _selectedStream.Process(payloadSegment);
                return;
            }

            if (payloadSegment.Array == null || payloadSegment.Count == 0)
                return;

            // A complete RTP datagram can be selected immediately. This check comes before
            // buffering because RTP packets are datagram-delimited, unlike raw MPEG-TS data.
            if (RtpPacket.TryParse(payloadSegment, out _))
            {
                SelectStream(MediaTransportMode.Rtp);
                _selectedStream.Process(payloadSegment);
                return;
            }

            // Raw TS normally arrives as 188-byte-aligned UDP datagrams. Keep a small probe
            // buffer as well so a source that splits a TS packet across datagrams is supported.
            if (_probeData.Count == 0 && payloadSegment.Array[payloadSegment.Offset] != 0x47)
                return;

            AppendProbeData(payloadSegment);

            if (_probeData.Count < TsPacketFactory.TsPacketFixedSize)
                return;

            byte[] probeBytes = _probeData.ToArray();
            if (!LooksLikeMpegTs(probeBytes))
            {
                _probeData.Clear();
                return;
            }

            SelectStream(MediaTransportMode.MpegTs);
            _probeData.Clear();
            _selectedStream.Process(new ArraySegment<byte>(probeBytes));
        }

        private void SelectStream(MediaTransportMode mode)
        {
            if (_selectedStream != null)
                return;

            if (mode == MediaTransportMode.Rtp)
                _selectedStream = _rtpStreamFactory();
            else if (mode == MediaTransportMode.MpegTs)
                _selectedStream = _mpegTsStreamFactory();
            else
                throw new InvalidOperationException($"Unsupported media transport mode: {_configuredMode}");

            if (_selectedStream == null)
                throw new InvalidOperationException($"The {mode} stream factory returned null");

            DetectedMode = mode;
        }

        private void AppendProbeData(ArraySegment<byte> payloadSegment)
        {
            for (int i = 0; i < payloadSegment.Count; i++)
                _probeData.Add(payloadSegment.Array[payloadSegment.Offset + i]);
        }

        private static bool LooksLikeMpegTs(byte[] data)
        {
            if (data == null || data.Length < TsPacketFactory.TsPacketFixedSize || data[0] != 0x47)
                return false;

            // If more than one complete packet is present, require sync at every packet
            // boundary. A single packet is enough because RTP version 2 cannot start with 0x47.
            for (int offset = TsPacketFactory.TsPacketFixedSize;
                 offset + TsPacketFactory.TsPacketFixedSize <= data.Length;
                 offset += TsPacketFactory.TsPacketFixedSize)
            {
                if (data[offset] != 0x47)
                    return false;
            }

            return true;
        }
    }
}

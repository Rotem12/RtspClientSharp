using System;
using System.Diagnostics;
using RtspClientSharp.Utils;

namespace RtspClientSharp.Rtp
{
    struct RtpPacket
    {
        public const int RtpHeaderSize = 12;
        public const int RtpProtocolVersion = 2;

        public int ProtocolVersion { get; private set; }
        public bool PaddingFlag { get; private set; }
        public bool ExtensionFlag { get; private set; }
        public int CsrcCount { get; private set; }
        public bool MarkerBit { get; private set; }
        public int PayloadType { get; private set; }
        public ushort SeqNumber { get; private set; }
        public uint Timestamp { get; private set; }
        public uint SyncSourceId { get; private set; }
        public int ExtensionHeaderId { get; private set; }

        public ArraySegment<byte> PayloadSegment { get; set; }

        internal RtpPacket(ushort seqNumber, ArraySegment<byte> payloadSegment)
        {
            ProtocolVersion = 1;
            PaddingFlag = false;
            ExtensionFlag = false;
            CsrcCount = 0;
            MarkerBit = false;
            PayloadType = 0;
            SeqNumber = 0;
            Timestamp = 0;
            SyncSourceId = 0;
            ExtensionHeaderId = 0;
            SeqNumber = seqNumber;
            PayloadSegment = payloadSegment;
        }

        public static bool TryParse(ArraySegment<byte> byteSegment, out RtpPacket rtpPacket)
        {
            rtpPacket = new RtpPacket();

            if (byteSegment.Array == null || byteSegment.Count < RtpHeaderSize)
                return false;

            int offset = byteSegment.Offset;
            int endOffset = byteSegment.Offset + byteSegment.Count;
            rtpPacket.ProtocolVersion = byteSegment.Array[offset] >> 6;

            if (rtpPacket.ProtocolVersion != RtpProtocolVersion)
                return false;

            rtpPacket.PaddingFlag = (byteSegment.Array[offset] >> 5 & 1) != 0;
            rtpPacket.ExtensionFlag = (byteSegment.Array[offset] >> 4 & 1) != 0;
            rtpPacket.CsrcCount = byteSegment.Array[offset++] & 0xF;

            rtpPacket.MarkerBit = byteSegment.Array[offset] >> 7 != 0;
            rtpPacket.PayloadType = byteSegment.Array[offset++] & 0x7F;

            rtpPacket.SeqNumber = (ushort) BigEndianConverter.ReadUInt16(byteSegment.Array, offset);
            offset += 2;

            rtpPacket.Timestamp = BigEndianConverter.ReadUInt32(byteSegment.Array, offset);
            offset += 4;

            rtpPacket.SyncSourceId = BigEndianConverter.ReadUInt32(byteSegment.Array, offset);
            offset += 4 + 4 * rtpPacket.CsrcCount;

            if (offset > endOffset)
                return false;

            if (rtpPacket.ExtensionFlag)
            {
                if (endOffset - offset < 4)
                    return false;

                rtpPacket.ExtensionHeaderId = BigEndianConverter.ReadUInt16(byteSegment.Array, offset);
                offset += 2;

                int extensionHeaderLength = BigEndianConverter.ReadUInt16(byteSegment.Array, offset) * 4;
                offset += 2 + extensionHeaderLength;

                if (offset > endOffset)
                    return false;
            }

            int payloadSize = endOffset - offset;

            if (rtpPacket.PaddingFlag)
            {
                if (payloadSize == 0)
                    return false;

                int paddingBytes = byteSegment.Array[endOffset - 1];

                if (paddingBytes == 0 || paddingBytes > payloadSize)
                    return false;

                payloadSize -= paddingBytes;
            }

            rtpPacket.PayloadSegment = new ArraySegment<byte>(byteSegment.Array, offset, payloadSize);
            return true;
        }
    }
}

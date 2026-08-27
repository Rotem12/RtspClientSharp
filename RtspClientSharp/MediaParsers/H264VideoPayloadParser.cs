using System;
using System.Diagnostics;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.Utils;

namespace RtspClientSharp.MediaParsers
{
    class H264VideoPayloadParser : MediaPayloadParser
    {
        enum PackModeType
        {
            STAP_A = 24,
            STAP_B = 25,
            MTAP16 = 26,
            MTAP24 = 27,
            FU_A = 28,
            FU_B = 29
        }

        const int DecodingOrderNumberFieldSize = 2;
        const int DondFieldSize = 1;

        private readonly H264Parser _h264Parser;
        private bool _waitForStartFu = true;
        private TimeSpan _timeOffset = TimeSpan.MinValue;

        public H264VideoPayloadParser(H264CodecInfo codecInfo)
        {
            if (codecInfo == null)
                throw new ArgumentNullException(nameof(codecInfo));
            if (codecInfo.SpsPpsBytes == null)
                throw new ArgumentException($"{nameof(codecInfo.SpsPpsBytes)} is null", nameof(codecInfo));

            _h264Parser = new H264Parser(() => GetFrameTimestamp(_timeOffset)) { FrameGenerated = OnFrameGenerated };

            if (codecInfo.SpsPpsBytes.Length != 0)
                _h264Parser.Parse(new ArraySegment<byte>(codecInfo.SpsPpsBytes), false);

        }

        public override void Parse(TimeSpan timeOffset, ArraySegment<byte> byteSegment, bool markerBit)
        {
            if (byteSegment.Array == null || byteSegment.Count == 0)
            {
                ResetState();
                return;
            }

            if (!markerBit && timeOffset != _timeOffset)
                _h264Parser.TryGenerateFrame();

            _timeOffset = timeOffset;

            PackModeType packMode = (PackModeType)(byteSegment.Array[byteSegment.Offset] & 0x1F);

            switch (packMode)
            {
                case PackModeType.FU_A:
                    ParseFU(byteSegment, 0, markerBit);
                    break;
                case PackModeType.FU_B:
                    ParseFU(byteSegment, DecodingOrderNumberFieldSize, markerBit);
                    break;
                case PackModeType.STAP_A:
                    ParseSTAP(byteSegment, 0, markerBit);
                    break;
                case PackModeType.STAP_B:
                    ParseSTAP(byteSegment, DecodingOrderNumberFieldSize, markerBit);
                    break;
                case PackModeType.MTAP16:
                    ParseMTAP(byteSegment, 2, markerBit);
                    break;
                case PackModeType.MTAP24:
                    ParseMTAP(byteSegment, 3, markerBit);
                    break;
                default:
                    _h264Parser.Parse(byteSegment, markerBit);
                    break;
            }
        }

        public override void ResetState()
        {
            _h264Parser.ResetState();
            _waitForStartFu = true;
        }

        private void ParseFU(ArraySegment<byte> byteSegment, int donFieldSize, bool markerBit)
        {
            if (byteSegment.Array == null || byteSegment.Count < 2 + donFieldSize)
            {
                ResetState();
                return;
            }

            int endOffset = byteSegment.Offset + byteSegment.Count;
            int fuIndicatorOffset = byteSegment.Offset;
            int fuHeaderOffset = fuIndicatorOffset + 1;
            int fuHeader = byteSegment.Array[fuHeaderOffset];
            bool startFlag = (fuHeader & 0x80) != 0;
            bool endFlag = (fuHeader & 0x40) != 0;

            if (startFlag)
            {
                int nalHeaderOffset = fuHeaderOffset + donFieldSize;
                int fragmentOffset = nalHeaderOffset + 1;

                if (fragmentOffset >= endOffset)
                {
                    ResetState();
                    return;
                }

                byte nalHeader = (byte)((fuHeader & 0x1F) | (byteSegment.Array[fuIndicatorOffset] & 0xE0));

                // A new start always replaces a previous incomplete or single-packet FU.
                _h264Parser.BeginFragmentedNal(nalHeader,
                    new ArraySegment<byte>(byteSegment.Array, fragmentOffset, endOffset - fragmentOffset));

                if (endFlag)
                    ParseCompletedNal(markerBit);
                else
                    _waitForStartFu = false;

                return;
            }

            if (_waitForStartFu)
                return;

            int payloadOffset = byteSegment.Offset + 2 + donFieldSize;

            if (payloadOffset >= endOffset)
            {
                ResetState();
                return;
            }

            _h264Parser.AppendFragmentedNal(
                new ArraySegment<byte>(byteSegment.Array, payloadOffset, endOffset - payloadOffset));

            if (endFlag)
                ParseCompletedNal(markerBit);
        }

        private void ParseCompletedNal(bool markerBit)
        {
            if (!_h264Parser.HasFragmentedNal)
            {
                ResetState();
                return;
            }

            _h264Parser.CompleteFragmentedNal(markerBit);
            _waitForStartFu = true;
        }

        private void ParseSTAP(ArraySegment<byte> byteSegment, int donFieldSize,
        bool markerBit)
        {
            Debug.Assert(byteSegment.Array != null, "byteSegment.Array != null");

            int startOffset = byteSegment.Offset + 1 + donFieldSize;
            int endOffset = byteSegment.Offset + byteSegment.Count;

            if (startOffset >= endOffset)
            {
                ResetState();
                return;
            }

            while (startOffset < endOffset)
            {
                if (endOffset - startOffset < 2)
                {
                    ResetState();
                    return;
                }

                int nalUnitSize = BigEndianConverter.ReadUInt16(byteSegment.Array, startOffset);

                startOffset += 2;

                if (nalUnitSize <= 0 || nalUnitSize > endOffset - startOffset)
                {
                    ResetState();
                    return;
                }

                var nalUnitSegment = new ArraySegment<byte>(byteSegment.Array, startOffset, nalUnitSize);

                startOffset += nalUnitSize;

                _h264Parser.Parse(nalUnitSegment, markerBit && startOffset == endOffset);
            }
        }

        private void ParseMTAP(ArraySegment<byte> byteSegment, int tsOffsetFieldSize,
            bool markerBit)
        {
            Debug.Assert(byteSegment.Array != null, "byteSegment.Array != null");

            int startOffset = byteSegment.Offset;
            int endOffset = byteSegment.Offset + byteSegment.Count;

            startOffset += 1 + DecodingOrderNumberFieldSize;

            if (startOffset >= endOffset)
            {
                ResetState();
                return;
            }

            while (startOffset < endOffset)
            {
                if (endOffset - startOffset < 2)
                {
                    ResetState();
                    return;
                }

                int nalUnitSize = BigEndianConverter.ReadUInt16(byteSegment.Array, startOffset);

                startOffset += 2 + DondFieldSize + tsOffsetFieldSize;

                if (nalUnitSize <= 0 || startOffset > endOffset || nalUnitSize > endOffset - startOffset)
                {
                    ResetState();
                    return;
                }

                var nalUnitSegment = new ArraySegment<byte>(byteSegment.Array, startOffset, nalUnitSize);

                startOffset += nalUnitSize;

                _h264Parser.Parse(nalUnitSegment, markerBit && startOffset == endOffset);
            }
        }
    }
}

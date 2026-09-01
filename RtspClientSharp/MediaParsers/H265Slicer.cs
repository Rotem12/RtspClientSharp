using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Rtp;
using RtspClientSharp.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace RtspClientSharp.MediaParsers
{
    static class H265Slicer
    {
        public static void Slice(ArraySegment<byte> byteSegment, Action<ArraySegment<byte>> nalUnitHandler)
        {
            Debug.Assert(byteSegment.Array != null, "byteSegment.Array != null");

            if (!StartsWithStartMarker(byteSegment))
                return;

            int endIndex = byteSegment.Offset + byteSegment.Count;

            int markerLength;
            int nalUnitStartIndex = FindStartMarker(byteSegment.Array, byteSegment.Offset, endIndex,
                out markerLength);

            if (nalUnitStartIndex == -1)
                nalUnitHandler?.Invoke(byteSegment);

            while (true)
            {
                int tailLength = endIndex - nalUnitStartIndex;

                if (tailLength <= markerLength)
                    return;
                
                int nalUnitType = (byteSegment.Array[nalUnitStartIndex + markerLength] >> 1) & 0x3F;

                if (!RtpH265TypeUtils.CheckIfIsValid(nalUnitType))
                    throw new H265ParserException($"Invalid (HEVC) NAL Unit Type { nalUnitType }");

                if (RtpH265TypeUtils.IsVideoCodingLayerNalUnit(nalUnitType))
                {
                    nalUnitHandler?.Invoke(new ArraySegment<byte>(byteSegment.Array, nalUnitStartIndex, tailLength));
                    return;
                }

                int nextMarkerLength;
                int nextNalUnitStartIndex = FindStartMarker(byteSegment.Array,
                    nalUnitStartIndex + markerLength, endIndex, out nextMarkerLength);

                if(nextNalUnitStartIndex >= 0)
                {
                    int nalUnitLength = nextNalUnitStartIndex - nalUnitStartIndex;

                    if (nalUnitLength > markerLength)
                        nalUnitHandler?.Invoke(new ArraySegment<byte>(byteSegment.Array, nalUnitStartIndex, nalUnitLength));
                }
                else
                {
                    nalUnitHandler?.Invoke(new ArraySegment<byte>(byteSegment.Array, nalUnitStartIndex, tailLength));
                    return;
                }

                nalUnitStartIndex = nextNalUnitStartIndex;
                markerLength = nextMarkerLength;
            }
        }

        public static bool StartsWithStartMarker(ArraySegment<byte> byteSegment)
        {
            if (byteSegment.Array == null || byteSegment.Count < 3)
                return false;

            return GetStartMarkerLength(byteSegment.Array, byteSegment.Offset,
                byteSegment.Offset + byteSegment.Count) != 0;
        }

        public static int GetStartMarkerLength(byte[] data, int index, int endIndex)
        {
            if (data == null || index < 0 || index >= endIndex)
                return 0;

            if (index + 4 <= endIndex && data[index] == 0 && data[index + 1] == 0 &&
                data[index + 2] == 0 && data[index + 3] == 1)
                return RawH265Frame.StartMarkerSize;

            if (index + 3 <= endIndex && data[index] == 0 && data[index + 1] == 0 &&
                data[index + 2] == 1)
                return 3;

            return 0;
        }

        private static int FindStartMarker(byte[] data, int startIndex, int endIndex,
            out int markerLength)
        {
            for (int index = startIndex; index + 2 < endIndex; index++)
            {
                markerLength = GetStartMarkerLength(data, index, endIndex);
                if (markerLength != 0)
                    return index;
            }

            markerLength = 0;
            return -1;
        }
    }
}

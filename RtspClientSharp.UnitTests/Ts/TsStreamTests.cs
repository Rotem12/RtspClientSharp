using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.MediaParsers;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Audio;
using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Ts;

namespace RtspClientSharp.UnitTests.Ts
{
    [TestClass]
    public class TsStreamTests
    {
        [TestMethod]
        public void Process_H264TransportStreamWithOffset_GeneratesVideoFrame()
        {
            var frames = new List<RawFrame>();
            var stream = new TsStream(new CapturingParser { FrameGenerated = frames.Add });

            byte[] spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            byte[] ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            byte[] iFrameBytes = { 0x0, 0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00 };
            byte[] accessUnit = spsBytes.Concat(ppsBytes).Concat(iFrameBytes).ToArray();
            byte[][] packets = BuildProgramPackets(0x1B, 0x0101, 0xE0, accessUnit);

            foreach (byte[] packet in packets)
            {
                byte[] buffer = new byte[packet.Length + 5];
                Buffer.BlockCopy(packet, 0, buffer, 5, packet.Length);
                stream.Process(new ArraySegment<byte>(buffer, 5, packet.Length));
            }

            Assert.AreEqual(packets.Length, stream.PacketsReceivedSinceLastReset);
            Assert.AreEqual(1, frames.Count);
            Assert.IsInstanceOfType(frames[0], typeof(RawH264IFrame));
            Assert.IsTrue(frames[0].FrameSegment.SequenceEqual(iFrameBytes));
        }

        [TestMethod]
        public void Process_H264TransportStreamSplitAcrossDatagrams_GeneratesVideoFrame()
        {
            var frames = new List<RawFrame>();
            var stream = new TsStream(new CapturingParser { FrameGenerated = frames.Add });

            byte[] spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            byte[] ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            byte[] iFrameBytes = { 0x0, 0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00 };
            byte[] accessUnit = spsBytes.Concat(ppsBytes).Concat(iFrameBytes).ToArray();
            byte[][] packets = BuildProgramPackets(0x1B, 0x0101, 0xE0, accessUnit);
            byte[] buffer = packets.SelectMany(packet => packet).ToArray();

            stream.Process(new ArraySegment<byte>(buffer, 0, 100));
            stream.Process(new ArraySegment<byte>(buffer, 100, buffer.Length - 100));

            Assert.AreEqual(packets.Length, stream.PacketsReceivedSinceLastReset);
            Assert.AreEqual(1, frames.Count);
            Assert.IsInstanceOfType(frames[0], typeof(RawH264IFrame));
            Assert.IsTrue(frames[0].FrameSegment.SequenceEqual(iFrameBytes));
        }

        [TestMethod]
        public void Process_AacTransportStream_GeneratesAudioFrame()
        {
            var frames = new List<RawFrame>();
            var stream = new TsStream(new CapturingParser { FrameGenerated = frames.Add });
            byte[] adtsFrame = { 0xFF, 0xF1, 0x50, 0x80, 0x01, 0x3F, 0xFC, 0x11, 0x22 };
            byte[][] packets = BuildProgramPackets(0x0F, 0x0101, 0xC0, adtsFrame);

            foreach (byte[] packet in packets)
                stream.Process(new ArraySegment<byte>(packet));

            Assert.AreEqual(packets.Length, stream.PacketsReceivedSinceLastReset);
            Assert.AreEqual(1, frames.Count);
            var aacFrame = (RawAACFrame)frames[0];
            Assert.IsTrue(aacFrame.FrameSegment.SequenceEqual(new byte[] { 0x11, 0x22 }));
            Assert.IsTrue(aacFrame.ConfigSegment.SequenceEqual(new byte[] { 0x12, 0x10 }));
        }

        private static byte[][] BuildProgramPackets(byte streamType, ushort elementaryPid, byte streamId, byte[] elementaryData)
        {
            byte[] pat = BuildTsPacket(0x0000, true, new byte[]
            {
                0x00,
                0x00, 0xB0, 0x0D,
                0x00, 0x01,
                0xC1,
                0x00,
                0x00,
                0x00, 0x01,
                0xE1, 0x00,
                0x00, 0x00, 0x00, 0x00
            });

            byte[] pmt = BuildTsPacket(0x0100, true, new byte[]
            {
                0x00,
                0x02, 0xB0, 0x12,
                0x00, 0x01,
                0xC1,
                0x00,
                0x00,
                0xE1, 0x01,
                0xF0, 0x00,
                streamType,
                (byte)(0xE0 | (elementaryPid >> 8)),
                (byte)(elementaryPid & 0xFF),
                0xF0, 0x00,
                0x00, 0x00, 0x00, 0x00
            });

            var packets = new List<byte[]> { pat, pmt };
            byte[] pes = BuildPes(streamId, elementaryData);

            int offset = 0;
            bool payloadStart = true;
            while (offset < pes.Length)
            {
                int payloadLength = Math.Min(184, pes.Length - offset);
                var payload = new byte[payloadLength];
                Buffer.BlockCopy(pes, offset, payload, 0, payloadLength);
                packets.Add(BuildTsPacket(elementaryPid, payloadStart, payload));
                payloadStart = false;
                offset += payloadLength;
            }

            packets.Add(BuildTsPacket(elementaryPid, true, BuildPes(streamId, new byte[0])));
            packets.Add(BuildTsPacket(0x1FFF, false, new byte[184]));
            return packets.ToArray();
        }

        private static byte[] BuildPes(byte streamId, byte[] elementaryData)
        {
            int pesLength = elementaryData.Length + 8;
            var pes = new byte[14 + elementaryData.Length];
            pes[0] = 0x00;
            pes[1] = 0x00;
            pes[2] = 0x01;
            pes[3] = streamId;
            pes[4] = (byte)(pesLength >> 8);
            pes[5] = (byte)(pesLength & 0xFF);
            pes[6] = 0x80;
            pes[7] = 0x80;
            pes[8] = 0x05;
            WritePts(pes, 9, 0);
            Buffer.BlockCopy(elementaryData, 0, pes, 14, elementaryData.Length);
            return pes;
        }

        private static void WritePts(byte[] data, int offset, long pts)
        {
            data[offset] = (byte)(0x20 | (((pts >> 30) & 0x07) << 1) | 0x01);
            data[offset + 1] = (byte)(pts >> 22);
            data[offset + 2] = (byte)((((pts >> 15) & 0x7F) << 1) | 0x01);
            data[offset + 3] = (byte)(pts >> 7);
            data[offset + 4] = (byte)(((pts & 0x7F) << 1) | 0x01);
        }

        private static byte[] BuildTsPacket(ushort pid, bool payloadUnitStart, byte[] payload)
        {
            var packet = new byte[188];
            packet[0] = 0x47;
            packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
            packet[2] = (byte)(pid & 0xFF);

            if (payload.Length == 184)
            {
                packet[3] = 0x10;
                Buffer.BlockCopy(payload, 0, packet, 4, payload.Length);
                return packet;
            }

            int adaptationFieldLength = 183 - payload.Length;
            packet[3] = 0x30;
            packet[4] = (byte)adaptationFieldLength;

            if (adaptationFieldLength > 0)
            {
                packet[5] = 0x00;
                for (int i = 6; i < 5 + adaptationFieldLength; i++)
                    packet[i] = 0xFF;
            }

            Buffer.BlockCopy(payload, 0, packet, 5 + adaptationFieldLength, payload.Length);
            return packet;
        }

        private sealed class CapturingParser : IMediaPayloadParser
        {
            public Action<RawFrame> FrameGenerated { get; set; }

            public void Parse(TimeSpan timeOffset, ArraySegment<byte> byteSegment, bool markerBit)
            {
                throw new NotSupportedException();
            }

            public void ResetState()
            {
            }
        }
    }
}

using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.MediaParsers;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.UnitTests.MediaParsers
{
    [TestClass]
    public class AutoDetectingVideoPayloadParserTests
    {
        [TestMethod]
        public void Parse_H264RtpPayload_AutomaticallySelectsH264()
        {
            RawFrame frame = null;
            var parser = new AutoDetectingVideoPayloadParser(CreateH264Parameters())
            {
                FrameGenerated = generatedFrame => frame = generatedFrame
            };

            parser.Parse(TimeSpan.Zero,
                new ArraySegment<byte>(new byte[] {0x65, 0x88, 0x80, 0x10, 0x00}), true);

            Assert.AreEqual(CodecInfoType.H264, parser.DetectedVideoCodec);
            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
        }

        [TestMethod]
        public void Parse_H264RtpWithAnnexBParameterPayloads_AutomaticallyGeneratesFrame()
        {
            RawFrame frame = null;
            var parser = new AutoDetectingVideoPayloadParser
            {
                FrameGenerated = generatedFrame => frame = generatedFrame
            };

            parser.Parse(TimeSpan.Zero,
                new ArraySegment<byte>(Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA")), false);
            parser.Parse(TimeSpan.Zero,
                new ArraySegment<byte>(Convert.FromBase64String("AAAAAWjuPIA=")), false);
            parser.Parse(TimeSpan.Zero,
                new ArraySegment<byte>(new byte[] {0x65, 0x88, 0x80, 0x10, 0x00}), true);

            Assert.AreEqual(CodecInfoType.H264, parser.DetectedVideoCodec);
            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
        }

        [TestMethod]
        public void Parse_H265RtpPayload_AutomaticallySelectsH265()
        {
            RawFrame frame = null;
            var parser = new AutoDetectingVideoPayloadParser
            {
                FrameGenerated = generatedFrame => frame = generatedFrame
            };

            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[] {0x40, 0x01, 0x80}), true);
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[] {0x42, 0x01, 0x80}), true);
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[] {0x44, 0x01, 0x80}), true);
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[] {0x26, 0x01, 0x80}), true);

            Assert.AreEqual(CodecInfoType.H265, parser.DetectedVideoCodec);
            Assert.IsInstanceOfType(frame, typeof(RawH265IFrame));
        }

        [TestMethod]
        public void Parse_MjpegRtpPayload_AutomaticallySelectsMjpeg()
        {
            RawFrame frame = null;
            var parser = new AutoDetectingVideoPayloadParser
            {
                FrameGenerated = generatedFrame => frame = generatedFrame
            };

            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(CreateMjpegPayload(0,
                new byte[] {0x11, 0x22, 0xFF, 0xD9})), false);
            parser.Parse(TimeSpan.FromMilliseconds(40), new ArraySegment<byte>(CreateMjpegPayload(0,
                new byte[] {0x33})), false);

            Assert.AreEqual(CodecInfoType.MJPEG, parser.DetectedVideoCodec);
            Assert.IsInstanceOfType(frame, typeof(RawJpegFrame));
        }

        [TestMethod]
        public void Parse_MjpegTransportPayloadWithSplitMarkers_GeneratesJpegFrame()
        {
            RawFrame frame = null;
            var parser = new RawJpegParser(() => DateTime.UtcNow)
            {
                FrameGenerated = generatedFrame => frame = generatedFrame
            };

            parser.Parse(new ArraySegment<byte>(new byte[] {0xFF}));
            parser.Parse(new ArraySegment<byte>(new byte[] {0xD8, 0x11, 0x22, 0xFF}));
            parser.Parse(new ArraySegment<byte>(new byte[] {0xD9}));

            Assert.IsInstanceOfType(frame, typeof(RawJpegFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(new byte[]
                {0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9}));
        }

        [TestMethod]
        public void Parse_H265FragmentationUnit_GeneratesIFrame()
        {
            RawH265Frame frame = null;
            RawH265Frame fuFrame = null;
            var parser = new H265VideoPayloadParser(new H265CodecInfo
            {
                VpsBytes = new byte[] {0x40, 0x01, 0x80},
                SpsBytes = new byte[] {0x42, 0x01, 0x80},
                PpsBytes = new byte[] {0x44, 0x01, 0x80}
            })
            {
                FrameGenerated = generatedFrame =>
                {
                    frame = (RawH265Frame)generatedFrame;
                    fuFrame = frame;
                }
            };

            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[]
                {0x62, 0x01, 0x93, 0x80}), false);
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[]
                {0x62, 0x01, 0x53, 0x00}), true);

            Assert.IsInstanceOfType(fuFrame, typeof(RawH265IFrame));

            Assert.IsInstanceOfType(frame, typeof(RawH265IFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(new byte[]
                {0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0x80, 0x00}));
        }

        [TestMethod]
        public void Parse_H265AnnexBWithMixedStartCodes_GeneratesIFrame()
        {
            RawFrame frame = null;
            var parser = new H265Parser(() => DateTime.UtcNow)
            {
                FrameGenerated = generatedFrame => frame = generatedFrame
            };

            parser.Parse(new ArraySegment<byte>(new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x80,
                0x00, 0x00, 0x01, 0x42, 0x01, 0x80,
                0x00, 0x00, 0x01, 0x44, 0x01, 0x80,
                0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0x80
            }), true);

            Assert.IsInstanceOfType(frame, typeof(RawH265IFrame));
        }

        private static byte[] CreateH264Parameters()
        {
            byte[] sps = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            byte[] pps = Convert.FromBase64String("AAAAAWjuPIA=");
            return sps.Concat(pps).ToArray();
        }

        private static byte[] CreateMjpegPayload(int fragmentOffset, byte[] data)
        {
            var payload = new byte[8 + data.Length];
            payload[0] = 0;
            payload[1] = (byte)(fragmentOffset >> 16);
            payload[2] = (byte)(fragmentOffset >> 8);
            payload[3] = (byte)fragmentOffset;
            payload[4] = 1;
            payload[5] = 1;
            payload[6] = 80;
            payload[7] = 60;
            Buffer.BlockCopy(data, 0, payload, 8, data.Length);
            return payload;
        }
    }
}

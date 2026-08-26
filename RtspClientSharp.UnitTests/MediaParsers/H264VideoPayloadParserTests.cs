using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.MediaParsers;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.UnitTests.MediaParsers
{
    [TestClass]
    public class H264VideoPayloadParserTests
    {
        [TestMethod]
        [DataRow(new byte[] {0x7C, 0xC5, 0x88, 0x80, 0x10, 0x00}, DisplayName = "FUA")]
        [DataRow(new byte[] {0x7D, 0xC5, 0x00, 0x00, 0x88, 0x80, 0x10, 0x00}, DisplayName = "FUB")]
        [DataRow(new byte[] {0x18, 0x00, 0x05, 0x65, 0x88, 0x80, 0x10, 0x00}, DisplayName = "STAPA")]
        [DataRow(new byte[] {0x19, 0x00, 0x00, 0x00, 0x05, 0x65, 0x88, 0x80, 0x10, 0x00}, DisplayName = "STAB")]
        [DataRow(new byte[] {0x1A, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x65, 0x88, 0x80, 0x10, 0x00},
            DisplayName = "MTAP16")]
        [DataRow(new byte[] {0x1B, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x65, 0x88, 0x80, 0x10, 0x00},
            DisplayName = "MTAP24")]
        public void Parse_DifferentAggregationUnits_ReturnsValidIFrame(byte[] testBytes)
        {
            H264CodecInfo testCodecInfo = CreateTestH264CodecInfo();

            RawH264Frame frame = null;
            var parser = new H264VideoPayloadParser(testCodecInfo);
            parser.FrameGenerated = rawFrame => frame = (RawH264Frame) rawFrame;
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(testBytes), true);

            Assert.IsNotNull(frame);
            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
        }

        [TestMethod]
        public void Constructor_EmptySpsPps_NoExceptionGenerated()
        {
            H264CodecInfo testCodecInfo = new H264CodecInfo {SpsPpsBytes = Array.Empty<byte>()};

            new H264VideoPayloadParser(testCodecInfo);
        }

        [TestMethod]
        public void Parse_SinglePacketFuThenFragmentedFu_DoesNotReusePreviousNalBytes()
        {
            H264CodecInfo testCodecInfo = CreateTestH264CodecInfo();
            RawH264Frame frame = null;

            var parser = new H264VideoPayloadParser(testCodecInfo)
            {
                FrameGenerated = rawFrame => frame = (RawH264Frame)rawFrame
            };

            // The first FU is complete in one packet. The second FU is split across
            // packets; its output must not contain the first FU's bytes.
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[]
                {0x7C, 0xC5, 0x88, 0x80, 0x10, 0x00}), true);
            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));

            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[]
                {0x7C, 0x81, 0x9A, 0x01}), false);
            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[]
                {0x7C, 0x41, 0x01, 0x64}), true);

            Assert.IsInstanceOfType(frame, typeof(RawH264PFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(new byte[]
                {0x0, 0x0, 0x0, 0x1, 0x61, 0x9A, 0x01, 0x01, 0x64}));
        }

        [TestMethod]
        public void Parse_TruncatedStap_DropsPacketWithoutThrowing()
        {
            var parser = new H264VideoPayloadParser(CreateTestH264CodecInfo());

            parser.Parse(TimeSpan.Zero, new ArraySegment<byte>(new byte[] {0x18, 0x00, 0x05, 0x65}), true);
        }

        private static H264CodecInfo CreateTestH264CodecInfo()
        {
            var testCodecInfo = new H264CodecInfo();

            var spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            var ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");

            testCodecInfo.SpsPpsBytes = spsBytes.Concat(ppsBytes).ToArray();
            return testCodecInfo;
        }
    }
}

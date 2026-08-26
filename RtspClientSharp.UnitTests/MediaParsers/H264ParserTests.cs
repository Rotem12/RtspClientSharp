using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.MediaParsers;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.UnitTests.MediaParsers
{
    [TestClass]
    public class H264ParserTests
    {
        [TestMethod]
        public void Parse_IFrameBytesThenPFrameBytes_GeneratesTwoFrames()
        {
            var spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            var ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            var iFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00};
            var pFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x61, 0x9a, 0x01, 0x01, 0x64};

            RawH264Frame frame = null;
            var parser = new H264Parser(() => DateTime.UtcNow) {FrameGenerated = rawFrame => frame = (RawH264Frame) rawFrame};
            parser.Parse(new ArraySegment<byte>(spsBytes), false);
            parser.Parse(new ArraySegment<byte>(ppsBytes), false);
            parser.Parse(new ArraySegment<byte>(iFrameBytes), true);

            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(iFrameBytes));

            parser.Parse(new ArraySegment<byte>(pFrameBytes), true);

            Assert.IsInstanceOfType(frame, typeof(RawH264PFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(pFrameBytes));
        }

        [TestMethod]
        public void ResetState_SpsPpsThenIFrameThenReset_FrameGenerated()
        {
            var spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            var ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            var iFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00};

            RawH264Frame frame = null;
            var parser = new H264Parser(() => DateTime.UtcNow) {FrameGenerated = rawFrame => frame = (RawH264Frame) rawFrame};
            parser.Parse(new ArraySegment<byte>(spsBytes),  false);
            parser.Parse(new ArraySegment<byte>(ppsBytes), false);

            parser.ResetState();
            parser.Parse(new ArraySegment<byte>(iFrameBytes), true);

            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
        }

        [TestMethod]
        public void ResetState_AfterIFrame_DropsPredictionFramesUntilNextIFrame()
        {
            var spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            var ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            var iFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00};
            var pFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x61, 0x9a, 0x01, 0x01, 0x64};

            int frameCount = 0;
            var parser = new H264Parser(() => DateTime.UtcNow)
            {
                FrameGenerated = rawFrame => ++frameCount
            };

            parser.Parse(new ArraySegment<byte>(spsBytes), false);
            parser.Parse(new ArraySegment<byte>(ppsBytes), false);
            parser.Parse(new ArraySegment<byte>(iFrameBytes), true);
            parser.Parse(new ArraySegment<byte>(pFrameBytes), true);

            Assert.AreEqual(2, frameCount);

            parser.ResetState();
            parser.Parse(new ArraySegment<byte>(pFrameBytes), true);

            Assert.AreEqual(2, frameCount);

            parser.Parse(new ArraySegment<byte>(iFrameBytes), true);
            Assert.AreEqual(3, frameCount);
        }

        [TestMethod]
        public void Parse_PFrameBeforeSpsPpsThenIFrame_GeneratesFirstIFrame()
        {
            var spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            var ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            var iFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00};
            var pFrameBytes = new byte[] {0x0, 0x0, 0x0, 0x1, 0x61, 0x9a, 0x01, 0x01, 0x64};

            RawH264Frame frame = null;
            var parser = new H264Parser(() => DateTime.UtcNow)
            {
                FrameGenerated = rawFrame => frame = (RawH264Frame)rawFrame
            };

            parser.Parse(new ArraySegment<byte>(pFrameBytes), true);
            Assert.IsNull(frame);

            parser.Parse(new ArraySegment<byte>(spsBytes), false);
            parser.Parse(new ArraySegment<byte>(ppsBytes), false);
            parser.Parse(new ArraySegment<byte>(iFrameBytes), true);

            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(iFrameBytes));
        }

        [TestMethod]
        public void Parse_ThreeByteAnnexBMarkers_GeneratesIFrame()
        {
            var spsBytes = ToThreeByteStartMarker(Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA"));
            var ppsBytes = ToThreeByteStartMarker(Convert.FromBase64String("AAAAAWjuPIA="));
            var iFrameBytes = new byte[] {0x0, 0x0, 0x1, 0x65, 0x88, 0x80, 0x10, 0x00};

            RawH264Frame frame = null;
            var parser = new H264Parser(() => DateTime.UtcNow)
            {
                FrameGenerated = rawFrame => frame = (RawH264Frame)rawFrame
            };

            parser.Parse(new ArraySegment<byte>(spsBytes.Concat(ppsBytes).Concat(iFrameBytes).ToArray()), true);

            Assert.IsInstanceOfType(frame, typeof(RawH264IFrame));
            Assert.IsTrue(frame.FrameSegment.SequenceEqual(iFrameBytes));
        }

        private static byte[] ToThreeByteStartMarker(byte[] bytes)
        {
            var result = new byte[bytes.Length - 1];
            result[0] = 0x0;
            result[1] = 0x0;
            result[2] = 0x1;
            Buffer.BlockCopy(bytes, 4, result, 3, bytes.Length - 4);
            return result;
        }
    }
}

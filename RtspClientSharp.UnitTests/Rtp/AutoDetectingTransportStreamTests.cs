using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp;
using RtspClientSharp.Rtp;

namespace RtspClientSharp.UnitTests.Rtp
{
    [TestClass]
    public class AutoDetectingTransportStreamTests
    {
        [TestMethod]
        public void Process_AutoModeWithMpegTs_SelectsMpegTsStream()
        {
            var selected = new CapturingStream();
            var autoStream = new AutoDetectingTransportStream(MediaTransportMode.Auto,
                () => throw new AssertFailedException("RTP stream should not be selected"),
                () => selected);

            byte[] payload = new byte[TsPacketSize * 2];
            payload[0] = 0x47;
            payload[TsPacketSize] = 0x47;

            autoStream.Process(new ArraySegment<byte>(payload));

            Assert.AreEqual(MediaTransportMode.MpegTs, autoStream.DetectedMode);
            Assert.AreEqual(1, selected.ProcessCount);
            Assert.AreEqual(payload.Length, selected.LastPayload.Count);
        }

        [TestMethod]
        public void Process_AutoModeWithRtp_SelectsRtpStream()
        {
            var selected = new CapturingStream();
            var autoStream = new AutoDetectingTransportStream(MediaTransportMode.Auto,
                () => selected,
                () => throw new AssertFailedException("MPEG-TS stream should not be selected"));

            byte[] payload = new byte[]
            {
                0x80, 0x60, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x02,
                0x65
            };

            autoStream.Process(new ArraySegment<byte>(payload));

            Assert.AreEqual(MediaTransportMode.Rtp, autoStream.DetectedMode);
            Assert.AreEqual(1, selected.ProcessCount);
            Assert.AreEqual(payload.Length, selected.LastPayload.Count);
        }

        [TestMethod]
        public void Process_AutoModeWithDamagedRtpHeader_SelectsRtpStream()
        {
            var selected = new CapturingStream();
            var autoStream = new AutoDetectingTransportStream(MediaTransportMode.Auto,
                () => selected,
                () => throw new AssertFailedException("MPEG-TS stream should not be selected"));

            byte[] payload = {0x80, 0x60};
            autoStream.Process(new ArraySegment<byte>(payload));

            Assert.AreEqual(MediaTransportMode.Rtp, autoStream.DetectedMode);
            Assert.AreEqual(1, selected.ProcessCount);
        }

        [TestMethod]
        public void Process_ExplicitModeSelectsConfiguredStreamBeforeSniffing()
        {
            var selected = new CapturingStream();
            var autoStream = new AutoDetectingTransportStream(MediaTransportMode.MpegTs,
                () => throw new AssertFailedException("RTP stream should not be selected"),
                () => selected);

            autoStream.Process(new ArraySegment<byte>(new byte[] {0x01}));

            Assert.AreEqual(MediaTransportMode.MpegTs, autoStream.DetectedMode);
            Assert.AreEqual(1, selected.ProcessCount);
        }

        private const int TsPacketSize = 188;

        private sealed class CapturingStream : ITransportStream
        {
            public int ProcessCount { get; private set; }
            public ArraySegment<byte> LastPayload { get; private set; }

            public void Process(ArraySegment<byte> payloadSegment)
            {
                ProcessCount++;
                LastPayload = payloadSegment;
            }
        }
    }
}

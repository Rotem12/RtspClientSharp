using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.Recording;

namespace RtspClientSharp.UnitTests.Recording
{
    [TestClass]
    public class CompressedVideoRollingBufferTests
    {
        [TestMethod]
        public void GetFramesForRecording_StartsAtKeyFrameBeforeRequestedTime()
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var buffer = new CompressedVideoRollingBuffer(TimeSpan.FromSeconds(10));

            buffer.Add(Frame(timestamp, true, 1));
            buffer.Add(Frame(timestamp.AddSeconds(1), false, 2));
            buffer.Add(Frame(timestamp.AddSeconds(2), false, 3));

            var frames = buffer.GetFramesForRecording(timestamp.AddSeconds(1.5));

            Assert.AreEqual(3, frames.Count);
            CollectionAssert.AreEqual(new byte[] { 1 }, frames[0].FrameBytes);
        }

        [TestMethod]
        public void GetFramesForRecording_StartsAtFirstKeyFrameAfterRequestedTime_WhenEarlierKeyFrameIsMissing()
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var buffer = new CompressedVideoRollingBuffer(TimeSpan.FromSeconds(10));

            buffer.Add(Frame(timestamp, false, 1));
            buffer.Add(Frame(timestamp.AddSeconds(1), true, 2));
            buffer.Add(Frame(timestamp.AddSeconds(2), false, 3));

            var frames = buffer.GetFramesForRecording(timestamp);

            Assert.AreEqual(2, frames.Count);
            CollectionAssert.AreEqual(new byte[] { 2 }, frames[0].FrameBytes);
        }

        [TestMethod]
        public void Add_PrunesOldFramesButKeepsPreviousKeyFrameForDecodableWindow()
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var buffer = new CompressedVideoRollingBuffer(TimeSpan.FromSeconds(2));

            buffer.Add(Frame(timestamp, true, 1));
            buffer.Add(Frame(timestamp.AddSeconds(1), false, 2));
            buffer.Add(Frame(timestamp.AddSeconds(2), false, 3));
            buffer.Add(Frame(timestamp.AddSeconds(3), false, 4));

            var frames = buffer.GetFramesForRecording(timestamp.AddSeconds(1));

            Assert.AreEqual(4, frames.Count);
            CollectionAssert.AreEqual(new byte[] { 1 }, frames[0].FrameBytes);
        }

        [TestMethod]
        public void TryGetLatestCodecParameters_ReturnsMostRecentParameterBytes()
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var buffer = new CompressedVideoRollingBuffer(TimeSpan.FromSeconds(10));

            buffer.Add(Frame(timestamp, true, 1, new byte[] { 7 }));
            buffer.Add(Frame(timestamp.AddSeconds(1), false, 2));
            buffer.Add(Frame(timestamp.AddSeconds(2), true, 3, new byte[] { 8, 9 }));

            bool found = buffer.TryGetLatestCodecParameters(EncodedVideoCodec.H264, out byte[] parameters);

            Assert.IsTrue(found);
            CollectionAssert.AreEqual(new byte[] { 8, 9 }, parameters);
        }

        [TestMethod]
        public void GetFramesForRecording_ReturnsEmpty_WhenNoKeyFrameExists()
        {
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var buffer = new CompressedVideoRollingBuffer(TimeSpan.FromSeconds(10));

            buffer.Add(Frame(timestamp, false, 1));
            buffer.Add(Frame(timestamp.AddSeconds(1), false, 2));

            var frames = buffer.GetFramesForRecording(timestamp);

            Assert.AreEqual(0, frames.Count);
        }

        private static EncodedVideoFrame Frame(DateTime timestamp, bool keyFrame, byte value,
            byte[] parameters = null)
        {
            return new EncodedVideoFrame(timestamp, EncodedVideoCodec.H264, keyFrame,
                new[] { value }, parameters);
        }
    }
}

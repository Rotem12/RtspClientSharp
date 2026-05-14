using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.RawFrames.Audio;
using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Recording;

namespace RtspClientSharp.UnitTests.Recording
{
    [TestClass]
    public class EncodedVideoFrameFactoryTests
    {
        [TestMethod]
        public void TryCreate_CopiesH264IFrameAndParameters()
        {
            byte[] frameBytes = { 0, 0, 0, 1, 5 };
            byte[] parametersBytes = { 0, 0, 0, 1, 7, 0, 0, 0, 1, 8 };
            var rawFrame = new RawH264IFrame(DateTime.UtcNow, new ArraySegment<byte>(frameBytes, 1, 4),
                new ArraySegment<byte>(parametersBytes, 2, 3));

            bool created = EncodedVideoFrameFactory.TryCreate(rawFrame, out EncodedVideoFrame encodedFrame);

            Assert.IsTrue(created);
            Assert.AreEqual(EncodedVideoCodec.H264, encodedFrame.Codec);
            Assert.IsTrue(encodedFrame.IsKeyFrame);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 5 }, encodedFrame.FrameBytes);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 7 }, encodedFrame.CodecParametersBytes);

            frameBytes[1] = 99;
            parametersBytes[2] = 99;
            CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 5 }, encodedFrame.FrameBytes);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 7 }, encodedFrame.CodecParametersBytes);
        }

        [TestMethod]
        public void TryCreate_ReturnsFalseForAudioFrame()
        {
            var rawFrame = new RawAACFrame(DateTime.UtcNow, new ArraySegment<byte>(new byte[] { 1, 2 }),
                new ArraySegment<byte>(new byte[] { 3, 4 }));

            bool created = EncodedVideoFrameFactory.TryCreate(rawFrame, out EncodedVideoFrame encodedFrame);

            Assert.IsFalse(created);
            Assert.IsNull(encodedFrame);
        }
    }
}

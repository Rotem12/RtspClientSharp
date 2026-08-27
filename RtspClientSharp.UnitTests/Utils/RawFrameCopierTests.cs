using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Utils;

namespace RtspClientSharp.UnitTests.Utils
{
    [TestClass]
    public class RawFrameCopierTests
    {
        [TestMethod]
        public void Copy_H264IFrame_DeepCopiesFrameAndParameters()
        {
            byte[] frameBytes = { 0, 0, 0, 1, 0x65, 0x01, 0x02 };
            byte[] spsPpsBytes = { 0, 0, 0, 1, 0x67, 0x03, 0, 0, 0, 1, 0x68, 0x04 };
            var original = new RawH264IFrame(DateTime.UtcNow, new ArraySegment<byte>(frameBytes),
                new ArraySegment<byte>(spsPpsBytes));

            using (RawFrameCopier.RawFrameCopy copied = RawFrameCopier.Copy(original))
            {
                var copiedIFrame = (RawH264IFrame)copied.Frame;

                Assert.IsFalse(ReferenceEquals(frameBytes, copiedIFrame.FrameSegment.Array));
                Assert.IsFalse(ReferenceEquals(spsPpsBytes, copiedIFrame.SpsPpsSegment.Array));
                Assert.IsTrue(copiedIFrame.FrameSegment.SequenceEqual(frameBytes));
                Assert.IsTrue(copiedIFrame.SpsPpsSegment.SequenceEqual(spsPpsBytes));
                Assert.IsTrue(copiedIFrame.HasDecoderInputPadding);
                int paddingStart = copiedIFrame.FrameSegment.Offset + copiedIFrame.FrameSegment.Count;
                int paddingEnd = paddingStart + 64;
                for (int i = paddingStart; i < paddingEnd; i++)
                    Assert.AreEqual(0, copiedIFrame.FrameSegment.Array[i]);

                frameBytes[4] = 0;
                spsPpsBytes[4] = 0;

                Assert.AreEqual(0x65, copiedIFrame.FrameSegment.Array[copiedIFrame.FrameSegment.Offset + 4]);
                Assert.AreEqual(0x67, copiedIFrame.SpsPpsSegment.Array[copiedIFrame.SpsPpsSegment.Offset + 4]);
            }
        }
    }
}

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Recording;

namespace RtspClientSharp.UnitTests.Recording
{
    [TestClass]
    public class CompressedVideoRecorderTests
    {
        [TestMethod]
        public void Factory_AutoUsesMpegTsForNormalPathAndAnnexBForElementaryPath()
        {
            ICompressedVideoRecorder containerRecorder =
                CompressedVideoRecorderFactory.Create("capture.wma");
            ICompressedVideoRecorder elementaryRecorder =
                CompressedVideoRecorderFactory.Create("capture.h264");

            Assert.IsInstanceOfType(containerRecorder, typeof(MpegTsVideoFileRecorder));
            Assert.IsInstanceOfType(elementaryRecorder, typeof(AnnexBVideoFileRecorder));
        }

        [TestMethod]
        public void MpegTsRecorder_WritesRawH264FramesWithoutFrameSizedCopyContract()
        {
            string requestedPath = Path.Combine(Path.GetTempPath(),
                "rtsp-client-sharp-record-" + Guid.NewGuid().ToString("N") + ".wma");
            string outputPath = Path.ChangeExtension(requestedPath, ".ts");

            try
            {
                DateTime timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var recorder = new MpegTsVideoFileRecorder();
                recorder.Start(requestedPath, null, new CompressedVideoRecorderOptions
                {
                    Format = CompressedVideoRecordingFormat.MpegTs,
                    FrameDuration = TimeSpan.FromMilliseconds(40)
                });

                var parameters = new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1E };
                var idr = new RawH264IFrame(timestamp,
                    new ArraySegment<byte>(new byte[] { 0, 0, 0, 1, 0x65, 1 }),
                    new ArraySegment<byte>(parameters));
                var pFrame = new RawH264PFrame(timestamp.AddMilliseconds(40),
                    new ArraySegment<byte>(new byte[] { 0, 0, 0, 1, 0x41, 2 }));

                recorder.WriteRawFrame(idr);
                recorder.WriteRawFrame(pFrame);
                recorder.Stop();

                Assert.AreEqual(outputPath, recorder.OutputFilePath);
                Assert.AreEqual(2, recorder.FramesWritten);
                Assert.IsTrue(recorder.PacketsWritten >= 3);

                byte[] output = File.ReadAllBytes(outputPath);
                Assert.IsTrue(output.Length >= 188 * 3);
                Assert.AreEqual(0, output.Length % 188);
                Assert.AreEqual(0x47, output[0]);
                Assert.AreEqual(0x47, output[188]);
                Assert.AreEqual(0x47, output[376]);
            }
            finally
            {
                DeleteIfPresent(requestedPath);
                DeleteIfPresent(outputPath);
            }
        }

        [TestMethod]
        public void AnnexBRecorder_WritesRawFrameSegmentsAndWaitsForKeyFrame()
        {
            string requestedPath = Path.Combine(Path.GetTempPath(),
                "rtsp-client-sharp-record-" + Guid.NewGuid().ToString("N") + ".wma");
            string outputPath = Path.ChangeExtension(requestedPath, ".h264");

            try
            {
                DateTime timestamp = DateTime.UtcNow;
                var recorder = new AnnexBVideoFileRecorder();
                recorder.Start(requestedPath);
                recorder.WriteRawFrame(new RawH264PFrame(timestamp,
                    new ArraySegment<byte>(new byte[] { 0, 0, 0, 1, 0x41 })));
                recorder.WriteRawFrame(new RawH264IFrame(timestamp,
                    new ArraySegment<byte>(new byte[] { 0, 0, 0, 1, 0x65 }),
                    new ArraySegment<byte>(new byte[] { 0, 0, 0, 1, 0x67 })));
                recorder.Stop();

                CollectionAssert.AreEqual(new byte[]
                {
                    0, 0, 0, 1, 0x67, 0, 0, 0, 1, 0x65
                }, File.ReadAllBytes(outputPath));
                Assert.AreEqual(1, recorder.FramesWritten);
            }
            finally
            {
                DeleteIfPresent(requestedPath);
                DeleteIfPresent(outputPath);
            }
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

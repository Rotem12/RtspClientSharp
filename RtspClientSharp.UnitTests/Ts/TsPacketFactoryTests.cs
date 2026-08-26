using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.Ts;

namespace RtspClientSharp.UnitTests.Ts
{
    [TestClass]
    public class TsPacketFactoryTests
    {
        [TestMethod]
        public void PushData_ArraySegment_PreservesPayloadAndRelativeSourceIndex()
        {
            var factory = new TsPacketFactory();
            TsPacket? capturedPacket = null;
            factory.TsPacketReady += (sender, args) => capturedPacket = args.TsPacket;

            byte[] buffer = new byte[TsPacketFactory.TsPacketFixedSize + 5];
            int offset = 5;
            buffer[offset] = 0x47;
            buffer[offset + 1] = 0x00;
            buffer[offset + 2] = 0x01;
            buffer[offset + 3] = 0x10;
            for (int i = 0; i < 184; i++)
                buffer[offset + 4 + i] = (byte)i;

            factory.PushData(new ArraySegment<byte>(buffer, offset,
                TsPacketFactory.TsPacketFixedSize));

            Assert.IsTrue(capturedPacket.HasValue);
            Assert.AreEqual(0, capturedPacket.Value.SourceBufferIndex);
            Assert.AreEqual(184, capturedPacket.Value.PayloadLen);
            Assert.AreEqual(0, capturedPacket.Value.Payload[0]);
            Assert.AreEqual(183, capturedPacket.Value.Payload[183]);
        }
    }
}

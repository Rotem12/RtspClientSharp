using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;
using DirectRtpClient = RtspClientSharp.RtpClient.RtpClient;

namespace RtspClientSharp.UnitTests.RtpClient
{
    [TestClass]
    public class RtpClientTests
    {
        [TestMethod]
        public async Task ReceiveLoop_AutoDetectsRtpH265AndRaisesFrame()
        {
            int port = GetAvailablePort();
            var connectionParameters = new ConnectionParameters(
                new Uri($"rtp://127.0.0.1:{port}"));
            var frameReady = new TaskCompletionSource<RawFrame>();
            using (var client = new DirectRtpClient(connectionParameters)
            {
                Timeout = 2000,
                UseInlineFrameDelivery = true
            })
            using (var sender = new UdpClient())
            using (var cancellation = new CancellationTokenSource())
            {
                client.FrameReceived += (senderObject, frame) => frameReady.TrySetResult(frame);
                await client.ConnectAsync(CancellationToken.None);
                Task receiveTask = client.ReceiveLoopAsync(cancellation.Token);

                IPEndPoint endpoint = new IPEndPoint(IPAddress.Loopback, port);
                byte[][] packets =
                {
                    new byte[] {0x80, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x12, 0x34, 0x56, 0x78,
                        0x40, 0x01, 0x80},
                    new byte[] {0x80, 0x60, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x12, 0x34, 0x56, 0x78,
                        0x42, 0x01, 0x80},
                    new byte[] {0x80, 0x60, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x12, 0x34, 0x56, 0x78,
                        0x44, 0x01, 0x80},
                    new byte[] {0x80, 0xE0, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x12, 0x34, 0x56, 0x78,
                        0x26, 0x01, 0x80}
                };

                foreach (byte[] packet in packets)
                    await sender.SendAsync(packet, packet.Length, endpoint);

                Task completedTask = await Task.WhenAny(frameReady.Task, Task.Delay(3000));
                Assert.AreSame(frameReady.Task, completedTask, "The direct RTP client did not raise a frame.");

                cancellation.Cancel();
                try
                {
                    await receiveTask;
                }
                catch (OperationCanceledException)
                {
                }

                Assert.IsInstanceOfType(frameReady.Task.Result, typeof(RawH265IFrame));
                Assert.AreEqual(MediaTransportMode.Rtp, client.DetectedTransportMode);
                Assert.AreEqual(CodecInfoType.H265, client.DetectedVideoCodec);
            }
        }

        [TestMethod]
        public async Task ReceiveLoop_TransportAndCodecMatrix_ProcessesEverySupportedMode()
        {
            var cases = new[]
            {
                new ClientCase("Auto/RTP/H264", MediaTransportMode.Auto, CodecInfoType.Auto,
                    CreateRtpH264Packets(), typeof(RawH264IFrame)),
                new ClientCase("Auto/RTP/H265", MediaTransportMode.Auto, CodecInfoType.Auto,
                    CreateRtpH265Packets(), typeof(RawH265IFrame)),
                new ClientCase("Auto/RTP/MJPEG", MediaTransportMode.Auto, CodecInfoType.Auto,
                    CreateRtpMjpegPackets(), typeof(RawJpegFrame)),

                new ClientCase("RTP/Auto/H264", MediaTransportMode.Rtp, CodecInfoType.Auto,
                    CreateRtpH264Packets(), typeof(RawH264IFrame)),
                new ClientCase("RTP/Auto/H265", MediaTransportMode.Rtp, CodecInfoType.Auto,
                    CreateRtpH265Packets(), typeof(RawH265IFrame)),
                new ClientCase("RTP/Auto/MJPEG", MediaTransportMode.Rtp, CodecInfoType.Auto,
                    CreateRtpMjpegPackets(), typeof(RawJpegFrame)),
                new ClientCase("RTP/H264", MediaTransportMode.Rtp, CodecInfoType.H264,
                    CreateRtpH264Packets(), typeof(RawH264IFrame)),
                new ClientCase("RTP/H265", MediaTransportMode.Rtp, CodecInfoType.H265,
                    CreateRtpH265Packets(), typeof(RawH265IFrame)),
                new ClientCase("RTP/MJPEG", MediaTransportMode.Rtp, CodecInfoType.MJPEG,
                    CreateRtpMjpegPackets(), typeof(RawJpegFrame)),

                new ClientCase("Auto/TS/H264", MediaTransportMode.Auto, CodecInfoType.Auto,
                    new[] {CreateTsDatagram(0x1B, 0xE0, CreateH264AccessUnit())}, typeof(RawH264IFrame)),
                new ClientCase("Auto/TS/H265", MediaTransportMode.Auto, CodecInfoType.Auto,
                    new[] {CreateTsDatagram(0x24, 0xE0, CreateH265AccessUnit())}, typeof(RawH265IFrame)),
                new ClientCase("Auto/TS/MJPEG", MediaTransportMode.Auto, CodecInfoType.Auto,
                    new[] {CreateTsDatagram(0x06, 0xBD, new byte[]
                    {0x00, 0x11, 0xFF, 0xD8, 0x22, 0x33, 0xFF, 0xD9, 0x44})}, typeof(RawJpegFrame)),
                new ClientCase("TS/Auto/H264", MediaTransportMode.MpegTs, CodecInfoType.Auto,
                    new[] {CreateTsDatagram(0x1B, 0xE0, CreateH264AccessUnit())}, typeof(RawH264IFrame)),
                new ClientCase("TS/Auto/H265", MediaTransportMode.MpegTs, CodecInfoType.Auto,
                    new[] {CreateTsDatagram(0x24, 0xE0, CreateH265AccessUnit())}, typeof(RawH265IFrame)),
                new ClientCase("TS/Auto/MJPEG", MediaTransportMode.MpegTs, CodecInfoType.Auto,
                    new[] {CreateTsDatagram(0x06, 0xBD, new byte[]
                    {0x00, 0x11, 0xFF, 0xD8, 0x22, 0x33, 0xFF, 0xD9, 0x44})}, typeof(RawJpegFrame))
            };

            foreach (ClientCase testCase in cases)
            {
                RawFrame frame = await ReceiveCase(testCase);
                Assert.IsInstanceOfType(frame, testCase.ExpectedFrameType, testCase.Name);
            }
        }

        private static async Task<RawFrame> ReceiveCase(ClientCase testCase)
        {
            int port = GetAvailablePort();
            var connectionParameters = new ConnectionParameters(
                new Uri($"rtp://127.0.0.1:{port}"))
            {
                TransportMode = testCase.TransportMode
            };
            var frameReady = new TaskCompletionSource<RawFrame>();
            using (var client = new DirectRtpClient(connectionParameters)
            {
                Timeout = 2000,
                VideoCodec = testCase.VideoCodec,
                UseInlineFrameDelivery = true
            })
            using (var sender = new UdpClient())
            using (var cancellation = new CancellationTokenSource())
            {
                client.FrameReceived += (senderObject, frame) => frameReady.TrySetResult(frame);
                await client.ConnectAsync(CancellationToken.None);
                Task receiveTask = client.ReceiveLoopAsync(cancellation.Token);

                IPEndPoint endpoint = new IPEndPoint(IPAddress.Loopback, port);
                foreach (byte[] datagram in testCase.Datagrams)
                    await sender.SendAsync(datagram, datagram.Length, endpoint);

                Task completedTask = await Task.WhenAny(frameReady.Task, Task.Delay(3000));
                Assert.AreSame(frameReady.Task, completedTask,
                    $"{testCase.Name}: the client did not raise a frame.");

                cancellation.Cancel();
                try
                {
                    await receiveTask;
                }
                catch (OperationCanceledException)
                {
                }

                Assert.AreEqual(testCase.ExpectedTransportMode, client.DetectedTransportMode,
                    $"{testCase.Name}: detected transport differs.");
                Assert.AreEqual(testCase.ExpectedCodec, client.DetectedVideoCodec,
                    $"{testCase.Name}: detected codec differs.");
                return frameReady.Task.Result;
            }
        }

        private static byte[][] CreateRtpH264Packets()
        {
            byte[] spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            byte[] ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            return new[]
            {
                CreateRtpPacket(1, 1, false, spsBytes),
                CreateRtpPacket(2, 1, false, ppsBytes),
                CreateRtpPacket(3, 1, true, new byte[] {0x65, 0x88, 0x80, 0x10, 0x00})
            };
        }

        private static byte[][] CreateRtpH265Packets()
        {
            return new[]
            {
                CreateRtpPacket(1, 1, false, new byte[] {0x40, 0x01, 0x80}),
                CreateRtpPacket(2, 1, false, new byte[] {0x42, 0x01, 0x80}),
                CreateRtpPacket(3, 1, false, new byte[] {0x44, 0x01, 0x80}),
                CreateRtpPacket(4, 1, true, new byte[] {0x26, 0x01, 0x80})
            };
        }

        private static byte[][] CreateRtpMjpegPackets()
        {
            return new[]
            {
                CreateRtpPacket(1, 1, true, CreateMjpegPayload(0,
                    new byte[] {0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9})),
                CreateRtpPacket(2, 2, true, CreateMjpegPayload(0,
                    new byte[] {0xFF, 0xD8, 0x33, 0x44, 0xFF, 0xD9}))
            };
        }

        private static byte[] CreateRtpPacket(ushort sequenceNumber, uint timestamp, bool markerBit,
            byte[] payload)
        {
            var packet = new byte[12 + payload.Length];
            packet[0] = 0x80;
            packet[1] = (byte)((markerBit ? 0x80 : 0) | 96);
            packet[2] = (byte)(sequenceNumber >> 8);
            packet[3] = (byte)sequenceNumber;
            packet[4] = (byte)(timestamp >> 24);
            packet[5] = (byte)(timestamp >> 16);
            packet[6] = (byte)(timestamp >> 8);
            packet[7] = (byte)timestamp;
            packet[8] = 0x12;
            packet[9] = 0x34;
            packet[10] = 0x56;
            packet[11] = 0x78;
            Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
            return packet;
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

        private static byte[] CreateH264AccessUnit()
        {
            byte[] spsBytes = Convert.FromBase64String("AAAAAWdNQCmaZgUB7YC1AQEBBenA");
            byte[] ppsBytes = Convert.FromBase64String("AAAAAWjuPIA=");
            byte[] iFrameBytes = {0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x80, 0x10, 0x00};
            return spsBytes.Concat(ppsBytes).Concat(iFrameBytes).ToArray();
        }

        private static byte[] CreateH265AccessUnit()
        {
            return new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x80,
                0x00, 0x00, 0x01, 0x42, 0x01, 0x80,
                0x00, 0x00, 0x01, 0x44, 0x01, 0x80,
                0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0x80
            };
        }

        private static byte[] CreateTsDatagram(byte streamType, byte streamId, byte[] elementaryData)
        {
            byte[][] packets = BuildProgramPackets(streamType, 0x0101, streamId, elementaryData);
            return packets.SelectMany(packet => packet).ToArray();
        }

        private static byte[][] BuildProgramPackets(byte streamType, ushort elementaryPid, byte streamId,
            byte[] elementaryData)
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

            var packets = new List<byte[]> {pat, pmt};
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
            pes[5] = (byte)pesLength;
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
            packet[2] = (byte)pid;

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

        private sealed class ClientCase
        {
            public ClientCase(string name, MediaTransportMode transportMode, CodecInfoType videoCodec,
                byte[][] datagrams, Type expectedFrameType)
            {
                Name = name;
                TransportMode = transportMode;
                VideoCodec = videoCodec;
                Datagrams = datagrams;
                ExpectedFrameType = expectedFrameType;
                ExpectedTransportMode = transportMode == MediaTransportMode.Auto
                    ? (datagrams[0][0] == 0x47 ? MediaTransportMode.MpegTs : MediaTransportMode.Rtp)
                    : transportMode;
                ExpectedCodec = expectedFrameType == typeof(RawH264IFrame)
                    ? CodecInfoType.H264
                    : expectedFrameType == typeof(RawH265IFrame)
                        ? CodecInfoType.H265
                        : CodecInfoType.MJPEG;
            }

            public string Name { get; }
            public MediaTransportMode TransportMode { get; }
            public CodecInfoType VideoCodec { get; }
            public byte[][] Datagrams { get; }
            public Type ExpectedFrameType { get; }
            public MediaTransportMode ExpectedTransportMode { get; }
            public CodecInfoType ExpectedCodec { get; }
        }

        private static int GetAvailablePort()
        {
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
        }
    }
}

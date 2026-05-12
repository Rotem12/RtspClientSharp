using System;
using System.Collections.Generic;
using RtspClientSharp.MediaParsers;

namespace RtspClientSharp.Ts
{
    class TsStream : ITransportStream
    {
        private readonly IMediaPayloadParser _mediaPayloadParser;
//        private const int TsPacketSize = 188;

        private TimeSpan _samplesSum = TimeSpan.Zero;
        private TimeSpan _previousTimestamp;

        private bool _isFirstPacket = true;

        public int PacketsReceivedSinceLastReset { get; private set; }
        public int PacketsLostSinceLastReset { get; private set; }

        private TsPacketFactory _tsPacketFactory;
        private Pes _currentVideoPes;

        private ushort VideoPid = 0;
        private ushort PmtPid = 0;


        public TsStream(IMediaPayloadParser mediaPayloadParser)
        {
            _mediaPayloadParser = mediaPayloadParser ?? throw new ArgumentNullException(nameof(mediaPayloadParser));
           _tsPacketFactory = new TsPacketFactory();
           _tsPacketFactory.TsPacketReady += _tsPacketFactory_TsPacketReady;
        }

        private void _tsPacketFactory_TsPacketReady(object sender, TsPacketReadyEventArgs args)
        {
            TsPacket packet = args.TsPacket;

            //if (!packet.ContainsPayload) return;

            //switch (packet.Pid)
            //{
            //    case (ushort)PidType.PatPid:
            //        for (int i = 8; i < packet.PayloadLen - 4; i += 4)
            //        {
            //            int programNumber = (packet.Payload[i] << 8) | packet.Payload[i + 1];
            //            if (programNumber != 0)
            //            {
            //                PmtPid = (ushort)(((packet.Payload[i + 2] & 0x1F) << 8) | packet.Payload[i + 3]);
            //                return;
            //            }
            //        }
            //        return;
            //    case (ushort)PidType.SdtBatPid:
            //        break;
            //    case (ushort)PidType.EitPid:
            //        break;
            //    case 2048:
            //        break;
            //    default:
            //        var list = new List<string>();

            //        int programInfoLength = ((packet.Payload[4] & 0x0F) << 8) | packet.Payload[5];
            //        int startIndex = 12 + programInfoLength;

            //        for (int i = startIndex; i < packet.PayloadLen; i += 5)
            //        {
            //            if (i + 4 >= packet.PayloadLen) break;

            //            byte streamType = packet.Payload[i];
            //            bool found = false;
            //            if (streamType == 0x1B) // h2646
            //            {
            //                list.Add("h264");
            //                found = true;
            //            }
            //            else if (streamType == 0x24) // hevc
            //            {
            //                list.Add("h265");
            //                found = true;
            //            }
            //            else if (streamType == 0x10) // hevc
            //            {
            //                list.Add("mpeg4");
            //                found = true;
            //            }
            //            else if (streamType == 0x02) // hevc
            //            {
            //                list.Add("mpeg2");
            //                found = true;
            //            }

            //            if (found)
            //            {
            //                VideoPid = (ushort)(((packet.Payload[i + 1] & 0x1F) << 8) | packet.Payload[i + 2]);
            //                return;
            //            }

            //        }
            //        return;
            //}

      //           if (packet.Pid != VideoPid) return; 
            if (packet.Pid != VideoPid && VideoPid != 0) return;

            if (packet.PayloadUnitStartIndicator)
            {

                //         if(packet.PesHeader.Pts > -1)
                //          {

                //          }
                if (_currentVideoPes != null)
                {
                    if (_currentVideoPes.Decode())
                    {
                        int startOfData = 6;
                        bool marker;


                        if (_currentVideoPes.OptionalPesHeader?.MarkerBits == 2) //optional PES header exists - minimum length is 3
                        {
                            startOfData += (ushort)(3 + _currentVideoPes.OptionalPesHeader.PesHeaderLength);
                            marker = true;
                        }
                        else
                        {
                            marker = false;
                        }

                        var dataBufSize = _currentVideoPes.Data.Length - startOfData;

                        var frame = new ArraySegment<byte>(_currentVideoPes.Data, startOfData, dataBufSize);
                        TimeSpan time = _currentVideoPes.DTS;

                        if (_isFirstPacket)
                        {
                            _isFirstPacket = false;
                        }
                        else
                        {
                            _samplesSum += time - _previousTimestamp;

                        }
                        _previousTimestamp = time;

                        _mediaPayloadParser.Parse(_samplesSum, frame, markerBit: marker);
                    }
                }
                Pes pes = new Pes(packet);
                if (!pes.Dropped)
                {
                    _currentVideoPes = pes;

                    if (packet.AdaptationFieldExists && packet.AdaptationField.PcrFlag)
                    {
                        VideoPid = packet.Pid;
                    }

                }
           //     else
           //     {
           //         Console.WriteLine("dropped");
           //     }
            }
            else
            {
                _currentVideoPes?.Add(packet);
            }


            //if (tsPacket.Pid != TsService?.VideoPid) return;

            //if (tsPacket.PayloadUnitStartIndicator)
            //{
            //    if (tsPacket.PesHeader.Pts > -1)
            //        LastPts = tsPacket.PesHeader.Pts;

            //    if (_currentVideoPes != null)
            //    {
            //        _currentVideoPes.Decode();
            //        TsService.AddData(_currentVideoPes, tsPacket.PesHeader, StreamType);
            //    }
            //    _currentVideoPes = new Pes(tsPacket);

            //}
            //else
            //{
            //    _currentVideoPes?.Add(tsPacket);
            //}
        }

        public void Process(ArraySegment<byte> payloadSegment)
        {
            _tsPacketFactory.PushData(payloadSegment.Array, payloadSegment.Count);

            //if (payloadSegment.Count % TsPacketSize != 0)
            //    return;

            //int packetCount = payloadSegment.Count / TsPacketSize;

            //for (int i = 0; i < packetCount; i++)
            //{
            //    var tsPacket = new ArraySegment<byte>(payloadSegment.Array, payloadSegment.Offset + i * TsPacketSize, TsPacketSize);

            //    // validate sync byte
            //    if(tsPacket.Array[tsPacket.Offset] != 0x47)
            //    {
            //        ++PacketsLostSinceLastReset;
            //        continue;
            //    }

            //    ProcessTsPacket(tsPacket, i == 0, i == packetCount-1, i);
           //     ++PacketsReceivedSinceLastReset;
            //}
        }

        private void ProcessTsPacket(ArraySegment<byte> tsPacket, bool first, bool marker, int i)
        {
            byte syncByte = tsPacket.Array[tsPacket.Offset];
            byte flags = tsPacket.Array[tsPacket.Offset + 1];

            ushort pid = (ushort)(((tsPacket.Array[tsPacket.Offset + 1] & 0x1F) << 8) | tsPacket.Array[tsPacket.Offset + 2]);
            byte adaptationFieldControl = (byte)((tsPacket.Array[tsPacket.Offset + 3] & 0x30) >> 4);

            bool hasPayload = (adaptationFieldControl & 0x01) != 0;
            bool hasAdaptationField = (adaptationFieldControl & 0x02) != 0;

            if (!hasPayload && !hasAdaptationField) return;

            TimeSpan timestamp;
            int adaptationFieldLength = 0;
            int payloadOffset = tsPacket.Offset + 4;

            if(hasAdaptationField)
            {
                adaptationFieldLength = tsPacket.Array[payloadOffset];
                payloadOffset += 1 + adaptationFieldLength;
            }

            if (adaptationFieldControl == 0x03)
            {
                adaptationFieldLength = tsPacket.Array[tsPacket.Offset + 4];
                if(adaptationFieldLength > 0)
                {
                    const int baseIndex = 5;

                    long pcrBase = (long)(tsPacket.Array[tsPacket.Offset + baseIndex] << 25 | 
                                          tsPacket.Array[tsPacket.Offset + baseIndex + 1] << 17 | 
                                          tsPacket.Array[tsPacket.Offset + baseIndex + 2] << 9 |
                                          tsPacket.Array[tsPacket.Offset + baseIndex + 3] << 1 | 
                                         (tsPacket.Array[tsPacket.Offset + baseIndex + 4] & 0x80) >> 7);

                    int pcrExtension = (tsPacket.Array[tsPacket.Offset + baseIndex + 4] & 0x01) << 8 | tsPacket.Array[tsPacket.Offset + baseIndex + 5];

                    timestamp = PcrToTimeSpan(pcrBase, pcrExtension);
                }
                else
                {
                    timestamp = TimeSpan.Zero;
                }
            }
            else
            {
                timestamp = TimeSpan.Zero;
            }

            if (timestamp != TimeSpan.Zero)
            {
                _samplesSum += timestamp - _previousTimestamp;
                _previousTimestamp = timestamp;
            }
            _isFirstPacket = false;

            if (hasPayload)
            {
                int payloadLength = tsPacket.Offset + 188 - payloadOffset;

                var payloadSegment = new ArraySegment<byte>(tsPacket.Array, payloadOffset, payloadLength);

                //     index++;
                //      bool marker = index == 6;
                //     if(marker) index = 0;

                _mediaPayloadParser.Parse(TimeSpan.MinValue, payloadSegment, markerBit: marker);
            }
        }

    //    int index = 0;

        private TimeSpan PcrToTimeSpan(long pcrBase, int pcrExtension)
        {
            double pcrInSeconds = pcrBase / 90000.0;

            double extensionInSeconds = pcrExtension / 90000.0 / 1024;

            return TimeSpan.FromSeconds(pcrInSeconds + extensionInSeconds);
        }

        public void ResetState()
        {
            PacketsLostSinceLastReset = 0;
            PacketsReceivedSinceLastReset = 0;
        }
    }
}
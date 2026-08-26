/* Copyright 2017-2023 Cinegy GmbH.

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

using System;
using System.Collections.Generic;

namespace RtspClientSharp.Ts
{
    public class Pes
    {
        public bool Dropped { get; set; }
        public const uint DefaultPacketStartCodePrefix = 0x000001;

        public uint PacketStartCodePrefix { get; set; }
        public byte StreamId { get; set; }
        public ushort PesPacketLength { get; set; }
        public OptionalPes OptionalPesHeader { get; set; }
        public byte[] Data { get; set; }
        public TimeSpan DTS { get; set; }
        public TimeSpan PTS { get; set; }
        public TimeSpan Timestamp => DTS != TimeSpan.MinValue ? DTS : PTS;

        public static readonly IList<PesStreamTypes> SimplePesTypes = new[]
        {
            PesStreamTypes.ProgramStreamMap,
            PesStreamTypes.PaddingStream,
            PesStreamTypes.PrivateStream2,
            PesStreamTypes.ECMStream,
            PesStreamTypes.EMMStream,
            PesStreamTypes.ProgramStreamDirectory,
            PesStreamTypes.DSMCCStream,
            PesStreamTypes.H2221TypeEStream
        };

        private int _pesBytes;
        private byte[] _data;

        public Pes(PesStreamTypes type, byte[] payload, OptionalPes optionalPesHeader = null)
        {
            PacketStartCodePrefix = DefaultPacketStartCodePrefix;
            StreamId = (byte)type;
            PesPacketLength = (ushort)(payload?.Length ?? 0);

            if (type == PesStreamTypes.PaddingStream)
            {
                Data = new byte[PesPacketLength];
                for (int i = 0; i < Data.Length; i++)
                    Data[i] = 0xFF;
                return;
            }

            if (!SimplePesTypes.Contains(type) && optionalPesHeader == null)
                throw new ArgumentException($"PES streams of type {type} must provide the optional PES header object");

            OptionalPesHeader = optionalPesHeader;
            Data = new byte[PesPacketLength];
            if (payload != null && payload.Length > 0)
                Buffer.BlockCopy(payload, 0, Data, 0, payload.Length);
        }

        public Pes(TsPacket packet)
        {
            DTS = TimeSpan.MinValue;
            PTS = TimeSpan.MinValue;

            ArraySegment<byte> payload = packet.GetPayloadSegment();
            if (payload.Array == null || payload.Count < 6)
            {
                Dropped = true;
                return;
            }

            byte[] buffer = payload.Array;
            int offset = payload.Offset;
            PacketStartCodePrefix = (uint)((buffer[offset] << 16) + (buffer[offset + 1] << 8) + buffer[offset + 2]);
            StreamId = buffer[offset + 3];
            PesPacketLength = (ushort)((buffer[offset + 4] << 8) + buffer[offset + 5]);

            if (PacketStartCodePrefix != DefaultPacketStartCodePrefix)
            {
                Dropped = true;
                return;
            }

            const double frequency = 90000.0;
            if (packet.PesHeader.Dts >= 0)
                DTS = TimeSpan.FromSeconds(packet.PesHeader.Dts / frequency);
            if (packet.PesHeader.Pts >= 0)
                PTS = TimeSpan.FromSeconds(packet.PesHeader.Pts / frequency);

            int initialCapacity = PesPacketLength > 0 ? PesPacketLength + 6 : Math.Max(packet.PayloadLen, 64 * 1024);
            _data = new byte[initialCapacity];
            AddPayload(buffer, offset, payload.Count);
        }

        public bool HasAllBytes()
        {
            if (PesPacketLength == 0)
                return true;

            return _pesBytes >= PesPacketLength + 6;
        }

        public bool Add(TsPacket packet)
        {
            ArraySegment<byte> payload = packet.GetPayloadSegment();
            if (packet.PayloadUnitStartIndicator || _data == null || payload.Array == null || payload.Count <= 0)
                return false;

            if (PesPacketLength == 0)
            {
                AddPayload(payload.Array, payload.Offset, payload.Count);
                return true;
            }

            int bytesRemaining = PesPacketLength + 6 - _pesBytes;
            if (bytesRemaining <= 0)
                return true;

            AddPayload(payload.Array, payload.Offset, Math.Min(payload.Count, bytesRemaining));
            return true;
        }

        public byte[] GetDataFromPes()
        {
            var data = new byte[6 + PesPacketLength];

            data[0] = 0x0;
            data[1] = 0x0;
            data[2] = 0x1;
            data[3] = StreamId;
            data[4] = (byte)(PesPacketLength >> 8);
            data[5] = (byte)(PesPacketLength & 0xFF);

            if (SimplePesTypes.Contains((PesStreamTypes)StreamId) || StreamId == (byte)PesStreamTypes.PaddingStream)
            {
                Buffer.BlockCopy(Data, 0, data, 6, PesPacketLength);
                return data;
            }

            data[6] = 0b10000000;
            data[6] += (byte)(OptionalPesHeader.ScramblingControl << 4);
            data[6] += (byte)(OptionalPesHeader.Priority ? 1 << 3 : 0);
            data[6] += (byte)(OptionalPesHeader.DataAlignmentIndicator ? 1 << 2 : 0);
            data[6] += (byte)(OptionalPesHeader.Copyright ? 1 << 1 : 0);
            data[6] += (byte)(OptionalPesHeader.OriginalOrCopy ? 1 : 0);
            data[8] += OptionalPesHeader.PesHeaderLength;

            var payloadPosition = 9;
            if (OptionalPesHeader.OptionalFields != null && OptionalPesHeader.OptionalFields.Length > 0)
            {
                Buffer.BlockCopy(OptionalPesHeader.OptionalFields, 0, data, payloadPosition, OptionalPesHeader.OptionalFields.Length);
                payloadPosition += OptionalPesHeader.OptionalFields.Length;
            }

            Buffer.BlockCopy(Data, 0, data, payloadPosition, PesPacketLength - 3);
            return data;
        }

        public bool Decode()
        {
            try
            {
                if (_data == null || !HasAllBytes())
                    return false;

                if (!SimplePesTypes.Contains((PesStreamTypes)StreamId))
                {
                    if (_pesBytes < 9)
                        return false;

                    OptionalPesHeader = new OptionalPes
                    {
                        MarkerBits = (byte)((_data[6] >> 6) & 0x03),
                        ScramblingControl = (byte)((_data[6] >> 4) & 0x03),
                        Priority = (_data[6] & 0x08) == 0x08,
                        DataAlignmentIndicator = (_data[6] & 0x04) == 0x04,
                        Copyright = (_data[6] & 0x02) == 0x02,
                        OriginalOrCopy = (_data[6] & 0x01) == 0x01,
                        PtsdtsIndicator = (byte)((_data[7] >> 6) & 0x03),
                        EscrFlag = (_data[7] & 0x20) == 0x20,
                        EsRateFlag = (_data[7] & 0x10) == 0x10,
                        DsmTrickModeFlag = (_data[7] & 0x08) == 0x08,
                        AdditionalCopyInfoFlag = (_data[7] & 0x04) == 0x04,
                        CrcFlag = (_data[7] & 0x02) == 0x02,
                        ExtensionFlag = (_data[7] & 0x01) == 0x01,
                        PesHeaderLength = _data[8]
                    };
                }

                Data = new byte[_pesBytes];
                Buffer.BlockCopy(_data, 0, Data, 0, _pesBytes);
                _data = null;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to decode PES packet: " + ex.Message);
                return false;
            }
        }

        private void AddPayload(byte[] payload, int payloadOffset, int payloadLength)
        {
            if (payload == null || payloadLength <= 0)
                return;

            EnsureCapacity(_pesBytes + payloadLength);
            Buffer.BlockCopy(payload, payloadOffset, _data, _pesBytes, payloadLength);
            _pesBytes += payloadLength;
        }

        private void EnsureCapacity(int requiredCapacity)
        {
            if (_data.Length >= requiredCapacity)
                return;

            int newCapacity = _data.Length;
            while (newCapacity < requiredCapacity)
                newCapacity *= 2;

            Array.Resize(ref _data, newCapacity);
        }
    }
}

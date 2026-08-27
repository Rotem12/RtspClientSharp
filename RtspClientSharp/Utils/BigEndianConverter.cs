using System.Runtime.CompilerServices;

namespace RtspClientSharp.Utils
{
    static class BigEndianConverter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint) (buffer[offset] << 24 |
                           buffer[offset + 1] << 16 |
                           buffer[offset + 2] << 8 |
                           buffer[offset + 3]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadUInt24(byte[] buffer, int offset)
        {
            return buffer[offset] << 16 |
                   buffer[offset + 1] << 8 |
                   buffer[offset + 2];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadUInt16(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }
    }
}

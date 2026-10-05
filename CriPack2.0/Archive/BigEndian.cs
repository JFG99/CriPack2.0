namespace CriPack.Archive
{
    static class BigEndian
    {
        public static ushort ReadUInt16(this byte[] buffer, int offset) => (ushort)(buffer[offset] << 8 | buffer[offset + 1]);

        public static uint ReadUInt32(this byte[] buffer, int offset) => (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer.ReadUInt16(offset + 2));//buffer[offset + 2] << 8 | buffer[offset + 3]);

        public static ulong ReadUInt64(this byte[] buffer, int offset) => ((ulong)buffer.ReadUInt32(offset) << 32) | buffer.ReadUInt32(offset + 4);

        public static void WriteUInt16(this byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        public static void WriteUInt32(this byte[] buffer, int offset, uint value)
        {
            buffer.WriteUInt16(offset, (ushort)(value >> 16));
            buffer.WriteUInt16(offset + 2, (ushort)value);
        }

        public static void WriteUInt64(this byte[] buffer, int offset, ulong value)
        {
            buffer.WriteUInt32(offset, (uint)(value >> 32));
            buffer.WriteUInt32(offset + 4, (uint)value);
        }
    }
}

namespace CpkToolkit.Core;

/// <summary>Reads and writes big-endian integers, the byte order used inside @UTF tables.</summary>
public static class BigEndianConverter
{
    public static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    public static uint ReadUInt32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) |
        ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) |
        (uint)buffer[offset + 3];

    public static ulong ReadUInt64(byte[] buffer, int offset) =>
        ((ulong)ReadUInt32(buffer, offset) << 32) | ReadUInt32(buffer, offset + 4);

    public static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    public static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        WriteUInt16(buffer, offset, (ushort)(value >> 16));
        WriteUInt16(buffer, offset + 2, (ushort)value);
    }

    public static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        WriteUInt32(buffer, offset, (uint)(value >> 32));
        WriteUInt32(buffer, offset + 4, (uint)value);
    }
}

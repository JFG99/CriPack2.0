using System.Buffers.Binary;
using System.Text;

namespace CpkToolkit.Core;

/// <summary>
/// One top-level CPK packet: a 16-byte little-endian header (magic, flags, payload length)
/// followed by a big-endian @UTF table that may be XOR-obfuscated.
/// <see cref="Data"/> always holds the packet with the payload decrypted.
/// </summary>
public sealed class CpkPacket
{
    public const int PacketHeaderLength = 16;
    private const ulong MaximumPayloadLength = 256UL * 1024 * 1024;

    private CpkPacket(string magic, byte[] data, int payloadLength, bool isEncrypted)
    {
        Magic = magic;
        Data = data;
        PayloadLength = payloadLength;
        IsEncrypted = isEncrypted;
    }

    public string Magic { get; }

    /// <summary>Packet header followed by the decrypted payload (and any trailing padding that was supplied).</summary>
    public byte[] Data { get; }

    public int PayloadLength { get; }

    public bool IsEncrypted { get; }

    public static CpkPacket Read(ArchiveByteReader reader, long offset, string? expectedMagic = null)
    {
        Span<byte> headerBytes = stackalloc byte[PacketHeaderLength];
        reader.ReadExactly(offset, headerBytes);

        ulong declaredPayloadLength = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes[8..]);
        if (declaredPayloadLength > MaximumPayloadLength)
            throw new InvalidDataException($"The packet at 0x{offset:X} declares an implausible size ({declaredPayloadLength:N0} bytes).");

        var rawBytes = new byte[PacketHeaderLength + (int)declaredPayloadLength];
        reader.ReadExactly(offset, rawBytes);

        CpkPacket packet = FromBytes(rawBytes);
        if (expectedMagic != null && packet.Magic != expectedMagic)
            throw new InvalidDataException($"Expected a '{expectedMagic}' packet at 0x{offset:X} but found '{packet.Magic}'.");
        return packet;
    }

    /// <summary>Builds a packet from raw file bytes (the array is copied, the payload is decrypted if needed).</summary>
    public static CpkPacket FromBytes(byte[] rawBytes)
    {
        if (rawBytes.Length < PacketHeaderLength + 4)
            throw new InvalidDataException("The data is too short to be a CPK packet.");

        string magic = Encoding.ASCII.GetString(rawBytes, 0, 4);
        ulong declaredPayloadLength = BinaryPrimitives.ReadUInt64LittleEndian(rawBytes.AsSpan(8));
        int availablePayload = rawBytes.Length - PacketHeaderLength;
        int payloadLength = declaredPayloadLength > (ulong)availablePayload ? availablePayload : (int)declaredPayloadLength;

        byte[] data = (byte[])rawBytes.Clone();
        bool isEncrypted = !UtfTable.HasUtfMagic(data, PacketHeaderLength);
        if (isEncrypted)
            CpkPacketCipher.ApplyXorMask(data, PacketHeaderLength, payloadLength);

        return new CpkPacket(magic, data, payloadLength, isEncrypted);
    }

    /// <summary>An independent copy whose <see cref="Data"/> can be patched without touching this packet.</summary>
    public CpkPacket CloneForPatching() => new(Magic, (byte[])Data.Clone(), PayloadLength, IsEncrypted);

    /// <summary>The bytes to store in a file: the payload is re-encrypted if the original was encrypted.</summary>
    public byte[] GetBytesForWriting()
    {
        byte[] bytes = (byte[])Data.Clone();
        if (IsEncrypted)
            CpkPacketCipher.ApplyXorMask(bytes, PacketHeaderLength, PayloadLength);
        return bytes;
    }
}

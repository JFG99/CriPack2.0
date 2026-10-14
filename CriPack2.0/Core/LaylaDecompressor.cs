using System.Buffers.Binary;
using System.Text;

namespace CpkToolkit.Core;

/// <summary>
/// Decompresses CRILAYLA data, the "Layla" compression used by CPK archives.
///
/// Layout of a CRILAYLA block: 8 bytes "CRILAYLA", u32 uncompressed size (without the 0x100 raw prefix),
/// u32 size of the compressed bit stream, the bit stream itself (read backwards), then the first 0x100
/// bytes of the original data stored uncompressed.
///
/// The bit stream is located through the header's size field, as the CRI tools do, so extra bytes after the
/// raw prefix (written by some third-party compressors) do not matter. If that start position fails to
/// decode, the older "stream ends right before the raw prefix at the end of the data" assumption is tried too.
/// </summary>
public static class LaylaDecompressor
{
    private const int HeaderLength = 0x10;
    private const int RawPrefixLength = 0x100;
    private static readonly int[] ExtraLengthBitWidths = { 2, 3, 5, 8 };

    public static byte[] Decompress(byte[] compressedData)
    {
        if (compressedData.Length < HeaderLength + RawPrefixLength ||
            Encoding.ASCII.GetString(compressedData, 0, 8) != "CRILAYLA")
            throw new InvalidDataException("The data is not CRILAYLA compressed.");

        int uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(compressedData.AsSpan(8));
        int compressedStreamLength = BinaryPrimitives.ReadInt32LittleEndian(compressedData.AsSpan(12));
        if (uncompressedSize < 0 || uncompressedSize > Array.MaxLength - RawPrefixLength ||
            compressedStreamLength < 0 || (long)HeaderLength + compressedStreamLength + RawPrefixLength > compressedData.Length)
            throw new InvalidDataException(
                $"The CRILAYLA header is invalid (uncompressed size {uncompressedSize:N0}, stream length {compressedStreamLength:N0}, " +
                $"data length {compressedData.Length:N0}).");

        int rawPrefixIndex = HeaderLength + compressedStreamLength;
        int headerBasedStreamStart = rawPrefixIndex - 1;
        int endBasedStreamStart = compressedData.Length - RawPrefixLength - 1;

        try
        {
            return DecodeStream(compressedData, uncompressedSize, rawPrefixIndex, headerBasedStreamStart);
        }
        catch (InvalidDataException headerBasedFailure)
        {
            if (endBasedStreamStart == headerBasedStreamStart)
                throw;

            try
            {
                return DecodeStream(compressedData, uncompressedSize, endBasedStreamStart + 1, endBasedStreamStart);
            }
            catch (InvalidDataException)
            {
                throw headerBasedFailure;   // report the failure of the standard interpretation
            }
        }
    }

    private static byte[] DecodeStream(byte[] compressedData, int uncompressedSize, int rawPrefixIndex, int streamStartIndex)
    {
        var output = new byte[uncompressedSize + RawPrefixLength];

        // The first 0x100 bytes of the result are stored uncompressed right after the bit stream.
        Array.Copy(compressedData, rawPrefixIndex, output, 0, RawPrefixLength);

        int inputPosition = streamStartIndex;                        // the bit stream is read backwards
        int outputEndIndex = RawPrefixLength + uncompressedSize - 1; // and written backwards
        int bytesWritten = 0;
        int bitPool = 0;
        int bitsRemaining = 0;

        int ReadBits(int bitCount)
        {
            int result = 0;
            int bitsCollected = 0;
            while (bitsCollected < bitCount)
            {
                if (bitsRemaining == 0)
                {
                    if (inputPosition < HeaderLength)
                        throw new InvalidDataException("the bit stream ended before all data was decoded");
                    bitPool = compressedData[inputPosition--];
                    bitsRemaining = 8;
                }

                int bitsToTake = Math.Min(bitsRemaining, bitCount - bitsCollected);
                result = (result << bitsToTake) | ((bitPool >> (bitsRemaining - bitsToTake)) & ((1 << bitsToTake) - 1));
                bitsRemaining -= bitsToTake;
                bitsCollected += bitsToTake;
            }

            return result;
        }

        string failureReason = "the stream is corrupt";
        try
        {
            while (bytesWritten < uncompressedSize)
            {
                if (ReadBits(1) != 0)
                {
                    // Back-reference into data that was already written (which is at higher indices).
                    int backReferenceOffset = ReadBits(13) + 3;
                    int copyLength = 3;

                    int levelIndex;
                    for (levelIndex = 0; levelIndex < ExtraLengthBitWidths.Length; levelIndex++)
                    {
                        int levelValue = ReadBits(ExtraLengthBitWidths[levelIndex]);
                        copyLength += levelValue;
                        if (levelValue != (1 << ExtraLengthBitWidths[levelIndex]) - 1)
                            break;
                    }

                    if (levelIndex == ExtraLengthBitWidths.Length)
                    {
                        int extraByte;
                        do
                        {
                            extraByte = ReadBits(8);
                            copyLength += extraByte;
                        }
                        while (extraByte == 255);
                    }

                    if (backReferenceOffset > bytesWritten)
                    {
                        failureReason = $"a back-reference reaches {backReferenceOffset:N0} bytes back but only {bytesWritten:N0} bytes exist";
                        throw new InvalidDataException(failureReason);
                    }

                    // A copy that would run past the end of the data is cut short instead of overwriting
                    // the raw prefix (some compressors let the last match overshoot).
                    for (int copied = 0; copied < copyLength && bytesWritten < uncompressedSize; copied++, bytesWritten++)
                    {
                        output[outputEndIndex - bytesWritten] = output[outputEndIndex - bytesWritten + backReferenceOffset];
                    }
                }
                else
                {
                    output[outputEndIndex - bytesWritten] = (byte)ReadBits(8);
                    bytesWritten++;
                }
            }
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"CRILAYLA decode failed: {exception.Message} (stream start 0x{streamStartIndex:X}, stopped at 0x{inputPosition:X}, " +
                $"{bytesWritten:N0} of {uncompressedSize:N0} bytes decoded, data length {compressedData.Length:N0}).");
        }
        catch (IndexOutOfRangeException)
        {
            throw new InvalidDataException(
                $"CRILAYLA decode failed: {failureReason} (stream start 0x{streamStartIndex:X}, stopped at 0x{inputPosition:X}, " +
                $"{bytesWritten:N0} of {uncompressedSize:N0} bytes decoded, data length {compressedData.Length:N0}).");
        }

        return output;
    }
}

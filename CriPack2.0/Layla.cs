using System.Text;

public static class Layla
{
    public static byte[] Decompress(byte[] compressedData)
    {
        if (Encoding.ASCII.GetString(compressedData, 0, 8) != "CRILAYLA")
            throw new InvalidDataException("Not CRILAYLA");

        int uncompressedSize = BitConverter.ToInt32(compressedData, 8);
        int rawPrefixOffset = BitConverter.ToInt32(compressedData, 12);

        var output = new byte[uncompressedSize + 0x100];
        Array.Copy(compressedData, 0x10 + rawPrefixOffset, output, 0, 0x100);   // raw 0x100-byte prefix

        int inputPosition = compressedData.Length - 0x100 - 1;   // bitstream is read backwards
        int outputEndIndex = 0x100 + uncompressedSize - 1;        // and written backwards
        int bytesWritten = 0;
        int bitPool = 0;
        int bitsRemaining = 0;

        int ReadBits(int bitCount)
        {
            int result = 0, bitsCollected = 0;
            while (bitsCollected < bitCount)
            {
                if (bitsRemaining == 0)
                {
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

        int[] extraLengthBitWidths = { 2, 3, 5, 8 };
        while (bytesWritten < uncompressedSize)
        {
            if (ReadBits(1) != 0)
            {
                // Back-reference: copy bytes from earlier in the (reverse-written) output
                int backReferenceOffset = ReadBits(13) + 3;
                int copyLength = 3;
                int levelIndex;
                for (levelIndex = 0; levelIndex < 4; levelIndex++)
                {
                    int levelValue = ReadBits(extraLengthBitWidths[levelIndex]);
                    copyLength += levelValue;
                    if (levelValue != (1 << extraLengthBitWidths[levelIndex]) - 1) break;
                }
                if (levelIndex == 4)
                {
                    int extraByte;
                    do
                    {
                        extraByte = ReadBits(8);
                        copyLength += extraByte;
                    } while (extraByte == 255);
                }
                for (int i = 0; i < copyLength; i++, bytesWritten++)
                    output[outputEndIndex - bytesWritten] = output[outputEndIndex - bytesWritten + backReferenceOffset];
            }
            else
            {
                // Literal byte
                output[outputEndIndex - bytesWritten] = (byte)ReadBits(8);
                bytesWritten++;
            }
        }
        return output;
    }
}
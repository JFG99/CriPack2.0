namespace CpkToolkit.Core;

/// <summary>The rolling XOR mask CRIWARE applies to obfuscated @UTF payloads.</summary>
public static class CpkPacketCipher
{
    private const uint InitialMask = 0x655F;
    private const uint MaskMultiplier = 0x4115;

    /// <summary>Symmetric: the same call encrypts and decrypts.</summary>
    public static void ApplyXorMask(byte[] buffer, int startOffset, int length)
    {
        uint mask = InitialMask;
        for (int index = 0; index < length; index++)
        {
            buffer[startOffset + index] ^= (byte)mask;
            mask *= MaskMultiplier;
        }
    }
}

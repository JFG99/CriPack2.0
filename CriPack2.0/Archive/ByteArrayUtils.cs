using System;
using System.Collections.Generic;
using System.Text;

namespace CriPack.Archive
{
    public static class ByteArrayUtils
    {
        // Symmetric: the same call encrypts and decrypts
        static void ApplyXorMask(byte[] buffer, int startOffset, int length)
        {
            uint mask = 0x655F;
            const uint multiplier = 0x4115;
            for (int i = 0; i < length; i++)
            {
                buffer[startOffset + i] ^= (byte)mask;
                mask *= multiplier;
            }
        }
    }
}

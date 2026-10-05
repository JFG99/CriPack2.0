namespace CriPack.Archive.Enum
{
    public enum UtfType : byte
    {
        Byte, SByte, UShort, Short, UInt, Int, ULong, Long, Float,
        String = 0xA, Data = 0xB, Guid = 0xC
    }
}

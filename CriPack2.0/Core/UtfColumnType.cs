namespace CpkToolkit.Core;

/// <summary>The value type of a @UTF table column (low nibble of the column flag byte).</summary>
public enum UtfColumnType : byte
{
    Byte = 0x0,
    SByte = 0x1,
    UShort = 0x2,
    Short = 0x3,
    UInt = 0x4,
    Int = 0x5,
    ULong = 0x6,
    Long = 0x7,
    Float = 0x8,
    String = 0xA,
    Data = 0xB,
    Guid = 0xC
}

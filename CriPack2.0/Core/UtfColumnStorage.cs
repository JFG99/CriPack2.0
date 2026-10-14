namespace CpkToolkit.Core;

/// <summary>How a @UTF table column stores its values (high nibble of the column flag byte).</summary>
public enum UtfColumnStorage : byte
{
    /// <summary>The column has no stored value (all rows are zero / empty).</summary>
    ZeroValue = 0x10,

    /// <summary>One value stored in the schema, shared by every row.</summary>
    Constant = 0x30,

    /// <summary>Each row stores its own value.</summary>
    PerRow = 0x50
}

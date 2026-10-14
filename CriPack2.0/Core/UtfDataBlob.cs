namespace CpkToolkit.Core;

/// <summary>A reference to a binary blob inside a @UTF table's data area.</summary>
/// <param name="Offset">Absolute position of the blob inside the buffer that was parsed.</param>
/// <param name="Length">Length of the blob in bytes.</param>
public readonly record struct UtfDataBlob(int Offset, int Length);

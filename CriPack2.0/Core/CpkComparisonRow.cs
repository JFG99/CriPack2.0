namespace CpkToolkit.Core;

/// <summary>One file that differs between two archives.</summary>
public sealed class CpkComparisonRow
{
    public required string FullPath { get; init; }

    public required CpkComparisonStatus Status { get; init; }

    /// <summary>The entry in the first archive, or null when the file only exists in the second.</summary>
    public CpkEntry? FirstEntry { get; init; }

    /// <summary>The entry in the second archive, or null when the file only exists in the first.</summary>
    public CpkEntry? SecondEntry { get; init; }
}

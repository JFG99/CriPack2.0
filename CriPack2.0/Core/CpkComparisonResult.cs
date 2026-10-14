namespace CpkToolkit.Core;

/// <summary>The outcome of comparing two archives.</summary>
public sealed class CpkComparisonResult
{
    /// <summary>Only the files that differ, sorted by status and then by path.</summary>
    public required IReadOnlyList<CpkComparisonRow> DifferentRows { get; init; }

    /// <summary>Files verified to have identical content in both archives.</summary>
    public required int IdenticalCount { get; init; }

    /// <summary>
    /// Files with the same size in both archives whose bytes were NOT compared (only in quick mode).
    /// These could still contain different data of the same length.
    /// </summary>
    public required int SameSizeUnverifiedCount { get; init; }

    public required int FirstArchiveFileCount { get; init; }

    public required int SecondArchiveFileCount { get; init; }

    public required bool ContentsWereVerified { get; init; }

    public int CountWithStatus(CpkComparisonStatus status) => DifferentRows.Count(row => row.Status == status);
}

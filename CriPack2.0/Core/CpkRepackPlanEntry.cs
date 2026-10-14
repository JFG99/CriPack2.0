namespace CpkToolkit.Core;

/// <summary>Where one file will be placed in the rebuilt archive and where its data comes from.</summary>
public sealed class CpkRepackPlanEntry
{
    public CpkEntry Entry { get; init; } = null!;

    /// <summary>Path of the replacement file on disk, or null to copy the original data unchanged.</summary>
    public string? ReplacementPath { get; init; }

    public long NewStoredSize { get; init; }

    public long NewExtractSize { get; init; }

    public long NewAbsoluteOffset { get; set; }

    /// <summary>False when this entry shares the data block of an earlier entry that is already written.</summary>
    public bool WritesOwnData { get; set; } = true;
}

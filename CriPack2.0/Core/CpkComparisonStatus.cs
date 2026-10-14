namespace CpkToolkit.Core;

/// <summary>How one file differs between the first (original) and second (patched) archive.</summary>
public enum CpkComparisonStatus
{
    /// <summary>The file has the same content in both archives.</summary>
    Identical,

    /// <summary>The file exists in both archives but its content is different.</summary>
    ContentChanged,

    /// <summary>The content is the same, but it is stored differently (for example compressed in one, raw in the other).</summary>
    StorageOnlyChanged,

    /// <summary>The file exists only in the first archive.</summary>
    OnlyInFirst,

    /// <summary>The file exists only in the second archive.</summary>
    OnlyInSecond,

    /// <summary>The two stored versions differ and at least one could not be decompressed, so the content could not be compared.</summary>
    CouldNotCompare
}

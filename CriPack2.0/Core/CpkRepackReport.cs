namespace CpkToolkit.Core;

public sealed class CpkRepackReport
{
    public string OutputPath { get; init; } = string.Empty;

    public int ReplacedEntryCount { get; init; }

    public int KeptEntryCount { get; init; }

    public int SharedDataEntryCount { get; init; }

    public long OutputLength { get; init; }

    /// <summary>Observations about header fields that were left alone or handled by a fallback rule.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

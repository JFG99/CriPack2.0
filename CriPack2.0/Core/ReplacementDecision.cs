namespace CpkToolkit.Core;

/// <summary>The outcome of looking for a replacement file for one archive entry.</summary>
public sealed record ReplacementDecision(CpkEntry Entry, string? ReplacementPath, ReplacementDecisionKind Kind)
{
    public bool IsReplacement => ReplacementPath != null;

    /// <summary>True when a file exists in the folder but was deliberately not used because it looked unedited.</summary>
    public bool IsSkippedAsUnedited => Kind == ReplacementDecisionKind.SkippedIdenticalContent;
}

using System.Text;

namespace CpkToolkit.Core;

/// <summary>Collects one <see cref="ReplacementDecision"/> per archive entry during a repack, for reporting.</summary>
public sealed class RepackDecisionLog
{
    private readonly List<ReplacementDecision> decisions = new();

    public IReadOnlyList<ReplacementDecision> Decisions => decisions;

    public void Add(ReplacementDecision decision) => decisions.Add(decision);

    public int CountOf(ReplacementDecisionKind kind) => decisions.Count(decision => decision.Kind == kind);

    public int ReplacedCount => decisions.Count(decision => decision.IsReplacement);

    /// <summary>Files that exist in the folder but were skipped because their content equals the extracted original.</summary>
    public IReadOnlyList<ReplacementDecision> SkippedAsUnedited =>
        decisions.Where(decision => decision.IsSkippedAsUnedited).ToList();

    public int FilesPresentInFolder => decisions.Count(decision => decision.Kind != ReplacementDecisionKind.NoFileInFolder);

    /// <summary>Writes one CSV row per archive entry that has a file in the folder, plus the entries without one.</summary>
    public void WriteCsv(string path)
    {
        var text = new StringBuilder();
        text.AppendLine("File,Decision,Reason,ArchiveExtractSize");
        foreach (ReplacementDecision decision in decisions
                     .OrderBy(d => d.IsReplacement ? 2 : d.IsSkippedAsUnedited ? 0 : 1)
                     .ThenBy(d => d.Entry.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            string outcome = decision.IsReplacement ? "Replaced"
                : decision.IsSkippedAsUnedited ? "Skipped (identical to extracted)"
                : "Kept (no file in folder)";
            text.AppendLine($"{Quote(decision.Entry.FullPath)},{outcome},{decision.Kind},{decision.Entry.ExtractSize}");
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

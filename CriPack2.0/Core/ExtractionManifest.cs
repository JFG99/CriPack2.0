namespace CpkToolkit.Core;

/// <summary>
/// A small text file stored next to the extracted files. For each file it records the length, last-write time
/// and SHA-256 hash from extraction time, so a later repack can tell which files were modified
/// without decompressing or re-reading the original archive data.
/// </summary>
public sealed class ExtractionManifest
{
    public const string FileName = "_cpk_manifest.tsv";

    private readonly Dictionary<string, ExtractionManifestRecord> recordsByArchivePath =
        new(StringComparer.OrdinalIgnoreCase);

    private const string SourceLinePrefix = "#source\t";

    public int Count => recordsByArchivePath.Count;

    /// <summary>File name of the CPK this manifest was written for (null for manifests from older versions).</summary>
    public string? SourceArchiveName { get; private set; }

    public long SourceArchiveLength { get; private set; }

    public void SetSource(string archiveName, long archiveLength)
    {
        SourceArchiveName = archiveName;
        SourceArchiveLength = archiveLength;
    }

    public static ExtractionManifest? TryLoad(string directory)
    {
        string manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath))
            return null;

        var manifest = new ExtractionManifest();
        foreach (string line in File.ReadLines(manifestPath))
        {
            if (line.StartsWith(SourceLinePrefix, StringComparison.Ordinal))
            {
                string[] sourceParts = line.Split('\t');
                if (sourceParts.Length == 3 && long.TryParse(sourceParts[2], out long sourceLength))
                    manifest.SetSource(sourceParts[1], sourceLength);
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length != 4 ||
                !long.TryParse(parts[1], out long length) ||
                !long.TryParse(parts[2], out long ticks))
                continue;

            manifest.recordsByArchivePath[parts[0]] = new ExtractionManifestRecord(length, ticks, parts[3]);
        }

        return manifest;
    }

    public void Save(string directory)
    {
        IEnumerable<string> recordLines = recordsByArchivePath
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}\t{pair.Value.Length}\t{pair.Value.LastWriteUtcTicks}\t{pair.Value.Sha256Hex}");
        IEnumerable<string> lines = SourceArchiveName == null
            ? recordLines
            : recordLines.Prepend($"{SourceLinePrefix}{SourceArchiveName}\t{SourceArchiveLength}");
        File.WriteAllLines(Path.Combine(directory, FileName), lines);
    }

    public void Set(string archivePath, ExtractionManifestRecord record) => recordsByArchivePath[archivePath] = record;

    public bool TryGet(string archivePath, out ExtractionManifestRecord record) =>
        recordsByArchivePath.TryGetValue(archivePath, out record);
}

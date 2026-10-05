using CriPack.Archive;
using System.Security.Cryptography;

public static class CpkTool
{
    const string ManifestFileName = "_manifest.tsv";   // "<path>\t<sha256 of extracted bytes>"

    // ---------- Extract ----------

    public static void ExtractAll(string cpkPath, string outputDirectory)
    {
        using var archive = new CpkArchive(cpkPath);
        Directory.CreateDirectory(outputDirectory);

        var manifestLines = new List<string>(archive.Entries.Count);
        int processed = 0;

        foreach (var entry in archive.Entries)
        {
            string localPath = ResolveSafePath(outputDirectory, entry.FullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            byte[] data = archive.Extract(entry);
            File.WriteAllBytes(localPath, data);
            manifestLines.Add($"{entry.FullPath}\t{Convert.ToHexString(SHA256.HashData(data))}");

            if (++processed % 250 == 0 || processed == archive.Entries.Count)
                Console.WriteLine($"Extracted {processed}/{archive.Entries.Count}");
        }

        File.WriteAllLines(Path.Combine(outputDirectory, ManifestFileName), manifestLines);
    }

    // ---------- Repack ----------

    public static void RepackFromDirectory(string originalCpkPath, string modifiedDirectory, string outputCpkPath)
    {
        if (Path.GetFullPath(originalCpkPath).Equals(Path.GetFullPath(outputCpkPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must differ from the original CPK.");
        if (!Directory.Exists(modifiedDirectory))
            throw new DirectoryNotFoundException(modifiedDirectory);

        var originalHashes = LoadManifest(modifiedDirectory);   // null if no manifest
        if (originalHashes == null)
            Console.WriteLine("No manifest found: every file present in the directory will be treated as modified.");

        using var archive = new CpkArchive(originalCpkPath);

        ReportFilesNotInArchive(archive, modifiedDirectory);

        int replacedCount = 0, keptCount = 0;
        int processed = 0;

        archive.Repack(outputCpkPath, entry =>
        {
            byte[]? replacement = GetReplacementIfModified(entry, modifiedDirectory, originalHashes);

            if (replacement != null) replacedCount++; else keptCount++;
            if (++processed % 250 == 0 || processed == archive.Entries.Count)
                Console.WriteLine($"Processed {processed}/{archive.Entries.Count}");

            return replacement;
        });

        Console.WriteLine($"Done. Replaced: {replacedCount}, kept original: {keptCount}");
    }

    static byte[]? GetReplacementIfModified(CpkEntry entry, string modifiedDirectory, Dictionary<string, string>? originalHashes)
    {
        string path = ResolveSafePath(modifiedDirectory, entry.FullPath);
        if (!File.Exists(path)) return null; // file absent: keep original

        byte[] data = File.ReadAllBytes(path);

        if (originalHashes != null &&
            originalHashes.TryGetValue(entry.FullPath, out string? originalHash) &&
            Convert.ToHexString(SHA256.HashData(data)) == originalHash)
            return null; // identical to extraction: keep original

        return data;
    }

    // ---------- Helpers ----------

    static Dictionary<string, string>? LoadManifest(string directory)
    {
        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;

        var hashesByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(manifestPath))
        {
            int separatorIndex = line.LastIndexOf('\t');
            if (separatorIndex > 0)
                hashesByPath[line[..separatorIndex]] = line[(separatorIndex + 1)..];
        }
        return hashesByPath;
    }

    static void ReportFilesNotInArchive(CpkArchive archive, string directory)
    {
        var archivePaths = new HashSet<string>(archive.Entries.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(directory);

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relativePath == ManifestFileName) continue;
            if (!archivePaths.Contains(relativePath))
                Console.WriteLine($"Warning: '{relativePath}' is not in the original archive and will be ignored.");
        }
    }

    // Maps an archive path onto a directory and rejects anything that escapes it (e.g. "..\..\x")
    static string ResolveSafePath(string baseDirectory, string archivePath)
    {
        string root = Path.GetFullPath(baseDirectory);
        string combined = Path.GetFullPath(Path.Combine(root, archivePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe path in archive: {archivePath}");
        return combined;
    }
}
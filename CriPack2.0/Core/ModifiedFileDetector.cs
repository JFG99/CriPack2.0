using System.Security.Cryptography;

namespace CpkToolkit.Core;

/// <summary>
/// Decides which files in a folder are modified replacements for archive entries.
/// The folder may hold any subset of the archive's files: entries without a file in the folder are never touched.
/// With an extraction manifest present, a file is unchanged only if its SHA-256 equals the hash recorded at
/// extraction (a different length already proves a change, so no hash is needed then). Timestamps are ignored.
/// Without a manifest, or for a file the manifest does not list, every file present is a replacement.
/// </summary>
public sealed class ModifiedFileDetector
{
    private const int HashBufferLength = 4 * 1024 * 1024;

    private readonly string modifiedDirectory;
    private readonly ExtractionManifest? manifest;

    /// <param name="ignoreManifest">
    /// When true, the manifest is not consulted: every file present in the folder replaces its entry,
    /// even if it is identical to what was extracted.
    /// </param>
    public ModifiedFileDetector(string modifiedDirectory, bool ignoreManifest = false)
    {
        this.modifiedDirectory = modifiedDirectory;
        manifest = ignoreManifest ? null : ExtractionManifest.TryLoad(modifiedDirectory);
        ManifestWasIgnored = ignoreManifest;
    }

    public bool HasManifest => manifest != null;

    public bool ManifestWasIgnored { get; }

    /// <summary>
    /// Describes a mismatch between the archive being repacked and the archive the manifest was written for,
    /// or null when they agree (or the manifest does not say).
    /// </summary>
    public string? DescribeManifestSourceMismatch(CpkArchive archive)
    {
        if (manifest == null || manifest.SourceArchiveName == null)
            return null;

        string archiveName = Path.GetFileName(archive.ArchivePath);
        bool sameName = string.Equals(manifest.SourceArchiveName, archiveName, StringComparison.OrdinalIgnoreCase);
        bool sameLength = manifest.SourceArchiveLength == archive.ArchiveLength;
        if (sameName && sameLength)
            return null;

        return $"The manifest in this folder was written when '{manifest.SourceArchiveName}' " +
               $"({manifest.SourceArchiveLength:N0} bytes) was extracted, but the archive being repacked is " +
               $"'{archiveName}' ({archive.ArchiveLength:N0} bytes). Files that match the manifest are treated as " +
               "unedited and keep the data of the archive being repacked, which may not be what you expect.";
    }

    /// <returns>The path of the replacement file, or null when the archive's own data should be kept.</returns>
    public string? FindReplacementPath(CpkEntry entry, CancellationToken cancellationToken) =>
        Decide(entry, cancellationToken).ReplacementPath;

    /// <summary>Decides whether a file in the folder replaces the entry, and records why.</summary>
    public ReplacementDecision Decide(CpkEntry entry, CancellationToken cancellationToken)
    {
        string candidatePath;
        try
        {
            candidatePath = ArchivePathResolver.ResolveInsideDirectory(modifiedDirectory, entry.FullPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new ReplacementDecision(entry, null, ReplacementDecisionKind.NoFileInFolder);
        }

        var fileInfo = new FileInfo(candidatePath);
        if (!fileInfo.Exists)
            return new ReplacementDecision(entry, null, ReplacementDecisionKind.NoFileInFolder);

        if (manifest == null)
            return new ReplacementDecision(entry, candidatePath, ReplacementDecisionKind.ReplacedNoManifest);

        if (!manifest.TryGet(entry.FullPath, out ExtractionManifestRecord record))
            return new ReplacementDecision(entry, candidatePath, ReplacementDecisionKind.ReplacedNotInManifest);

        if (fileInfo.Length != record.Length)
            return new ReplacementDecision(entry, candidatePath, ReplacementDecisionKind.ReplacedLengthChanged);

        // Same length: only the content can tell whether the file was edited. Timestamps are not used at all,
        // so copying, touching or restoring files cannot make an edited file look unedited (or the reverse).
        return ComputeSha256Hex(candidatePath, cancellationToken) == record.Sha256Hex
            ? new ReplacementDecision(entry, null, ReplacementDecisionKind.SkippedIdenticalContent)
            : new ReplacementDecision(entry, candidatePath, ReplacementDecisionKind.ReplacedContentChanged);
    }

    /// <summary>Lists files in the folder that do not correspond to any archive entry (they will be ignored).</summary>
    public IReadOnlyList<string> FindFilesNotInArchive(CpkArchive archive)
    {
        var archivePaths = new HashSet<string>(archive.Entries.Select(entry => entry.FullPath), StringComparer.OrdinalIgnoreCase);
        string rootDirectory = Path.GetFullPath(modifiedDirectory);
        var unmatchedFiles = new List<string>();

        foreach (string filePath in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(rootDirectory, filePath).Replace('\\', '/');
            if (relativePath == ExtractionManifest.FileName)
                continue;
            if (!archivePaths.Contains(relativePath))
                unmatchedFiles.Add(relativePath);
        }

        return unmatchedFiles;
    }

    private static string ComputeSha256Hex(string path, CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferLength, FileOptions.SequentialScan);
        var buffer = new byte[HashBufferLength];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }
}

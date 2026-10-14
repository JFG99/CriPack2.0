namespace CpkToolkit.Core;

public static class ArchivePathResolver
{
    /// <summary>
    /// Maps a path stored in the archive ("dir/file.bin") onto a local directory and rejects
    /// anything that would escape that directory (for example "..\..\file").
    /// </summary>
    public static string ResolveInsideDirectory(string baseDirectory, string archivePath)
    {
        string rootDirectory = Path.GetFullPath(baseDirectory);
        string combinedPath = Path.GetFullPath(
            Path.Combine(rootDirectory, archivePath.Replace('/', Path.DirectorySeparatorChar)));

        string rootWithSeparator = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;

        if (!combinedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe path in archive: {archivePath}");

        return combinedPath;
    }
}

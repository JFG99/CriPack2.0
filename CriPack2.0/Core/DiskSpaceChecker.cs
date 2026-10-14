namespace CpkToolkit.Core;

public static class DiskSpaceChecker
{
    /// <summary>Throws when the drive that will hold <paramref name="targetPath"/> clearly lacks the space.</summary>
    public static void EnsureFreeSpace(string targetPath, long requiredBytes)
    {
        long? availableBytes = TryGetAvailableBytes(targetPath);
        if (availableBytes.HasValue && availableBytes.Value < requiredBytes)
        {
            throw new IOException(
                $"Not enough free disk space: {ByteSizeFormatter.Format(requiredBytes)} needed, " +
                $"{ByteSizeFormatter.Format(availableBytes.Value)} available.");
        }
    }

    private static long? TryGetAvailableBytes(string targetPath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return null;   // network paths and other roots DriveInfo cannot describe
        }
    }
}

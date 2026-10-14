namespace CpkToolkit.Core;

/// <summary>One file listed in the archive's TOC.</summary>
public sealed class CpkEntry
{
    public int TocRowIndex { get; init; }

    public string? DirectoryName { get; init; }

    public string FileName { get; init; } = string.Empty;

    /// <summary>Number of bytes the file occupies inside the archive (compressed size for compressed files).</summary>
    public long StoredSize { get; init; }

    /// <summary>Size after decompression (equal to <see cref="StoredSize"/> for uncompressed files).</summary>
    public long ExtractSize { get; init; }

    /// <summary>Position of the file's data from the start of the CPK file.</summary>
    public long AbsoluteOffset { get; init; }

    public long? Id { get; init; }

    public bool IsCompressed => ExtractSize > StoredSize;

    public string FullPath
    {
        get
        {
            string directory = (DirectoryName ?? string.Empty).Replace('\\', '/').Trim('/');
            return directory.Length == 0 ? FileName : directory + "/" + FileName;
        }
    }

    internal UtfCell StoredSizeCell { get; init; } = null!;

    internal UtfCell ExtractSizeCell { get; init; } = null!;

    internal UtfCell FileOffsetCell { get; init; } = null!;
}

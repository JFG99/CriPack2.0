namespace CpkToolkit.Core;

/// <summary>A top-level packet of the archive (TOC, ITOC, GTOC, ETOC, HTOC, ...) as listed in the CPK header.</summary>
public sealed class CpkSection
{
    /// <summary>Header field prefix, for example "Toc" for the TocOffset/TocSize pair.</summary>
    public string Name { get; init; } = string.Empty;

    public long Offset { get; init; }

    public long Size { get; init; }

    public long EndOffset => Offset + Size;

    /// <summary>Name of the header field that stores this section's offset.</summary>
    public string OffsetFieldName => Name + "Offset";

    public string DisplayName => Name.ToUpperInvariant();

    public string Description => Name switch
    {
        "Toc" => "File name TOC: directory, file name, sizes and offset of every file",
        "Itoc" => "ID TOC: file sizes looked up by file ID",
        "Gtoc" => "Group info TOC: groups and attributes that link files together",
        "Etoc" => "Extended TOC: per-file update times",
        "Htoc" => "Hash TOC: hash-based file lookup",
        _ => "Additional table"
    };
}

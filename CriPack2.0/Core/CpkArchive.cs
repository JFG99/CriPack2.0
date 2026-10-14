using System.Text;

namespace CpkToolkit.Core;

/// <summary>
/// Read-only view of a CPK file: header fields, sections (TOC, ITOC, GTOC, ETOC, ...) and file entries.
/// Only the small tables are kept in memory; file data is always read from disk on demand,
/// so archives of many gigabytes are fine.
/// </summary>
public sealed class CpkArchive
{
    private const long DefaultAlignment = 0x800;

    private readonly IReadOnlyDictionary<string, UtfCell> headerFields;
    private readonly List<CpkEntry> entries = new();
    private readonly List<CpkSection> sections;

    public CpkArchive(string archivePath)
    {
        ArchivePath = Path.GetFullPath(archivePath);

        using var reader = new ArchiveByteReader(ArchivePath);
        ArchiveLength = reader.Length;

        HeaderPacket = CpkPacket.Read(reader, 0, "CPK ");
        headerFields = UtfTable.Parse(HeaderPacket.Data, CpkPacket.PacketHeaderLength).Rows[0];

        ContentOffset = GetHeaderValue("ContentOffset");
        ContentSize = GetHeaderValue("ContentSize");
        long tocOffset = GetHeaderValue("TocOffset");
        long declaredAlignment = GetHeaderValue("Align");
        Alignment = declaredAlignment > 0 ? declaredAlignment : DefaultAlignment;

        // File offsets in the TOC are relative to the lower of the TOC offset and the content offset.
        DataBaseOffset = ContentOffset > 0 && ContentOffset < tocOffset ? ContentOffset : tocOffset;

        sections = FindSections();
        CpkSection tocSection = sections.FirstOrDefault(section => section.Name == "Toc")
            ?? throw new InvalidDataException("This CPK has no filename TOC (ID-only archives are not supported).");

        TocPacket = CpkPacket.Read(reader, tocSection.Offset, "TOC ");
        LoadEntries();
    }

    public string ArchivePath { get; }

    public long ArchiveLength { get; }

    public CpkPacket HeaderPacket { get; }

    public CpkPacket TocPacket { get; }

    public long ContentOffset { get; }

    public long ContentSize { get; }

    public long Alignment { get; }

    /// <summary>The position that the TOC's FileOffset values are relative to.</summary>
    public long DataBaseOffset { get; }

    public IReadOnlyList<CpkEntry> Entries => entries;

    public IReadOnlyList<CpkSection> Sections => sections;

    public IReadOnlyDictionary<string, UtfCell> HeaderFields => headerFields;

    public ArchiveByteReader OpenReader() => new(ArchivePath);

    public long GetHeaderValue(string fieldName) =>
        headerFields.TryGetValue(fieldName, out UtfCell? cell) && cell.Value != null ? (long)cell.AsUInt64() : 0;

    // ---------- Reading data ----------

    public byte[] ReadSectionBytes(CpkSection section)
    {
        using ArchiveByteReader reader = OpenReader();
        var bytes = new byte[section.Size];
        reader.ReadExactly(section.Offset, bytes);
        return bytes;
    }

    public CpkPacket ReadSectionPacket(CpkSection section) => CpkPacket.FromBytes(ReadSectionBytes(section));

    /// <summary>Reads an entry's stored bytes into memory. Only suitable for entries below 2 GB.</summary>
    public byte[] ReadEntryRawBytes(ArchiveByteReader reader, CpkEntry entry)
    {
        if (entry.StoredSize > Array.MaxLength)
            throw new NotSupportedException(
                $"'{entry.FullPath}' is {ByteSizeFormatter.Format(entry.StoredSize)} and cannot be loaded into memory.");

        var data = new byte[entry.StoredSize];
        reader.ReadExactly(entry.AbsoluteOffset, data);
        return data;
    }

    /// <summary>Reads an entry and decompresses it when it is stored compressed.</summary>
    public byte[] ReadEntryDecompressed(ArchiveByteReader reader, CpkEntry entry)
    {
        byte[] rawBytes = ReadEntryRawBytes(reader, entry);
        if (!entry.IsCompressed)
            return rawBytes;

        try
        {
            return LaylaDecompressor.Decompress(rawBytes);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"'{entry.FullPath}' (stored {entry.StoredSize:N0} bytes at 0x{entry.AbsoluteOffset:X}, expected {entry.ExtractSize:N0} bytes): {exception.Message}",
                exception);
        }
    }

    // ---------- Descriptions for the user interface ----------

    public string BuildSummaryText()
    {
        long totalExtractSize = entries.Sum(entry => entry.ExtractSize);
        long totalStoredSize = entries.Sum(entry => entry.StoredSize);
        int compressedCount = entries.Count(entry => entry.IsCompressed);
        double storedRatio = totalExtractSize > 0 ? (double)totalStoredSize / totalExtractSize : 1;

        string versionText = headerFields.ContainsKey("Version")
            ? $"Ver.{GetHeaderValue("Version")}, Rev.{GetHeaderValue("Revision")}"
            : "(not stored in header)";

        var text = new StringBuilder();
        text.AppendLine($"File:                  {ArchivePath}");
        text.AppendLine($"Archive size:          {ArchiveLength:N0} bytes ({ByteSizeFormatter.Format(ArchiveLength)})");
        text.AppendLine($"File format version:   {versionText}");
        text.AppendLine($"Data alignment:        {Alignment:N0}");
        text.AppendLine($"Content offset:        0x{ContentOffset:X}");
        text.AppendLine($"Data base offset:      0x{DataBaseOffset:X}");
        text.AppendLine($"Content files:         {entries.Count:N0}");
        text.AppendLine($"Compressed files:      {compressedCount:N0}");
        text.AppendLine($"Content file size:     {totalExtractSize:N0} bytes ({ByteSizeFormatter.Format(totalExtractSize)})");
        text.AppendLine($"Compressed file size:  {totalStoredSize:N0} bytes ({storedRatio:P2})");
        text.AppendLine($"Largest file:          {ByteSizeFormatter.Format(entries.Count > 0 ? entries.Max(entry => entry.StoredSize) : 0)}");
        text.AppendLine($"Files with directory:  {entries.Count(entry => !string.IsNullOrEmpty(entry.DirectoryName)):N0}");
        text.AppendLine($"Files with ID column:  {entries.Count(entry => entry.Id.HasValue):N0}");
        text.AppendLine();
        text.AppendLine("Sections (in file order):");
        foreach (CpkSection section in sections)
        {
            text.AppendLine($"  {section.DisplayName,-6} 0x{section.Offset:X8}  {section.Size,12:N0} bytes   {section.Description}");
        }

        return text.ToString();
    }

    public string DescribeSection(CpkSection section)
    {
        var text = new StringBuilder();
        text.AppendLine($"Section:        {section.DisplayName} - {section.Description}");
        text.AppendLine($"Offset:         0x{section.Offset:X} ({section.Offset:N0})");
        text.AppendLine($"Size:           {section.Size:N0} bytes");

        CpkPacket packet = ReadSectionPacket(section);
        text.AppendLine($"Packet magic:   \"{packet.Magic}\"");
        text.AppendLine($"Encrypted:      {(packet.IsEncrypted ? "yes (XOR obfuscated)" : "no")}");
        text.AppendLine($"Payload bytes:  {packet.PayloadLength:N0}");
        text.AppendLine();

        UtfTable table;
        try
        {
            table = UtfTable.Parse(packet.Data, CpkPacket.PacketHeaderLength);
        }
        catch (Exception exception) when (exception is InvalidDataException or IndexOutOfRangeException or OverflowException)
        {
            text.AppendLine("The payload could not be read as a @UTF table.");
            return text.ToString();
        }

        AppendTableDescription(text, table, packet.Data, indent: string.Empty);
        return text.ToString();
    }

    private static void AppendTableDescription(StringBuilder text, UtfTable table, byte[] buffer, string indent)
    {
        const int MaximumSampleRows = 5;

        text.AppendLine($"{indent}@UTF table \"{table.Name}\": {table.Rows.Count:N0} rows, {table.Columns.Count} columns");
        foreach (UtfColumnDefinition column in table.Columns)
        {
            string constantText = column.Storage == UtfColumnStorage.Constant ? $" = {column.ConstantValue}" : string.Empty;
            text.AppendLine($"{indent}  {column.Name,-20} {column.Type,-7} {column.Storage}{constantText}");
        }

        for (int rowIndex = 0; rowIndex < Math.Min(MaximumSampleRows, table.Rows.Count); rowIndex++)
        {
            text.AppendLine();
            text.AppendLine($"{indent}Row {rowIndex}:");
            foreach (KeyValuePair<string, UtfCell> pair in table.Rows[rowIndex])
            {
                text.AppendLine($"{indent}  {pair.Key,-20} {pair.Value.ToDisplayString()}");
            }
        }

        // Some tables (such as the ITOC) carry further @UTF tables inside their data blobs.
        if (table.Rows.Count == 0)
            return;

        foreach (KeyValuePair<string, UtfCell> pair in table.Rows[0])
        {
            if (pair.Value.Value is UtfDataBlob blob && blob.Length > 0 && UtfTable.HasUtfMagic(buffer, blob.Offset))
            {
                text.AppendLine();
                text.AppendLine($"{indent}Nested table in column \"{pair.Key}\":");
                try
                {
                    UtfTable nestedTable = UtfTable.Parse(buffer, blob.Offset);
                    AppendTableDescription(text, nestedTable, buffer, indent + "    ");
                }
                catch (Exception exception) when (exception is InvalidDataException or IndexOutOfRangeException or OverflowException)
                {
                    text.AppendLine($"{indent}    (could not be parsed)");
                }
            }
        }
    }

    // ---------- Validation ----------

    /// <summary>Checks that sections and file data lie inside the file and do not overlap.</summary>
    public IReadOnlyList<string> ValidateLayout()
    {
        const int MaximumReportedProblems = 50;
        var problems = new List<string>();

        void Report(string message)
        {
            if (problems.Count < MaximumReportedProblems)
                problems.Add(message);
        }

        foreach (CpkSection section in sections)
        {
            if (section.EndOffset > ArchiveLength)
                Report($"{section.DisplayName} ends at 0x{section.EndOffset:X}, beyond the end of the file (0x{ArchiveLength:X}).");
        }

        List<CpkSection> sectionsByOffset = sections.OrderBy(section => section.Offset).ToList();
        for (int index = 1; index < sectionsByOffset.Count; index++)
        {
            if (sectionsByOffset[index].Offset < sectionsByOffset[index - 1].EndOffset)
                Report($"{sectionsByOffset[index - 1].DisplayName} and {sectionsByOffset[index].DisplayName} overlap.");
        }

        long highestEntryEnd = 0;
        CpkEntry? previousEntry = null;
        int absoluteAlignedCount = 0;
        int contentRelativeAlignedCount = 0;
        int baseRelativeAlignedCount = 0;
        foreach (CpkEntry entry in entries.OrderBy(e => e.AbsoluteOffset).ThenBy(e => e.StoredSize))
        {
            long entryEnd = entry.AbsoluteOffset + entry.StoredSize;
            if (entryEnd > ArchiveLength)
                Report($"{entry.FullPath} ends at 0x{entryEnd:X}, beyond the end of the file.");
            if (entry.AbsoluteOffset < ContentOffset)
                Report($"{entry.FullPath} starts at 0x{entry.AbsoluteOffset:X}, before the content offset 0x{ContentOffset:X}.");
            if (entry.AbsoluteOffset % Alignment == 0)
                absoluteAlignedCount++;
            if ((entry.AbsoluteOffset - ContentOffset) % Alignment == 0)
                contentRelativeAlignedCount++;
            if ((entry.AbsoluteOffset - DataBaseOffset) % Alignment == 0)
                baseRelativeAlignedCount++;

            bool sharesDataWithPrevious = previousEntry != null &&
                                          previousEntry.AbsoluteOffset == entry.AbsoluteOffset &&
                                          previousEntry.StoredSize == entry.StoredSize;
            if (entry.AbsoluteOffset < highestEntryEnd && !sharesDataWithPrevious)
                Report($"{entry.FullPath} overlaps the data of another file.");

            highestEntryEnd = Math.Max(highestEntryEnd, entryEnd);
            previousEntry = entry;
        }

        // One summary line instead of one line per file: it shows which alignment convention the archive follows.
        if (entries.Count > 0 && absoluteAlignedCount != entries.Count)
        {
            Report(
                $"Alignment {Alignment:N0}: {absoluteAlignedCount:N0} of {entries.Count:N0} files start on an absolute boundary; " +
                $"{contentRelativeAlignedCount:N0} are aligned relative to the content offset (0x{ContentOffset:X}); " +
                $"{baseRelativeAlignedCount:N0} relative to the TOC base (0x{DataBaseOffset:X}). " +
                "This is informational unless the game itself requires absolute alignment.");
        }

        foreach (CpkSection section in sections.Where(s => s.Offset >= ContentOffset && s.Name != "Toc"))
        {
            if (section.Offset < highestEntryEnd)
                Report($"{section.DisplayName} at 0x{section.Offset:X} overlaps file data (data ends at 0x{highestEntryEnd:X}).");
        }

        return problems;
    }

    // ---------- Parsing helpers ----------

    /// <summary>Finds every "XxxOffset"/"XxxSize" pair in the CPK header (TOC, ITOC, GTOC, ETOC, ...).</summary>
    private List<CpkSection> FindSections()
    {
        var found = new List<CpkSection>();
        foreach (KeyValuePair<string, UtfCell> pair in headerFields)
        {
            const string OffsetSuffix = "Offset";
            if (!pair.Key.EndsWith(OffsetSuffix, StringComparison.Ordinal) || pair.Key == "ContentOffset")
                continue;

            string name = pair.Key[..^OffsetSuffix.Length];
            if (!headerFields.TryGetValue(name + "Size", out UtfCell? sizeCell) || pair.Value.Value == null || sizeCell.Value == null)
                continue;

            long offset = (long)pair.Value.AsUInt64();
            long size = (long)sizeCell.AsUInt64();
            if (offset > 0 && size > 0)
                found.Add(new CpkSection { Name = name, Offset = offset, Size = size });
        }

        return found.OrderBy(section => section.Offset).ToList();
    }

    private void LoadEntries()
    {
        UtfTable tocTable = UtfTable.Parse(TocPacket.Data, CpkPacket.PacketHeaderLength);
        for (int rowIndex = 0; rowIndex < tocTable.Rows.Count; rowIndex++)
        {
            IReadOnlyDictionary<string, UtfCell> row = tocTable.Rows[rowIndex];
            UtfCell storedSizeCell = GetRequiredCell(row, "FileSize");
            UtfCell extractSizeCell = GetRequiredCell(row, "ExtractSize");
            UtfCell fileOffsetCell = GetRequiredCell(row, "FileOffset");

            long? fileId = null;
            if (row.TryGetValue("ID", out UtfCell? idCell) && idCell.Value != null)
                fileId = (long)idCell.AsUInt64();

            string? directoryName = row.TryGetValue("DirName", out UtfCell? directoryCell)
                ? directoryCell.Value as string
                : null;

            entries.Add(new CpkEntry
            {
                TocRowIndex = rowIndex,
                DirectoryName = directoryName,
                FileName = GetRequiredCell(row, "FileName").Value as string ?? string.Empty,
                StoredSize = (long)storedSizeCell.AsUInt64(),
                ExtractSize = (long)extractSizeCell.AsUInt64(),
                AbsoluteOffset = DataBaseOffset + (long)fileOffsetCell.AsUInt64(),
                Id = fileId,
                StoredSizeCell = storedSizeCell,
                ExtractSizeCell = extractSizeCell,
                FileOffsetCell = fileOffsetCell
            });
        }
    }

    private static UtfCell GetRequiredCell(IReadOnlyDictionary<string, UtfCell> row, string columnName) =>
        row.TryGetValue(columnName, out UtfCell? cell)
            ? cell
            : throw new InvalidDataException($"The TOC has no '{columnName}' column.");
}

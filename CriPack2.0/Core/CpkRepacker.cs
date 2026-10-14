using System.Security.Cryptography;

namespace CpkToolkit.Core;

/// <summary>
/// Rebuilds a CPK with some files replaced by files of any size.
///
/// The new layout is planned first, then everything is written in one sequential pass:
/// head area (CPK header, TOC and any sections before the data, patched), file data (streamed, never held in
/// memory as a whole), then the sections that originally followed the data (GTOC, ETOC, ...) moved behind it.
/// </summary>
public sealed class CpkRepacker
{
    private const int CopyBufferLength = 4 * 1024 * 1024;
    private const int OutputStreamBufferLength = 1024 * 1024;
    private const long MaximumHeadAreaLength = 256L * 1024 * 1024;

    private readonly CpkArchive archive;

    public CpkRepacker(CpkArchive archive)
    {
        this.archive = archive;
    }

    /// <param name="outputPath">Where the new CPK is written. Must differ from the original.</param>
    /// <param name="getReplacementPath">Returns the replacement file for an entry, or null to keep its data.</param>
    /// <param name="fillPaddingWithRandomBytes">
    /// Fill the alignment gaps between files with random bytes, as CRI's own packer does, instead of zeros.
    /// </param>
    public CpkRepackReport Repack(
        string outputPath,
        Func<CpkEntry, string?> getReplacementPath,
        IProgress<CpkOperationProgress>? progress,
        CancellationToken cancellationToken,
        bool fillPaddingWithRandomBytes = false)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullOutputPath, archive.ArchivePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output file must be different from the original CPK.");

        CpkSection tocSection = archive.Sections.First(section => section.Name == "Toc");
        foreach (CpkSection section in archive.Sections.Where(s => s.Offset < archive.ContentOffset))
        {
            if (section.EndOffset > archive.ContentOffset)
                throw new NotSupportedException($"Section {section.DisplayName} overlaps the file data; this layout is not supported.");
        }

        if (archive.ContentOffset <= 0 || archive.ContentOffset > MaximumHeadAreaLength)
            throw new NotSupportedException("The CPK header has no usable ContentOffset.");

        var notes = new List<string>();

        // ---------- 1. Plan the new layout ----------
        List<CpkRepackPlanEntry> plan = BuildPlan(getReplacementPath, progress, cancellationToken);

        long cursor = archive.ContentOffset;
        var sharedDataOffsets = new Dictionary<(long Offset, long Size), long>();
        foreach (CpkRepackPlanEntry item in plan)
        {
            var originalDataKey = (item.Entry.AbsoluteOffset, item.Entry.StoredSize);
            if (item.ReplacementPath == null && sharedDataOffsets.TryGetValue(originalDataKey, out long sharedOffset))
            {
                item.NewAbsoluteOffset = sharedOffset;
                item.WritesOwnData = false;
                continue;
            }

            cursor = AlignUp(cursor, archive.Alignment);
            item.NewAbsoluteOffset = cursor;
            cursor += item.NewStoredSize;

            if (item.ReplacementPath == null)
                sharedDataOffsets[originalDataKey] = item.NewAbsoluteOffset;
        }

        long lastDataEnd = cursor;
        long alignedContentEnd = AlignUp(lastDataEnd, archive.Alignment);

        // Sections that followed the data move behind the new data; sections before it stay where they are.
        var relocatedSectionOffsets = new List<(CpkSection Section, long NewOffset)>();
        long newFileLength = alignedContentEnd;
        foreach (CpkSection section in archive.Sections.Where(s => s.Name != "Toc" && s.Offset >= archive.ContentOffset))
        {
            long newOffset = AlignUp(newFileLength, archive.Alignment);
            relocatedSectionOffsets.Add((section, newOffset));
            newFileLength = newOffset + section.Size;
        }

        DiskSpaceChecker.EnsureFreeSpace(fullOutputPath, newFileLength);

        // ---------- 2. Patch copies of the header, TOC and other sections ----------
        CpkPacket headerPacket = archive.HeaderPacket.CloneForPatching();
        CpkPacket tocPacket = archive.TocPacket.CloneForPatching();

        foreach (CpkRepackPlanEntry item in plan)
        {
            UtfTable.PatchCell(tocPacket.Data, item.Entry.StoredSizeCell, (ulong)item.NewStoredSize);
            UtfTable.PatchCell(tocPacket.Data, item.Entry.ExtractSizeCell, (ulong)item.NewExtractSize);
            UtfTable.PatchCell(tocPacket.Data, item.Entry.FileOffsetCell, (ulong)(item.NewAbsoluteOffset - archive.DataBaseOffset));
        }

        var sizesByFileId = new Dictionary<long, CpkEntrySizes>();
        foreach (CpkRepackPlanEntry item in plan.Where(p => p.Entry.Id.HasValue))
        {
            sizesByFileId[item.Entry.Id!.Value] = new CpkEntrySizes(item.NewStoredSize, item.NewExtractSize);
        }

        // Raw bytes of every section except the TOC, with the ITOC's sizes updated.
        var sectionBytes = new Dictionary<CpkSection, byte[]>();
        foreach (CpkSection section in archive.Sections.Where(s => s.Name != "Toc"))
        {
            if (section.Name == "Itoc")
            {
                CpkPacket itocPacket = archive.ReadSectionPacket(section);
                int updatedRows = CpkItocUpdater.UpdateFileSizes(itocPacket, sizesByFileId);
                if (updatedRows == 0)
                    notes.Add("The ITOC was found but none of its IDs matched the TOC's ID column; its sizes may be stale.");
                sectionBytes[section] = itocPacket.GetBytesForWriting();
            }
            else
            {
                sectionBytes[section] = archive.ReadSectionBytes(section);
            }
        }

        PatchHeader(headerPacket, plan, lastDataEnd, alignedContentEnd, relocatedSectionOffsets, newFileLength, notes);

        // ---------- 3. Build the head area (everything before the file data) ----------
        byte[] headArea = ReadHeadArea();
        Overlay(headArea, 0, headerPacket.GetBytesForWriting(), "CPK header");
        Overlay(headArea, tocSection.Offset, tocPacket.GetBytesForWriting(), "TOC");
        foreach (CpkSection section in archive.Sections.Where(s => s.Name != "Toc" && s.Offset < archive.ContentOffset))
        {
            Overlay(headArea, section.Offset, sectionBytes[section], section.DisplayName);
        }

        // ---------- 4. Write everything in one sequential pass ----------
        long totalBytesToWrite = newFileLength;
        long bytesWritten = 0;
        int itemsWritten = 0;

        void ReportProgress(string message) =>
            progress?.Report(new CpkOperationProgress(message, bytesWritten, totalBytesToWrite, itemsWritten, plan.Count));

        var copyBuffer = new byte[CopyBufferLength];
        bool completed = false;
        try
        {
            using (var output = new FileStream(
                       fullOutputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       OutputStreamBufferLength, FileOptions.SequentialScan))
            using (ArchiveByteReader originalReader = archive.OpenReader())
            {
                output.Write(headArea);
                long position = headArea.Length;
                bytesWritten = position;

                foreach (CpkRepackPlanEntry item in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!item.WritesOwnData)
                    {
                        itemsWritten++;
                        continue;
                    }

                    WritePadding(output, item.NewAbsoluteOffset - position, fillPaddingWithRandomBytes);
                    bytesWritten += item.NewAbsoluteOffset - position;
                    position = item.NewAbsoluteOffset;

                    string message = $"Writing {item.Entry.FullPath}";
                    ReportProgress(message);

                    void OnChunkCopied(int copiedBytes)
                    {
                        bytesWritten += copiedBytes;
                        ReportProgress(message);
                    }

                    if (item.ReplacementPath != null)
                    {
                        CopyReplacementFile(item, output, copyBuffer, cancellationToken, OnChunkCopied);
                    }
                    else
                    {
                        originalReader.CopyRangeTo(
                            output, item.Entry.AbsoluteOffset, item.Entry.StoredSize, copyBuffer,
                            hasher: null, cancellationToken, OnChunkCopied);
                    }

                    position += item.NewStoredSize;
                    itemsWritten++;
                }

                WritePadding(output, alignedContentEnd - position, fillPaddingWithRandomBytes);
                bytesWritten += alignedContentEnd - position;
                position = alignedContentEnd;

                foreach ((CpkSection section, long newOffset) in relocatedSectionOffsets)
                {
                    WritePadding(output, newOffset - position, fillPaddingWithRandomBytes);
                    byte[] bytes = sectionBytes[section];
                    output.Write(bytes);
                    position = newOffset + bytes.Length;
                    bytesWritten = position;
                }

                output.Flush();
                if (output.Length != newFileLength)
                    throw new InvalidOperationException(
                        $"Internal error: expected {newFileLength:N0} bytes but wrote {output.Length:N0}.");
            }

            completed = true;
        }
        finally
        {
            if (!completed)
                TryDeleteFile(fullOutputPath);
        }

        ReportProgress("Repack finished");

        return new CpkRepackReport
        {
            OutputPath = fullOutputPath,
            ReplacedEntryCount = plan.Count(p => p.ReplacementPath != null),
            KeptEntryCount = plan.Count(p => p.ReplacementPath == null),
            SharedDataEntryCount = plan.Count(p => !p.WritesOwnData),
            OutputLength = newFileLength,
            Notes = notes
        };
    }

    // ---------- Planning ----------

    private List<CpkRepackPlanEntry> BuildPlan(
        Func<CpkEntry, string?> getReplacementPath,
        IProgress<CpkOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = new List<CpkRepackPlanEntry>(archive.Entries.Count);
        int scanned = 0;

        // Entries keep the physical order they have in the original archive (OrderBy is stable).
        foreach (CpkEntry entry in archive.Entries.OrderBy(e => e.AbsoluteOffset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CpkOperationProgress($"Scanning {entry.FullPath}", 0, 0, scanned, archive.Entries.Count));
            scanned++;

            string? replacementPath = getReplacementPath(entry);
            long newStoredSize;
            long newExtractSize;
            if (replacementPath != null)
            {
                long replacementLength = new FileInfo(replacementPath).Length;
                if (replacementLength > uint.MaxValue)
                    throw new InvalidOperationException(
                        $"'{entry.FullPath}' would be {ByteSizeFormatter.Format(replacementLength)}; CPK file sizes are limited to 4 GB.");
                newStoredSize = replacementLength;
                newExtractSize = replacementLength;   // replacements are stored uncompressed
            }
            else
            {
                newStoredSize = entry.StoredSize;
                newExtractSize = entry.ExtractSize;
            }

            plan.Add(new CpkRepackPlanEntry
            {
                Entry = entry,
                ReplacementPath = replacementPath,
                NewStoredSize = newStoredSize,
                NewExtractSize = newExtractSize
            });
        }

        return plan;
    }

    // ---------- Header patching ----------

    private void PatchHeader(
        CpkPacket headerPacket,
        List<CpkRepackPlanEntry> plan,
        long lastDataEnd,
        long alignedContentEnd,
        List<(CpkSection Section, long NewOffset)> relocatedSectionOffsets,
        long newFileLength,
        List<string> notes)
    {
        // ContentSize: follow whichever convention the original header used (with or without end padding).
        long originalLastDataEnd = archive.Entries.Count > 0
            ? archive.Entries.Max(entry => entry.AbsoluteOffset + entry.StoredSize)
            : archive.ContentOffset;
        long originalContentEnd = archive.ContentOffset + archive.ContentSize;
        bool excludesEndPadding = originalContentEnd == originalLastDataEnd &&
                                  originalLastDataEnd != AlignUp(originalLastDataEnd, archive.Alignment);
        if (!excludesEndPadding && originalContentEnd != AlignUp(originalLastDataEnd, archive.Alignment))
            notes.Add("ContentSize did not match the original data end; the aligned data end was used.");

        long newContentEnd = excludesEndPadding ? lastDataEnd : alignedContentEnd;
        PatchRequiredHeaderField(headerPacket, "ContentSize", (ulong)(newContentEnd - archive.ContentOffset));

        foreach ((CpkSection section, long newOffset) in relocatedSectionOffsets)
        {
            PatchRequiredHeaderField(headerPacket, section.OffsetFieldName, (ulong)newOffset);
        }

        ulong originalStoredSum = 0;
        ulong originalExtractSum = 0;
        ulong newStoredSum = 0;
        ulong newExtractSum = 0;
        foreach (CpkRepackPlanEntry item in plan)
        {
            originalStoredSum += (ulong)item.Entry.StoredSize;
            originalExtractSum += (ulong)item.Entry.ExtractSize;
            newStoredSum += (ulong)item.NewStoredSize;
            newExtractSum += (ulong)item.NewExtractSize;
        }

        // The header's two "Enabled" totals are not simple sums of the TOC columns. On the archive these formulas
        // were derived from, EnabledPackedSize was exactly twice the total extracted size and EnabledDataSize was
        // the total extracted size plus the total stored size. Each is only rewritten when the formula reproduces
        // the ORIGINAL header value exactly, so a CPK that follows a different rule is left untouched.
        PatchTotalIfMatchesExpected(headerPacket, "EnabledPackedSize",
            expectedOriginalValue: 2 * originalExtractSum, newValue: 2 * newExtractSum, notes);
        PatchTotalIfMatchesExpected(headerPacket, "EnabledDataSize",
            expectedOriginalValue: originalExtractSum + originalStoredSum, newValue: newExtractSum + newStoredSum, notes);
        PatchTotalIfMatchesExpected(headerPacket, "FileSize",
            expectedOriginalValue: (ulong)archive.ArchiveLength, newValue: (ulong)newFileLength, notes);
    }

    private void PatchRequiredHeaderField(CpkPacket headerPacket, string fieldName, ulong newValue)
    {
        if (!archive.HeaderFields.TryGetValue(fieldName, out UtfCell? cell) || cell.Value == null)
            throw new InvalidDataException($"The CPK header has no '{fieldName}' field.");
        UtfTable.PatchCell(headerPacket.Data, cell, newValue);
    }

    /// <summary>
    /// Updates a header total only if the original value equals what the formula gives for the original archive,
    /// which proves the formula is the one this CPK was built with.
    /// </summary>
    private void PatchTotalIfMatchesExpected(
        CpkPacket headerPacket, string fieldName, ulong expectedOriginalValue, ulong newValue, List<string> notes)
    {
        if (!archive.HeaderFields.TryGetValue(fieldName, out UtfCell? cell) || cell.Value == null || !cell.IsPatchable)
            return;

        ulong originalValue = cell.AsUInt64();
        if (originalValue == expectedOriginalValue)
            UtfTable.PatchCell(headerPacket.Data, cell, newValue);
        else if (originalValue != 0)
            notes.Add($"Header field {fieldName} ({originalValue:N0}) does not match the formula this tool uses " +
                      $"({expectedOriginalValue:N0}); it was left unchanged.");
    }

    // ---------- Writing helpers ----------

    private byte[] ReadHeadArea()
    {
        var headArea = new byte[archive.ContentOffset];
        using ArchiveByteReader reader = archive.OpenReader();
        reader.ReadExactly(0, headArea);
        return headArea;
    }

    private static void Overlay(byte[] headArea, long offset, byte[] bytes, string description)
    {
        if (offset < 0 || offset + bytes.Length > headArea.Length)
            throw new NotSupportedException($"{description} does not fit inside the area before the file data.");
        Buffer.BlockCopy(bytes, 0, headArea, (int)offset, bytes.Length);
    }

    private static void CopyReplacementFile(
        CpkRepackPlanEntry item,
        Stream output,
        byte[] copyBuffer,
        CancellationToken cancellationToken,
        Action<int> onChunkCopied)
    {
        using var source = new FileStream(
            item.ReplacementPath!, FileMode.Open, FileAccess.Read, FileShare.Read,
            OutputStreamBufferLength, FileOptions.SequentialScan);

        long copiedTotal = 0;
        int bytesRead;
        while ((bytesRead = source.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(copyBuffer, 0, bytesRead);
            copiedTotal += bytesRead;
            onChunkCopied(bytesRead);
        }

        if (copiedTotal != item.NewStoredSize)
            throw new InvalidOperationException(
                $"'{item.ReplacementPath}' changed size while the archive was being written ({item.NewStoredSize:N0} -> {copiedTotal:N0} bytes).");
    }

    private static void WritePadding(Stream output, long byteCount, bool useRandomBytes)
    {
        if (byteCount < 0)
            throw new InvalidOperationException("Internal error: negative padding length.");

        var paddingBuffer = new byte[(int)Math.Min(byteCount, 64 * 1024)];
        long remaining = byteCount;
        while (remaining > 0)
        {
            int chunkLength = (int)Math.Min(paddingBuffer.Length, remaining);
            if (useRandomBytes)
                RandomNumberGenerator.Fill(paddingBuffer.AsSpan(0, chunkLength));
            output.Write(paddingBuffer, 0, chunkLength);
            remaining -= chunkLength;
        }
    }

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

using System.Security.Cryptography;

namespace CpkToolkit.Core;

/// <summary>
/// Compares two CPK archives file by file (matched by path, ignoring upper/lower case) and reports which
/// files differ. Sizes are compared first, which is instant; only files with equal sizes need their bytes
/// hashed. Compressed files are decompressed before comparing, so a file that was only recompressed
/// differently is reported as "storage only" and not as a content change.
/// </summary>
public sealed class CpkComparer
{
    private const int ReadBufferLength = 4 * 1024 * 1024;

    private readonly CpkArchive firstArchive;
    private readonly CpkArchive secondArchive;

    public CpkComparer(CpkArchive firstArchive, CpkArchive secondArchive)
    {
        this.firstArchive = firstArchive;
        this.secondArchive = secondArchive;
    }

    /// <param name="verifyContents">
    /// True to hash every file whose sizes match (accurate, reads both archives completely).
    /// False to compare sizes only (instant, but same-size edits are not detected).
    /// </param>
    public CpkComparisonResult Compare(
        bool verifyContents,
        IProgress<CpkOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var secondEntriesByPath = new Dictionary<string, CpkEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (CpkEntry secondEntry in secondArchive.Entries)
            secondEntriesByPath.TryAdd(secondEntry.FullPath, secondEntry);

        var differentRows = new List<CpkComparisonRow>();
        var pairsWithEqualSize = new List<(CpkEntry First, CpkEntry Second)>();
        var pathsFoundInFirst = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CpkEntry firstEntry in firstArchive.Entries)
        {
            if (!pathsFoundInFirst.Add(firstEntry.FullPath))
                continue;   // duplicate path inside the first archive: only the first one is compared

            if (!secondEntriesByPath.TryGetValue(firstEntry.FullPath, out CpkEntry? secondEntry))
            {
                differentRows.Add(new CpkComparisonRow
                {
                    FullPath = firstEntry.FullPath,
                    Status = CpkComparisonStatus.OnlyInFirst,
                    FirstEntry = firstEntry
                });
            }
            else if (firstEntry.ExtractSize != secondEntry.ExtractSize)
            {
                differentRows.Add(new CpkComparisonRow
                {
                    FullPath = firstEntry.FullPath,
                    Status = CpkComparisonStatus.ContentChanged,
                    FirstEntry = firstEntry,
                    SecondEntry = secondEntry
                });
            }
            else
            {
                pairsWithEqualSize.Add((firstEntry, secondEntry));
            }
        }

        foreach (CpkEntry secondEntry in secondEntriesByPath.Values)
        {
            if (!pathsFoundInFirst.Contains(secondEntry.FullPath))
            {
                differentRows.Add(new CpkComparisonRow
                {
                    FullPath = secondEntry.FullPath,
                    Status = CpkComparisonStatus.OnlyInSecond,
                    SecondEntry = secondEntry
                });
            }
        }

        int identicalCount = 0;
        int sameSizeUnverifiedCount = 0;

        if (!verifyContents)
        {
            sameSizeUnverifiedCount = pairsWithEqualSize.Count;
        }
        else
        {
            VerifyPairs(pairsWithEqualSize, differentRows, ref identicalCount, progress, cancellationToken);
        }

        differentRows.Sort((left, right) =>
        {
            int byStatus = left.Status.CompareTo(right.Status);
            return byStatus != 0 ? byStatus : string.Compare(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        });

        return new CpkComparisonResult
        {
            DifferentRows = differentRows,
            IdenticalCount = identicalCount,
            SameSizeUnverifiedCount = sameSizeUnverifiedCount,
            FirstArchiveFileCount = firstArchive.Entries.Count,
            SecondArchiveFileCount = secondArchive.Entries.Count,
            ContentsWereVerified = verifyContents
        };
    }

    private void VerifyPairs(
        List<(CpkEntry First, CpkEntry Second)> pairs,
        List<CpkComparisonRow> differentRows,
        ref int identicalCount,
        IProgress<CpkOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Reading in archive order keeps the disk heads moving forward instead of jumping around.
        pairs.Sort((left, right) => left.First.AbsoluteOffset.CompareTo(right.First.AbsoluteOffset));

        long totalBytes = pairs.Sum(pair => pair.First.StoredSize + pair.Second.StoredSize);
        long bytesProcessed = 0;
        int itemsProcessed = 0;
        var readBuffer = new byte[ReadBufferLength];

        void Report(string message) =>
            progress?.Report(new CpkOperationProgress(message, bytesProcessed, totalBytes, itemsProcessed, pairs.Count));

        using ArchiveByteReader firstReader = firstArchive.OpenReader();
        using ArchiveByteReader secondReader = secondArchive.OpenReader();

        foreach ((CpkEntry firstEntry, CpkEntry secondEntry) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report($"Comparing {firstEntry.FullPath}");

            void OnChunkRead(int chunkLength)
            {
                bytesProcessed += chunkLength;
                Report($"Comparing {firstEntry.FullPath}");
            }

            CpkComparisonStatus status = CompareEntryContents(
                firstEntry, secondEntry, firstReader, secondReader, readBuffer, cancellationToken, OnChunkRead);

            if (status == CpkComparisonStatus.Identical)
            {
                identicalCount++;
            }
            else
            {
                differentRows.Add(new CpkComparisonRow
                {
                    FullPath = firstEntry.FullPath,
                    Status = status,
                    FirstEntry = firstEntry,
                    SecondEntry = secondEntry
                });
            }

            itemsProcessed++;
        }

        Report("Comparison finished");
    }

    private CpkComparisonStatus CompareEntryContents(
        CpkEntry firstEntry,
        CpkEntry secondEntry,
        ArchiveByteReader firstReader,
        ArchiveByteReader secondReader,
        byte[] readBuffer,
        CancellationToken cancellationToken,
        Action<int> onChunkRead)
    {
        bool storedTheSameWay =
            firstEntry.IsCompressed == secondEntry.IsCompressed &&
            firstEntry.StoredSize == secondEntry.StoredSize;

        if (storedTheSameWay)
        {
            string firstStoredHash = HashStoredBytes(firstReader, firstEntry, readBuffer, cancellationToken, onChunkRead);
            string secondStoredHash = HashStoredBytes(secondReader, secondEntry, readBuffer, cancellationToken, onChunkRead);

            if (firstStoredHash == secondStoredHash)
                return CpkComparisonStatus.Identical;

            // Uncompressed files with different bytes have different content. Compressed files with
            // different bytes might still decompress to the same data, so they are checked below.
            if (!firstEntry.IsCompressed)
                return CpkComparisonStatus.ContentChanged;
        }

        string firstContentHash;
        string secondContentHash;
        try
        {
            firstContentHash = HashContent(firstArchive, firstReader, firstEntry, readBuffer, cancellationToken, onChunkRead);
            secondContentHash = HashContent(secondArchive, secondReader, secondEntry, readBuffer, cancellationToken, onChunkRead);
        }
        catch (InvalidDataException)
        {
            return CpkComparisonStatus.CouldNotCompare;
        }

        return firstContentHash == secondContentHash
            ? CpkComparisonStatus.StorageOnlyChanged
            : CpkComparisonStatus.ContentChanged;
    }

    private static string HashStoredBytes(
        ArchiveByteReader reader,
        CpkEntry entry,
        byte[] readBuffer,
        CancellationToken cancellationToken,
        Action<int> onChunkRead)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = entry.StoredSize;
        long position = entry.AbsoluteOffset;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = (int)Math.Min(readBuffer.Length, remaining);
            reader.ReadExactly(position, readBuffer.AsSpan(0, chunkLength));
            hasher.AppendData(readBuffer, 0, chunkLength);
            position += chunkLength;
            remaining -= chunkLength;
            onChunkRead(chunkLength);
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    /// <summary>Hash of the file's real content: decompressed first when the file is stored compressed.</summary>
    private static string HashContent(
        CpkArchive archive,
        ArchiveByteReader reader,
        CpkEntry entry,
        byte[] readBuffer,
        CancellationToken cancellationToken,
        Action<int> onChunkRead)
    {
        if (!entry.IsCompressed)
            return HashStoredBytes(reader, entry, readBuffer, cancellationToken, onChunkRead);

        byte[] decompressedData = archive.ReadEntryDecompressed(reader, entry);
        onChunkRead((int)Math.Min(entry.StoredSize, int.MaxValue));
        return Convert.ToHexString(SHA256.HashData(decompressedData));
    }
}

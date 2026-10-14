using System.Security.Cryptography;

namespace CpkToolkit.Core;

/// <summary>
/// Extracts entries to a folder. Uncompressed files are streamed in small pieces (so files larger than 1 GB
/// are no problem); compressed files are decompressed in memory. An extraction manifest is written next to
/// the files so a later repack can detect which ones were edited.
/// </summary>
public sealed class CpkExtractor
{
    private const int CopyBufferLength = 4 * 1024 * 1024;
    private const int OutputStreamBufferLength = 1024 * 1024;

    private readonly CpkArchive archive;

    public CpkExtractor(CpkArchive archive)
    {
        this.archive = archive;
    }

    /// <summary>Folder (inside the output folder) that receives the raw bytes of files that could not be decompressed.</summary>
    public const string FailedFilesFolderName = "_failed_decompress";

    /// <returns>
    /// One message per file that could not be decompressed. Such a file does not stop the extraction: its stored
    /// (still compressed) bytes are saved under <see cref="FailedFilesFolderName"/> with the extension ".crilayla".
    /// </returns>
    public IReadOnlyList<string> Extract(
        IReadOnlyList<CpkEntry> entriesToExtract,
        string outputDirectory,
        IProgress<CpkOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long totalBytes = entriesToExtract.Sum(entry => entry.ExtractSize);
        DiskSpaceChecker.EnsureFreeSpace(outputDirectory, totalBytes);
        Directory.CreateDirectory(outputDirectory);

        // Merge with an existing manifest so extracting a few files does not forget the others.
        ExtractionManifest manifest = ExtractionManifest.TryLoad(outputDirectory) ?? new ExtractionManifest();
        manifest.SetSource(Path.GetFileName(archive.ArchivePath), archive.ArchiveLength);

        var failureMessages = new List<string>();
        var copyBuffer = new byte[CopyBufferLength];
        long bytesProcessed = 0;
        int itemsProcessed = 0;

        void ReportProgress(string message) =>
            progress?.Report(new CpkOperationProgress(message, bytesProcessed, totalBytes, itemsProcessed, entriesToExtract.Count));

        try
        {
            using ArchiveByteReader reader = archive.OpenReader();
            foreach (CpkEntry entry in entriesToExtract)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress($"Extracting {entry.FullPath}");

                string destinationPath = ArchivePathResolver.ResolveInsideDirectory(outputDirectory, entry.FullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long writtenLength;

                if (entry.IsCompressed)
                {
                    byte[] decompressedData;
                    try
                    {
                        decompressedData = archive.ReadEntryDecompressed(reader, entry);
                    }
                    catch (InvalidDataException exception)
                    {
                        failureMessages.Add(exception.Message);
                        string failedCopyPath = ArchivePathResolver.ResolveInsideDirectory(
                            outputDirectory, FailedFilesFolderName + "/" + entry.FullPath + ".crilayla");
                        Directory.CreateDirectory(Path.GetDirectoryName(failedCopyPath)!);
                        File.WriteAllBytes(failedCopyPath, archive.ReadEntryRawBytes(reader, entry));
                        bytesProcessed += entry.ExtractSize;
                        itemsProcessed++;
                        continue;
                    }

                    File.WriteAllBytes(destinationPath, decompressedData);
                    hasher.AppendData(decompressedData);
                    writtenLength = decompressedData.Length;
                    bytesProcessed += writtenLength;
                }
                else
                {
                    using var destination = new FileStream(
                        destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        OutputStreamBufferLength, FileOptions.SequentialScan);

                    reader.CopyRangeTo(
                        destination, entry.AbsoluteOffset, entry.StoredSize, copyBuffer, hasher, cancellationToken,
                        copiedBytes =>
                        {
                            bytesProcessed += copiedBytes;
                            ReportProgress($"Extracting {entry.FullPath}");
                        });
                    writtenLength = entry.StoredSize;
                }

                manifest.Set(
                    entry.FullPath,
                    new ExtractionManifestRecord(
                        writtenLength,
                        File.GetLastWriteTimeUtc(destinationPath).Ticks,
                        Convert.ToHexString(hasher.GetHashAndReset())));

                itemsProcessed++;
            }
        }
        finally
        {
            manifest.Save(outputDirectory);   // also saved after a cancel, covering the files that were completed
        }

        ReportProgress("Extraction finished");
        return failureMessages;
    }
}

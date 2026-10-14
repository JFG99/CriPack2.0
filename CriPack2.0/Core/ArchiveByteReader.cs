using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CpkToolkit.Core;

/// <summary>
/// Position-based reader over the archive file. It never holds more than the caller's buffer in memory,
/// so entries larger than 1 GB can be streamed.
/// </summary>
public sealed class ArchiveByteReader : IDisposable
{
    private readonly SafeFileHandle fileHandle;

    public ArchiveByteReader(string path)
    {
        fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
        Length = RandomAccess.GetLength(fileHandle);
    }

    public long Length { get; }

    public void ReadExactly(long offset, Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = RandomAccess.Read(fileHandle, destination[totalRead..], offset + totalRead);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of file while reading at 0x{offset + totalRead:X}.");
            totalRead += read;
        }
    }

    /// <summary>Copies a byte range of the archive to <paramref name="destination"/> in buffer-sized pieces.</summary>
    public void CopyRangeTo(
        Stream destination,
        long sourceOffset,
        long length,
        byte[] buffer,
        IncrementalHash? hasher,
        CancellationToken cancellationToken,
        Action<int>? onChunkCopied)
    {
        long remaining = length;
        long position = sourceOffset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = (int)Math.Min(buffer.Length, remaining);
            ReadExactly(position, buffer.AsSpan(0, chunkLength));
            destination.Write(buffer, 0, chunkLength);
            hasher?.AppendData(buffer, 0, chunkLength);
            position += chunkLength;
            remaining -= chunkLength;
            onChunkCopied?.Invoke(chunkLength);
        }
    }

    public void Dispose() => fileHandle.Dispose();
}

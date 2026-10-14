namespace CpkToolkit.Core;

/// <summary>What a file looked like when it was extracted; used to detect which files were edited later.</summary>
public readonly record struct ExtractionManifestRecord(long Length, long LastWriteUtcTicks, string Sha256Hex);

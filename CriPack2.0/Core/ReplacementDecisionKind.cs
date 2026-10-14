namespace CpkToolkit.Core;

/// <summary>Why a file from the modified folder was, or was not, used to replace an archive entry.</summary>
public enum ReplacementDecisionKind
{
    /// <summary>The folder has no file for this entry; the archive's own data is kept.</summary>
    NoFileInFolder,

    /// <summary>Replaced: no extraction manifest is used, so every file present counts as modified.</summary>
    ReplacedNoManifest,

    /// <summary>Replaced: the manifest has no record of this file.</summary>
    ReplacedNotInManifest,

    /// <summary>
    /// Replaced: the file length differs from the one recorded at extraction. Different lengths can never hash
    /// equal, so no hash is computed; the result is the same as comparing hashes.
    /// </summary>
    ReplacedLengthChanged,

    /// <summary>Replaced: the SHA-256 of the file differs from the one recorded at extraction.</summary>
    ReplacedContentChanged,

    /// <summary>Skipped: the SHA-256 of the file equals the one recorded at extraction (byte-identical).</summary>
    SkippedIdenticalContent
}

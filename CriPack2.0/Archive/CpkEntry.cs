namespace CriPack.Archive
{
    public sealed class CpkEntry
    {
        public string? DirectoryName;
        public string FileName = "";
        public uint FileSize, ExtractSize;
        public long AbsoluteOffset;
        internal UtfCell FileSizeCell = null!, ExtractSizeCell = null!, FileOffsetCell = null!;

        public string FullPath => string.IsNullOrEmpty(DirectoryName) ? FileName : DirectoryName + "/" + FileName;
        public bool IsCompressed => ExtractSize > FileSize;
    }
}

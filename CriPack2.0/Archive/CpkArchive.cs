using System.Text;

namespace CriPack.Archive
{
    public sealed class CpkArchive : IDisposable
    {
        readonly FileStream _stream;
        byte[] _headerPacket, _tocPacket;               // full packets (16-byte header + decrypted @UTF)
        bool _headerEncrypted, _tocEncrypted;
        Dictionary<string, UtfCell> _headerRow;         // the single row of the CpkHeader table

        public List<CpkEntry> Entries = new();
        public long DataBaseOffset, ContentOffset, TocOffset, TocSize, EtocOffset, EtocSize, Alignment;

        public CpkArchive(string path)
        {
            _stream = File.OpenRead(path);

            _headerPacket = ReadPacket(0, "CPK ", out _headerEncrypted);
            _headerRow = UtfTable.Parse(_headerPacket, 16).Rows[0];

            ContentOffset = (long)GetHeaderValue("ContentOffset");
            TocOffset = (long)GetHeaderValue("TocOffset");
            TocSize = (long)GetHeaderValue("TocSize");
            EtocOffset = (long)GetHeaderValue("EtocOffset");
            EtocSize = (long)GetHeaderValue("EtocSize");
            Alignment = (long)(GetHeaderValue("Align") is ulong alignValue && alignValue > 0 ? alignValue : 0x800);
            DataBaseOffset = Math.Min(ContentOffset, TocOffset);   // verify against your archives

            _tocPacket = ReadPacket(TocOffset, "TOC ", out _tocEncrypted);
            foreach (var row in UtfTable.Parse(_tocPacket, 16).Rows)
            {
                var entry = new CpkEntry
                {
                    DirectoryName = row.TryGetValue("DirName", out var directoryCell) ? directoryCell.Value as string : null,
                    FileName = row["FileName"].Value as string ?? "",
                    FileSize = (uint)row["FileSize"].AsUInt64(),
                    ExtractSize = (uint)row["ExtractSize"].AsUInt64(),
                    FileSizeCell = row["FileSize"],
                    ExtractSizeCell = row["ExtractSize"],
                    FileOffsetCell = row["FileOffset"],
                };
                entry.AbsoluteOffset = DataBaseOffset + (long)row["FileOffset"].AsUInt64();
                Entries.Add(entry);
            }
        }

        ulong GetHeaderValue(string columnName) => _headerRow.TryGetValue(columnName, out var cell) && cell.Value != null ? cell.AsUInt64() : 0;

        byte[] ReadPacket(long packetOffset, string expectedMagic, out bool isEncrypted)
        {
            var packetHeader = new byte[16];
            _stream.Position = packetOffset;
            _stream.ReadExactly(packetHeader);

            if (Encoding.ASCII.GetString(packetHeader, 0, 4) != expectedMagic)
                throw new InvalidDataException($"Expected '{expectedMagic}' at 0x{packetOffset:X}");

            int payloadLength = checked((int)BitConverter.ToUInt64(packetHeader, 8));   // packet header is little-endian
            var packet = new byte[16 + payloadLength];
            Array.Copy(packetHeader, packet, 16);
            _stream.ReadExactly(packet, 16, payloadLength);

            isEncrypted = !(packet[16] == '@' && packet[17] == 'U' && packet[18] == 'T' && packet[19] == 'F');
            if (isEncrypted) ApplyXorMask(packet, 16, payloadLength);
            return packet;
        }

        // Symmetric: the same call encrypts and decrypts
        static void ApplyXorMask(byte[] buffer, int startOffset, int length)
        {
            uint mask = 0x655F;
            const uint multiplier = 0x4115;
            for (int i = 0; i < length; i++)
            {
                buffer[startOffset + i] ^= (byte)mask;
                mask *= multiplier;
            }
        }

        public byte[] ReadRaw(CpkEntry entry)
        {
            var rawData = new byte[entry.FileSize];
            _stream.Position = entry.AbsoluteOffset;
            _stream.ReadExactly(rawData);
            return rawData;
        }

        public byte[] Extract(CpkEntry entry)
        {
            var rawData = ReadRaw(entry);
            return entry.IsCompressed ? Layla.Decompress(rawData) : rawData;
        }

        static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

        /// getReplacement returns new (uncompressed) bytes for an entry, or null to keep the original data.
        /// Call this once per CpkArchive instance: it patches the header/TOC buffers in place.
        public void Repack(string outputPath, Func<CpkEntry, byte[]?> getReplacement)
        {
            if (TocOffset + TocSize > ContentOffset)
                throw new NotSupportedException("Expected layout: header, TOC, data, ETOC.");

            using var output = File.Create(outputPath);

            // 1. Copy the original header/TOC area as-is (keeps padding and footer text)
            var originalHeaderArea = new byte[ContentOffset];
            _stream.Position = 0;
            _stream.ReadExactly(originalHeaderArea);
            output.Write(originalHeaderArea);

            // 2. Write file data in original physical order
            long writePosition = ContentOffset;
            foreach (var entry in Entries.OrderBy(e => e.AbsoluteOffset))
            {
                writePosition = AlignUp(writePosition, Alignment);

                byte[] fileData;
                uint newFileSize, newExtractSize;

                var replacementData = getReplacement(entry);
                if (replacementData != null)
                {
                    fileData = replacementData;
                    newFileSize = newExtractSize = (uint)replacementData.Length;
                }
                else
                {
                    fileData = ReadRaw(entry);
                    newFileSize = entry.FileSize;
                    newExtractSize = entry.ExtractSize;
                }

                output.Position = writePosition;
                output.Write(fileData);

                UtfTable.PatchCell(_tocPacket, entry.FileSizeCell, newFileSize);
                UtfTable.PatchCell(_tocPacket, entry.ExtractSizeCell, newExtractSize);
                UtfTable.PatchCell(_tocPacket, entry.FileOffsetCell, (ulong)(writePosition - DataBaseOffset));

                writePosition += fileData.Length;
            }
            long contentEndOffset = AlignUp(writePosition, Alignment);

            // 3. ETOC (timestamps, unchanged; TOC order is preserved)
            long newEtocOffset = contentEndOffset;
            long totalFileLength = contentEndOffset;
            if (EtocSize > 0)
            {
                var etocData = new byte[EtocSize];
                _stream.Position = EtocOffset;
                _stream.ReadExactly(etocData);

                output.Position = newEtocOffset;
                output.Write(etocData);
                totalFileLength = newEtocOffset + EtocSize;
            }
            output.SetLength(totalFileLength);

            // 4. Patch CpkHeader
            UtfTable.PatchCell(_headerPacket, _headerRow["ContentSize"], (ulong)(contentEndOffset - ContentOffset));
            if (EtocSize > 0)
                UtfTable.PatchCell(_headerPacket, _headerRow["EtocOffset"], (ulong)newEtocOffset);
            if (_headerRow.TryGetValue("FileSize", out var fileSizeCell) && fileSizeCell.Position >= 0)
                UtfTable.PatchCell(_headerPacket, fileSizeCell, (ulong)totalFileLength);

            // 5. Re-encrypt and write header + TOC
            if (_headerEncrypted) ApplyXorMask(_headerPacket, 16, _headerPacket.Length - 16);
            if (_tocEncrypted) ApplyXorMask(_tocPacket, 16, _tocPacket.Length - 16);

            output.Position = 0;
            output.Write(_headerPacket);
            output.Position = TocOffset;
            output.Write(_tocPacket);
        }

        public void Dispose() => _stream.Dispose();
    }
}
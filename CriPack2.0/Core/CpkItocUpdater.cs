namespace CpkToolkit.Core;

/// <summary>
/// The ITOC stores per-file sizes (keyed by file ID) in two nested @UTF tables:
/// DataL with 16-bit sizes and DataH with 32-bit sizes. This updates them in place.
/// </summary>
public static class CpkItocUpdater
{
    private const ulong MaximumSixteenBitSize = 0xFFFF;

    /// <returns>The number of ITOC rows that were updated.</returns>
    public static int UpdateFileSizes(CpkPacket itocPacket, IReadOnlyDictionary<long, CpkEntrySizes> sizesByFileId)
    {
        int updatedRowCount = 0;
        IReadOnlyDictionary<string, UtfCell> outerRow =
            UtfTable.Parse(itocPacket.Data, CpkPacket.PacketHeaderLength).Rows[0];

        foreach (string nestedTableName in new[] { "DataL", "DataH" })
        {
            if (!outerRow.TryGetValue(nestedTableName, out UtfCell? blobCell) ||
                blobCell.Value is not UtfDataBlob blob ||
                blob.Length <= 0)
                continue;

            UtfTable nestedTable = UtfTable.Parse(itocPacket.Data, blob.Offset);
            foreach (IReadOnlyDictionary<string, UtfCell> row in nestedTable.Rows)
            {
                long fileId = (long)row["ID"].AsUInt64();
                if (!sizesByFileId.TryGetValue(fileId, out CpkEntrySizes sizes))
                    continue;

                bool exceedsSixteenBits = (ulong)sizes.StoredSize > MaximumSixteenBitSize ||
                                          (ulong)sizes.ExtractSize > MaximumSixteenBitSize;
                if (nestedTableName == "DataL" && exceedsSixteenBits)
                {
                    throw new NotSupportedException(
                        $"File ID {fileId} no longer fits the 16-bit ITOC table (DataL) and would have to move " +
                        "to the 32-bit table (DataH). That requires rebuilding the ITOC, which this tool does not do yet.");
                }

                UtfTable.PatchCell(itocPacket.Data, row["FileSize"], (ulong)sizes.StoredSize);
                UtfTable.PatchCell(itocPacket.Data, row["ExtractSize"], (ulong)sizes.ExtractSize);
                updatedRowCount++;
            }
        }

        return updatedRowCount;
    }
}

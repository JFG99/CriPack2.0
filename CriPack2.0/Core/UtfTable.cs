using System.Text;

namespace CpkToolkit.Core;

/// <summary>
/// A parsed @UTF table, the structured table format CRIWARE uses inside every CPK packet.
/// Every cell remembers its byte position so values of fixed width can be patched in place.
/// </summary>
public sealed class UtfTable
{
    private const int TableHeaderLength = 0x20;

    private readonly List<UtfColumnDefinition> columns = new();
    private readonly List<Dictionary<string, UtfCell>> rows = new();

    /// <summary>Text encoding of the table's string pool (older archives may use Shift-JIS).</summary>
    public static Encoding StringEncoding { get; set; } = Encoding.UTF8;

    public string Name { get; private set; } = string.Empty;

    public IReadOnlyList<UtfColumnDefinition> Columns => columns;

    public IReadOnlyList<IReadOnlyDictionary<string, UtfCell>> Rows => rows;

    public static bool HasUtfMagic(byte[] buffer, int position) =>
        position >= 0 &&
        position + 4 <= buffer.Length &&
        buffer[position] == (byte)'@' &&
        buffer[position + 1] == (byte)'U' &&
        buffer[position + 2] == (byte)'T' &&
        buffer[position + 3] == (byte)'F';

    /// <param name="buffer">Buffer holding the (decrypted) table.</param>
    /// <param name="tableStart">Position of the "@UTF" magic inside the buffer.</param>
    public static UtfTable Parse(byte[] buffer, int tableStart)
    {
        if (!HasUtfMagic(buffer, tableStart) || tableStart + TableHeaderLength > buffer.Length)
            throw new InvalidDataException("The data is not a @UTF table (it may still be encrypted).");

        // All offsets stored inside the table are relative to the byte after the table size field.
        int tableBase = tableStart + 8;
        int rowsOffset = BigEndianConverter.ReadUInt16(buffer, tableStart + 0x0A);
        int stringsOffset = checked((int)BigEndianConverter.ReadUInt32(buffer, tableStart + 0x0C));
        int dataOffset = checked((int)BigEndianConverter.ReadUInt32(buffer, tableStart + 0x10));
        uint tableNameOffset = BigEndianConverter.ReadUInt32(buffer, tableStart + 0x14);
        int columnCount = BigEndianConverter.ReadUInt16(buffer, tableStart + 0x18);
        int rowWidth = BigEndianConverter.ReadUInt16(buffer, tableStart + 0x1A);
        int rowCount = checked((int)BigEndianConverter.ReadUInt32(buffer, tableStart + 0x1C));

        string ReadString(uint stringOffset)
        {
            int start = tableBase + stringsOffset + (int)stringOffset;
            int end = start;
            while (buffer[end] != 0)
            {
                end++;
            }

            return StringEncoding.GetString(buffer, start, end - start);
        }

        object ReadValue(UtfColumnType columnType, ref int position)
        {
            object value;
            switch (columnType)
            {
                case UtfColumnType.Byte:
                    value = buffer[position];
                    position += 1;
                    break;
                case UtfColumnType.SByte:
                    value = (sbyte)buffer[position];
                    position += 1;
                    break;
                case UtfColumnType.UShort:
                    value = BigEndianConverter.ReadUInt16(buffer, position);
                    position += 2;
                    break;
                case UtfColumnType.Short:
                    value = (short)BigEndianConverter.ReadUInt16(buffer, position);
                    position += 2;
                    break;
                case UtfColumnType.UInt:
                    value = BigEndianConverter.ReadUInt32(buffer, position);
                    position += 4;
                    break;
                case UtfColumnType.Int:
                    value = (int)BigEndianConverter.ReadUInt32(buffer, position);
                    position += 4;
                    break;
                case UtfColumnType.ULong:
                    value = BigEndianConverter.ReadUInt64(buffer, position);
                    position += 8;
                    break;
                case UtfColumnType.Long:
                    value = (long)BigEndianConverter.ReadUInt64(buffer, position);
                    position += 8;
                    break;
                case UtfColumnType.Float:
                    value = BitConverter.Int32BitsToSingle((int)BigEndianConverter.ReadUInt32(buffer, position));
                    position += 4;
                    break;
                case UtfColumnType.String:
                    value = ReadString(BigEndianConverter.ReadUInt32(buffer, position));
                    position += 4;
                    break;
                case UtfColumnType.Data:
                    value = new UtfDataBlob(
                        tableBase + dataOffset + checked((int)BigEndianConverter.ReadUInt32(buffer, position)),
                        checked((int)BigEndianConverter.ReadUInt32(buffer, position + 4)));
                    position += 8;
                    break;
                case UtfColumnType.Guid:
                    value = buffer.AsSpan(position, 16).ToArray();
                    position += 16;
                    break;
                default:
                    throw new InvalidDataException($"Unknown @UTF column type 0x{(byte)columnType:X}.");
            }

            return value;
        }

        var table = new UtfTable { Name = ReadString(tableNameOffset) };

        int schemaPosition = tableStart + TableHeaderLength;
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            byte flags = buffer[schemaPosition++];
            var storage = (UtfColumnStorage)(flags & 0xF0);
            var type = (UtfColumnType)(flags & 0x0F);
            string columnName = ReadString(BigEndianConverter.ReadUInt32(buffer, schemaPosition));
            schemaPosition += 4;

            object? constantValue = storage == UtfColumnStorage.Constant
                ? ReadValue(type, ref schemaPosition)
                : null;

            table.columns.Add(new UtfColumnDefinition(columnName, storage, type, constantValue));
        }

        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            int cellPosition = tableBase + rowsOffset + (rowIndex * rowWidth);
            var row = new Dictionary<string, UtfCell>();

            foreach (UtfColumnDefinition column in table.columns)
            {
                if (column.Storage == UtfColumnStorage.PerRow)
                {
                    int cellStart = cellPosition;
                    object value = ReadValue(column.Type, ref cellPosition);
                    row[column.Name] = new UtfCell { Type = column.Type, Position = cellStart, Value = value };
                }
                else if (column.Storage == UtfColumnStorage.Constant)
                {
                    row[column.Name] = new UtfCell { Type = column.Type, Value = column.ConstantValue };
                }
            }

            table.rows.Add(row);
        }

        return table;
    }

    /// <summary>
    /// Overwrites a fixed-width cell inside <paramref name="buffer"/>. The parsed table is not modified,
    /// so the same parsed table can be used to patch several independent copies of the buffer.
    /// </summary>
    public static void PatchCell(byte[] buffer, UtfCell cell, ulong newValue)
    {
        if (!cell.IsPatchable)
            throw new InvalidOperationException("The column uses constant storage and cannot be patched in place.");

        switch (cell.Type)
        {
            case UtfColumnType.Byte:
                EnsureFits(newValue, byte.MaxValue, cell.Type);
                buffer[cell.Position] = (byte)newValue;
                break;
            case UtfColumnType.UShort:
                EnsureFits(newValue, ushort.MaxValue, cell.Type);
                BigEndianConverter.WriteUInt16(buffer, cell.Position, (ushort)newValue);
                break;
            case UtfColumnType.UInt:
                EnsureFits(newValue, uint.MaxValue, cell.Type);
                BigEndianConverter.WriteUInt32(buffer, cell.Position, (uint)newValue);
                break;
            case UtfColumnType.ULong:
                BigEndianConverter.WriteUInt64(buffer, cell.Position, newValue);
                break;
            default:
                throw new NotSupportedException($"Patching cells of type {cell.Type} is not supported.");
        }
    }

    private static void EnsureFits(ulong value, ulong maximum, UtfColumnType type)
    {
        if (value > maximum)
            throw new OverflowException($"The value {value:N0} does not fit in a {type} column (maximum {maximum:N0}).");
    }
}

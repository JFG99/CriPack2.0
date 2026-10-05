using CriPack.Archive.Enum;
using System.Text;

namespace CriPack.Archive
{
    public sealed class UtfTable
    {
        public string Name = "";
        public List<Dictionary<string, UtfCell>> Rows = new();

        public static UtfTable Parse(byte[] buffer, int tableStart)
        {
            if (buffer[tableStart] != '@' || buffer[tableStart + 1] != 'U' ||
                buffer[tableStart + 2] != 'T' || buffer[tableStart + 3] != 'F')
                throw new InvalidDataException("Not a @UTF table (still encrypted?)");

            int tableBase = tableStart + 8;   // all internal offsets are relative to this
            int rowsOffset = buffer.ReadUInt16(tableStart + 0x0A);
            int stringsOffset = (int)buffer.ReadUInt32(tableStart + 0x0C);
            int dataOffset = (int)buffer.ReadUInt32(tableStart + 0x10);
            int tableNameOffset = (int)buffer.ReadUInt32(tableStart + 0x14);
            int columnCount = buffer.ReadUInt16(tableStart + 0x18);
            int rowWidth = buffer.ReadUInt16(tableStart + 0x1A);
            int rowCount = (int)buffer.ReadUInt32(tableStart + 0x1C);

            var table = new UtfTable { Name = ReadString((uint)tableNameOffset) };
            var columns = new List<(string Name, int StorageMode, UtfType Type, object? ConstantValue)>();

            int schemaPosition = tableStart + 0x20;
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                byte flags = buffer[schemaPosition++];
                int storageMode = flags & 0xF0;                  // 0x10 = no value, 0x30 = constant, 0x50 = per row
                var columnType = (UtfType)(flags & 0x0F);
                string columnName = ReadString(buffer.ReadUInt32(schemaPosition));
                schemaPosition += 4;
                object? constantValue = storageMode == 0x30 ? ReadValue(columnType, ref schemaPosition) : null;
                columns.Add((columnName, storageMode, columnType, constantValue));
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                int cellPosition = tableBase + rowsOffset + rowIndex * rowWidth;
                var row = new Dictionary<string, UtfCell>();
                foreach (var column in columns)
                {
                    if (column.StorageMode == 0x50)
                    {
                        int cellStart = cellPosition;
                        row[column.Name] = new UtfCell
                        {
                            Type = column.Type,
                            Position = cellStart,
                            Value = ReadValue(column.Type, ref cellPosition)
                        };
                    }
                    else if (column.StorageMode == 0x30)
                    {
                        row[column.Name] = new UtfCell { Type = column.Type, Value = column.ConstantValue };
                    }
                }
                table.Rows.Add(row);
            }
            return table;

            string ReadString(uint stringOffset)
            {
                int start = tableBase + stringsOffset + (int)stringOffset;
                int end = start;
                while (buffer[end] != 0) end++;
                return Encoding.UTF8.GetString(buffer, start, end - start);   // some older archives use Shift-JIS
            }

            object ReadValue(UtfType columnType, ref int position)
            {
                object value;
                switch (columnType)
                {
                    case UtfType.Byte: value = buffer[position]; position += 1; break;
                    case UtfType.SByte: value = (sbyte)buffer[position]; position += 1; break;
                    case UtfType.UShort: value = buffer.ReadUInt16(position); position += 2; break;
                    case UtfType.Short: value = (short)buffer.ReadUInt16(position); position += 2; break;
                    case UtfType.UInt: value = buffer.ReadUInt32(position); position += 4; break;
                    case UtfType.Int: value = (int)buffer.ReadUInt32(position); position += 4; break;
                    case UtfType.ULong: value = buffer.ReadUInt64(position); position += 8; break;
                    case UtfType.Long: value = (long)buffer.ReadUInt64(position); position += 8; break;
                    case UtfType.Float: value = BitConverter.Int32BitsToSingle((int)buffer.ReadUInt32(position)); position += 4; break;
                    case UtfType.String: value = ReadString(buffer.ReadUInt32(position)); position += 4; break;
                    case UtfType.Data:
                        value = (Offset: tableBase + dataOffset + (int)buffer.ReadUInt32(position),
                                 Length: (int)buffer.ReadUInt32(position + 4));
                        position += 8;
                        break;
                    case UtfType.Guid: value = buffer.AsSpan(position, 16).ToArray(); position += 16; break;
                    default: throw new InvalidDataException($"Unknown column type {columnType}");
                }
                return value;
            }
        }

        public static void PatchCell(byte[] buffer, UtfCell cell, ulong newValue)
        {
            if (cell.Position < 0)
                throw new InvalidOperationException("Column is constant-storage; can't patch in place.");

            switch (cell.Type)
            {
                case UtfType.UShort: buffer.WriteUInt16(cell.Position, (ushort)newValue); break;
                case UtfType.UInt: buffer.WriteUInt32(cell.Position, (uint)newValue); break;
                case UtfType.ULong: buffer.WriteUInt64(cell.Position, newValue); break;
                default: throw new NotSupportedException($"Patching {cell.Type} not supported");
            }

            cell.Value = cell.Type switch
            {
                UtfType.UShort => (object)(ushort)newValue,
                UtfType.UInt => (uint)newValue,
                _ => newValue
            };
        }
    }
}

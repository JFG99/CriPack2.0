using CriPack.Archive.Enum;

namespace CriPack.Archive
{
    public sealed class UtfCell
    {
        public UtfType Type;
        public int Position = -1;   // absolute position in the buffer (-1 = constant, not patchable)
        public object? Value;
        public ulong AsUInt64() => Convert.ToUInt64(Value);
    }
}

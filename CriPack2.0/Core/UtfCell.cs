using System.Globalization;

namespace CpkToolkit.Core;

/// <summary>One value of a @UTF table row, together with where it lives in the parsed buffer.</summary>
public sealed class UtfCell
{
    public UtfColumnType Type { get; init; }

    /// <summary>Absolute position of the value in the buffer, or -1 for constant-storage columns.</summary>
    public int Position { get; init; } = -1;

    public object? Value { get; init; }

    public bool IsPatchable => Position >= 0;

    public ulong AsUInt64() => Convert.ToUInt64(Value, CultureInfo.InvariantCulture);

    public string ToDisplayString() => Value switch
    {
        null => "(none)",
        UtfDataBlob blob => $"data blob ({blob.Length:N0} bytes)",
        byte[] bytes => Convert.ToHexString(bytes),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Value.ToString() ?? string.Empty
    };
}

namespace CpkToolkit.Core;

public static class ByteSizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Formats a size such as 6227496181 as "5.80 GB".</summary>
    public static string Format(long byteCount)
    {
        double value = byteCount;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{byteCount:N0} B" : $"{value:0.##} {Units[unitIndex]}";
    }
}

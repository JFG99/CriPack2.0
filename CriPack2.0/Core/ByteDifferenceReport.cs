using System.Text;

namespace CpkToolkit.Core;

/// <summary>
/// Builds a readable text report of where two versions of a file differ: the total number of differing bytes
/// and the differing ranges with a hex and text preview of both sides.
/// </summary>
public static class ByteDifferenceReport
{
    private const int MaxRangesShown = 300;
    private const int MergeGapLength = 8;       // differing bytes closer together than this form one range
    private const int PreviewByteCount = 24;

    public static string Build(string fileName, byte[] firstData, byte[] secondData)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"File: {fileName}");
        builder.AppendLine($"First version : {firstData.Length:N0} bytes");
        builder.AppendLine($"Second version: {secondData.Length:N0} bytes");

        int commonLength = Math.Min(firstData.Length, secondData.Length);
        if (firstData.Length != secondData.Length)
        {
            builder.AppendLine(
                $"The sizes differ by {Math.Abs(firstData.Length - secondData.Length):N0} bytes; " +
                $"only the first {commonLength:N0} bytes are compared byte by byte.");
        }

        var ranges = new List<(int Start, int EndExclusive)>();
        long differingByteCount = 0;
        int index = 0;
        while (index < commonLength)
        {
            if (firstData[index] == secondData[index])
            {
                index++;
                continue;
            }

            int rangeStart = index;
            int lastDifferentIndex = index;
            differingByteCount++;
            index++;
            while (index < commonLength && index - lastDifferentIndex <= MergeGapLength)
            {
                if (firstData[index] != secondData[index])
                {
                    lastDifferentIndex = index;
                    differingByteCount++;
                }

                index++;
            }

            ranges.Add((rangeStart, lastDifferentIndex + 1));
            index = lastDifferentIndex + 1;
        }

        builder.AppendLine($"Differing bytes: {differingByteCount:N0} in {ranges.Count:N0} range(s)");
        if (ranges.Count == 0 && firstData.Length == secondData.Length)
        {
            builder.AppendLine("The two versions are identical.");
            return builder.ToString();
        }

        builder.AppendLine();
        int shownCount = Math.Min(ranges.Count, MaxRangesShown);
        for (int rangeIndex = 0; rangeIndex < shownCount; rangeIndex++)
        {
            (int start, int endExclusive) = ranges[rangeIndex];
            builder.AppendLine($"0x{start:X6} - 0x{endExclusive - 1:X6}  ({endExclusive - start:N0} bytes)");
            builder.AppendLine("  first : " + DescribeBytes(firstData, start, endExclusive));
            builder.AppendLine("  second: " + DescribeBytes(secondData, start, endExclusive));
        }

        if (ranges.Count > shownCount)
            builder.AppendLine($"... and {ranges.Count - shownCount:N0} more range(s) not shown.");

        return builder.ToString();
    }

    private static string DescribeBytes(byte[] data, int start, int endExclusive)
    {
        int previewLength = Math.Min(PreviewByteCount, endExclusive - start);
        var hex = new StringBuilder();
        var text = new StringBuilder();
        for (int offset = 0; offset < previewLength; offset++)
        {
            byte value = data[start + offset];
            hex.Append(value.ToString("X2")).Append(' ');
            text.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
        }

        string ellipsis = endExclusive - start > previewLength ? " ..." : string.Empty;
        return $"{hex.ToString().TrimEnd()}{ellipsis}   |{text}|";
    }
}

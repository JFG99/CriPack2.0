namespace CpkToolkit.Core;

/// <summary>A progress report from a long-running extract or repack operation.</summary>
public readonly record struct CpkOperationProgress(
    string Message,
    long BytesProcessed,
    long TotalBytes,
    int ItemsProcessed,
    int TotalItems)
{
    /// <summary>Completion between 0 and 1, based on bytes when known and on items otherwise.</summary>
    public double Fraction
    {
        get
        {
            if (TotalBytes > 0)
                return Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1);
            if (TotalItems > 0)
                return Math.Clamp((double)ItemsProcessed / TotalItems, 0, 1);
            return 0;
        }
    }
}

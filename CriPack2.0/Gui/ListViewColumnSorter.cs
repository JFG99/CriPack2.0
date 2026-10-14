using System.Collections;

namespace CpkToolkit.Gui;

/// <summary>
/// Sorts a details-view ListView by a clicked column. Each sub-item's Tag may hold a sort key
/// (for example a number) so that "10" sorts after "9" even though the displayed text is formatted.
/// </summary>
public sealed class ListViewColumnSorter : IComparer
{
    public int SortColumnIndex { get; set; }

    public bool IsAscending { get; set; } = true;

    public int Compare(object? left, object? right)
    {
        if (left is not ListViewItem leftItem || right is not ListViewItem rightItem)
            return 0;

        object? leftKey = GetSortKey(leftItem);
        object? rightKey = GetSortKey(rightItem);

        int result;
        if (leftKey is IComparable comparableLeft && rightKey != null && leftKey.GetType() == rightKey.GetType())
        {
            result = comparableLeft.CompareTo(rightKey);
        }
        else
        {
            result = string.Compare(
                leftItem.SubItems[SortColumnIndex].Text,
                rightItem.SubItems[SortColumnIndex].Text,
                StringComparison.OrdinalIgnoreCase);
        }

        return IsAscending ? result : -result;
    }

    private object? GetSortKey(ListViewItem item) =>
        SortColumnIndex < item.SubItems.Count ? item.SubItems[SortColumnIndex].Tag : null;
}

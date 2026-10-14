using System.Globalization;
using System.Text;
using CpkToolkit.Core;

namespace CpkToolkit.Gui;

/// <summary>
/// Window that compares an original CPK with a patched CPK and lists every file that differs.
/// The differing files can be exported to CSV or extracted from either archive.
/// </summary>
public sealed class CompareForm : Form
{
    private const int ProgressBarResolution = 1000;
    private const string AllStatusesFilterText = "All differences";

    private readonly ListViewColumnSorter resultColumnSorter = new();

    private readonly TextBox firstArchivePathTextBox = new();
    private readonly TextBox secondArchivePathTextBox = new();
    private readonly Button firstBrowseButton = new() { Text = "Browse..." };
    private readonly Button secondBrowseButton = new() { Text = "Browse..." };
    private readonly CheckBox verifyContentsCheckBox = new()
    {
        Text = "Compare file contents (accurate, reads both archives fully). Untick for a size-only check.",
        Checked = true,
        AutoSize = true
    };
    private readonly Button compareButton = new() { Text = "Compare" };
    private readonly Button cancelButton = new() { Text = "Cancel", Enabled = false };

    private readonly Label summaryLabel = new() { AutoSize = true };
    private readonly ComboBox statusFilterComboBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox nameFilterTextBox = new() { Width = 220, PlaceholderText = "Filter by name..." };
    private readonly ListView resultListView = new();

    private readonly Button exportCsvButton = new() { Text = "Export list as CSV...", AutoSize = true };
    private readonly Button extractFromSecondButton = new() { Text = "Extract from patched CPK...", AutoSize = true };
    private readonly Button extractFromFirstButton = new() { Text = "Extract from original CPK...", AutoSize = true };
    private readonly Button copyNamesButton = new() { Text = "Copy names", AutoSize = true };
    private readonly Button byteDifferencesButton = new() { Text = "Byte differences of selected file...", AutoSize = true };

    private readonly ProgressBar progressBar = new() { Minimum = 0, Maximum = ProgressBarResolution, Dock = DockStyle.Fill };
    private readonly Label statusLabel = new() { AutoSize = true, Text = "Choose the original CPK and the patched CPK, then press Compare." };

    private CpkArchive? firstArchive;
    private CpkArchive? secondArchive;
    private CpkComparisonResult? comparisonResult;
    private CancellationTokenSource? operationCancellation;
    private bool isBusy;

    public CompareForm(string? initialFirstArchivePath)
    {
        Text = "Compare two CPK archives";
        Width = 1250;
        Height = 780;
        StartPosition = FormStartPosition.CenterParent;

        firstArchivePathTextBox.Text = initialFirstArchivePath ?? string.Empty;

        BuildLayout();
        WireEvents();
        UpdateButtonStates();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (isBusy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "An operation is still running. Cancel it first, then close the window.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        base.OnFormClosing(e);
    }

    // ---------- Layout ----------

    private void BuildLayout()
    {
        var topTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Padding = new Padding(8)
        };
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        firstArchivePathTextBox.Dock = DockStyle.Fill;
        secondArchivePathTextBox.Dock = DockStyle.Fill;

        topTable.Controls.Add(new Label { Text = "Original CPK:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        topTable.Controls.Add(firstArchivePathTextBox, 1, 0);
        topTable.Controls.Add(firstBrowseButton, 2, 0);
        topTable.Controls.Add(new Label { Text = "Patched CPK:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        topTable.Controls.Add(secondArchivePathTextBox, 1, 1);
        topTable.Controls.Add(secondBrowseButton, 2, 1);
        topTable.Controls.Add(verifyContentsCheckBox, 1, 2);
        topTable.Controls.Add(compareButton, 2, 2);
        topTable.Controls.Add(cancelButton, 3, 2);

        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 4)
        };
        filterPanel.Controls.Add(summaryLabel);
        filterPanel.Controls.Add(new Label { Text = "   Show:", AutoSize = true, Margin = new Padding(12, 6, 0, 0) });
        filterPanel.Controls.Add(statusFilterComboBox);
        filterPanel.Controls.Add(nameFilterTextBox);

        statusFilterComboBox.Items.AddRange(new object[]
        {
            AllStatusesFilterText,
            DescribeStatus(CpkComparisonStatus.ContentChanged),
            DescribeStatus(CpkComparisonStatus.StorageOnlyChanged),
            DescribeStatus(CpkComparisonStatus.OnlyInFirst),
            DescribeStatus(CpkComparisonStatus.OnlyInSecond),
            DescribeStatus(CpkComparisonStatus.CouldNotCompare)
        });
        statusFilterComboBox.SelectedIndex = 0;

        ConfigureResultListView();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 4)
        };
        buttonPanel.Controls.Add(exportCsvButton);
        buttonPanel.Controls.Add(copyNamesButton);
        buttonPanel.Controls.Add(byteDifferencesButton);
        buttonPanel.Controls.Add(extractFromSecondButton);
        buttonPanel.Controls.Add(extractFromFirstButton);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(8, 0, 8, 6)
        };
        statusPanel.Controls.Add(statusLabel, 0, 0);
        progressBar.Height = 14;
        statusPanel.Controls.Add(progressBar, 0, 1);

        // Docking order: the last control added is laid out first.
        Controls.Add(resultListView);
        Controls.Add(buttonPanel);
        Controls.Add(statusPanel);
        Controls.Add(filterPanel);
        Controls.Add(topTable);
    }

    private void ConfigureResultListView()
    {
        resultListView.Dock = DockStyle.Fill;
        resultListView.View = View.Details;
        resultListView.FullRowSelect = true;
        resultListView.GridLines = true;
        resultListView.HideSelection = false;
        resultListView.MultiSelect = true;
        resultListView.ListViewItemSorter = resultColumnSorter;

        resultListView.Columns.Add("Difference", 190);
        resultListView.Columns.Add("File", 360);
        resultListView.Columns.Add("Original size", 100, HorizontalAlignment.Right);
        resultListView.Columns.Add("Patched size", 100, HorizontalAlignment.Right);
        resultListView.Columns.Add("Size change", 100, HorizontalAlignment.Right);
        resultListView.Columns.Add("Original stored", 100, HorizontalAlignment.Right);
        resultListView.Columns.Add("Patched stored", 100, HorizontalAlignment.Right);
        resultListView.Columns.Add("Original compressed", 120);
        resultListView.Columns.Add("Patched compressed", 120);

        resultListView.ColumnClick += (_, args) =>
        {
            if (args.Column == resultColumnSorter.SortColumnIndex)
            {
                resultColumnSorter.IsAscending = !resultColumnSorter.IsAscending;
            }
            else
            {
                resultColumnSorter.SortColumnIndex = args.Column;
                resultColumnSorter.IsAscending = true;
            }

            resultListView.Sort();
        };
    }

    private void WireEvents()
    {
        firstBrowseButton.Click += (_, _) => BrowseForArchive(firstArchivePathTextBox, "Choose the ORIGINAL CPK");
        secondBrowseButton.Click += (_, _) => BrowseForArchive(secondArchivePathTextBox, "Choose the PATCHED CPK");
        compareButton.Click += OnCompareClicked;
        cancelButton.Click += (_, _) => operationCancellation?.Cancel();
        statusFilterComboBox.SelectedIndexChanged += (_, _) => RefreshResultList();
        nameFilterTextBox.TextChanged += (_, _) => RefreshResultList();
        exportCsvButton.Click += OnExportCsvClicked;
        copyNamesButton.Click += OnCopyNamesClicked;
        byteDifferencesButton.Click += OnByteDifferencesClicked;
        resultListView.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        extractFromSecondButton.Click += async (_, _) => await ExtractSelectedAsync(fromSecondArchive: true);
        extractFromFirstButton.Click += async (_, _) => await ExtractSelectedAsync(fromSecondArchive: false);
    }

    private void BrowseForArchive(TextBox target, string title)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "CPK archives (*.cpk)|*.cpk|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.FileName;
    }

    // ---------- Comparing ----------

    private async void OnCompareClicked(object? sender, EventArgs e)
    {
        string firstPath = firstArchivePathTextBox.Text.Trim().Trim('"');
        string secondPath = secondArchivePathTextBox.Text.Trim().Trim('"');
        if (!File.Exists(firstPath) || !File.Exists(secondPath))
        {
            MessageBox.Show(this, "Choose two existing CPK files first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool verifyContents = verifyContentsCheckBox.Checked;
        using var cancellationSource = new CancellationTokenSource();
        operationCancellation = cancellationSource;
        SetBusy(true, "Reading both archives' tables...");
        var progress = new Progress<CpkOperationProgress>(ShowProgress);

        try
        {
            (CpkArchive first, CpkArchive second, CpkComparisonResult result) = await Task.Run(() =>
            {
                var firstOpened = new CpkArchive(firstPath);
                var secondOpened = new CpkArchive(secondPath);
                CpkComparisonResult comparison = new CpkComparer(firstOpened, secondOpened)
                    .Compare(verifyContents, progress, cancellationSource.Token);
                return (firstOpened, secondOpened, comparison);
            });

            firstArchive = first;
            secondArchive = second;
            comparisonResult = result;
            ShowSummary(result);
            RefreshResultList();
            statusLabel.Text = "Done.";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "Cancelled.";
        }
        catch (Exception exception)
        {
            statusLabel.Text = "Failed.";
            ShowError(exception);
        }
        finally
        {
            operationCancellation = null;
            SetBusy(false, null);
        }
    }

    private void ShowSummary(CpkComparisonResult result)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.CurrentCulture, $"{result.DifferentRows.Count:N0} different, ");
        builder.Append(CultureInfo.CurrentCulture, $"{result.IdenticalCount:N0} identical");
        if (result.SameSizeUnverifiedCount > 0)
            builder.Append(CultureInfo.CurrentCulture, $", {result.SameSizeUnverifiedCount:N0} same size (bytes NOT compared)");
        builder.Append(CultureInfo.CurrentCulture, $"   |   changed {result.CountWithStatus(CpkComparisonStatus.ContentChanged):N0}");
        builder.Append(CultureInfo.CurrentCulture, $", storage only {result.CountWithStatus(CpkComparisonStatus.StorageOnlyChanged):N0}");
        builder.Append(CultureInfo.CurrentCulture, $", only in original {result.CountWithStatus(CpkComparisonStatus.OnlyInFirst):N0}");
        builder.Append(CultureInfo.CurrentCulture, $", only in patched {result.CountWithStatus(CpkComparisonStatus.OnlyInSecond):N0}");
        int couldNotCompareCount = result.CountWithStatus(CpkComparisonStatus.CouldNotCompare);
        if (couldNotCompareCount > 0)
            builder.Append(CultureInfo.CurrentCulture, $", could not decompress {couldNotCompareCount:N0}");
        summaryLabel.Text = builder.ToString();
    }

    // ---------- Result list ----------

    private void RefreshResultList()
    {
        resultListView.BeginUpdate();
        resultListView.Items.Clear();

        if (comparisonResult != null)
        {
            string nameFilter = nameFilterTextBox.Text.Trim();
            string statusFilter = statusFilterComboBox.SelectedItem as string ?? AllStatusesFilterText;

            var items = new List<ListViewItem>();
            foreach (CpkComparisonRow row in comparisonResult.DifferentRows)
            {
                if (statusFilter != AllStatusesFilterText && DescribeStatus(row.Status) != statusFilter)
                    continue;
                if (nameFilter.Length > 0 && row.FullPath.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                items.Add(CreateResultItem(row));
            }

            resultListView.Items.AddRange(items.ToArray());
        }

        resultListView.EndUpdate();
        UpdateButtonStates();
    }

    private static ListViewItem CreateResultItem(CpkComparisonRow row)
    {
        CpkEntry? first = row.FirstEntry;
        CpkEntry? second = row.SecondEntry;

        var item = new ListViewItem(DescribeStatus(row.Status)) { Tag = row };
        item.SubItems[0].Tag = (int)row.Status;
        AddSubItem(item, row.FullPath, row.FullPath);
        AddSizeSubItem(item, first?.ExtractSize);
        AddSizeSubItem(item, second?.ExtractSize);

        if (first != null && second != null)
        {
            long change = second.ExtractSize - first.ExtractSize;
            AddSubItem(item, (change > 0 ? "+" : string.Empty) + change.ToString("N0", CultureInfo.CurrentCulture), change);
        }
        else
        {
            AddSubItem(item, string.Empty, 0L);
        }

        AddSizeSubItem(item, first?.StoredSize);
        AddSizeSubItem(item, second?.StoredSize);
        AddSubItem(item, first == null ? string.Empty : first.IsCompressed ? "Yes" : "No", first?.IsCompressed == true ? 1 : 0);
        AddSubItem(item, second == null ? string.Empty : second.IsCompressed ? "Yes" : "No", second?.IsCompressed == true ? 1 : 0);
        return item;
    }

    private static void AddSizeSubItem(ListViewItem item, long? size) =>
        AddSubItem(item, size.HasValue ? size.Value.ToString("N0", CultureInfo.CurrentCulture) : string.Empty, size ?? -1L);

    private static void AddSubItem(ListViewItem item, string text, IComparable sortKey)
    {
        ListViewItem.ListViewSubItem subItem = item.SubItems.Add(text);
        subItem.Tag = sortKey;
    }

    private static string DescribeStatus(CpkComparisonStatus status) => status switch
    {
        CpkComparisonStatus.ContentChanged => "Content changed",
        CpkComparisonStatus.StorageOnlyChanged => "Same content, stored differently",
        CpkComparisonStatus.OnlyInFirst => "Only in original",
        CpkComparisonStatus.OnlyInSecond => "Only in patched",
        CpkComparisonStatus.CouldNotCompare => "Could not decompress to compare",
        _ => "Identical"
    };

    /// <summary>The selected rows, or every shown row when nothing is selected.</summary>
    private List<CpkComparisonRow> GetSelectedOrShownRows()
    {
        IEnumerable<ListViewItem> items = resultListView.SelectedItems.Count > 0
            ? resultListView.SelectedItems.Cast<ListViewItem>()
            : resultListView.Items.Cast<ListViewItem>();
        return items.Select(item => (CpkComparisonRow)item.Tag!).ToList();
    }

    // ---------- Export and extract ----------

    private void OnExportCsvClicked(object? sender, EventArgs e)
    {
        List<CpkComparisonRow> rows = GetSelectedOrShownRows();
        if (rows.Count == 0)
            return;

        using var dialog = new SaveFileDialog { Title = "Save difference list", Filter = "CSV file (*.csv)|*.csv", FileName = "cpk_differences.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Difference,File,OriginalSize,PatchedSize,OriginalStored,PatchedStored,OriginalCompressed,PatchedCompressed");
            foreach (CpkComparisonRow row in rows)
            {
                builder.AppendLine(string.Join(",",
                    EscapeCsv(DescribeStatus(row.Status)),
                    EscapeCsv(row.FullPath),
                    row.FirstEntry?.ExtractSize.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    row.SecondEntry?.ExtractSize.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    row.FirstEntry?.StoredSize.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    row.SecondEntry?.StoredSize.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    row.FirstEntry == null ? string.Empty : row.FirstEntry.IsCompressed.ToString(),
                    row.SecondEntry == null ? string.Empty : row.SecondEntry.IsCompressed.ToString()));
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            statusLabel.Text = $"Saved {rows.Count:N0} rows to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private void OnCopyNamesClicked(object? sender, EventArgs e)
    {
        List<CpkComparisonRow> rows = GetSelectedOrShownRows();
        if (rows.Count == 0)
            return;

        Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(row => row.FullPath)));
        statusLabel.Text = $"Copied {rows.Count:N0} file names.";
    }

    private async void OnByteDifferencesClicked(object? sender, EventArgs e)
    {
        if (resultListView.SelectedItems.Count != 1 || firstArchive == null || secondArchive == null)
            return;

        var row = (CpkComparisonRow)resultListView.SelectedItems[0].Tag!;
        CpkEntry? firstEntry = row.FirstEntry;
        CpkEntry? secondEntry = row.SecondEntry;
        if (firstEntry == null || secondEntry == null)
            return;

        CpkArchive first = firstArchive;
        CpkArchive second = secondArchive;
        SetBusy(true, "Reading both versions...");
        try
        {
            string report = await Task.Run(() =>
            {
                using ArchiveByteReader firstReader = first.OpenReader();
                using ArchiveByteReader secondReader = second.OpenReader();
                byte[] firstData = first.ReadEntryDecompressed(firstReader, firstEntry);
                byte[] secondData = second.ReadEntryDecompressed(secondReader, secondEntry);
                return ByteDifferenceReport.Build(row.FullPath, firstData, secondData);
            });

            statusLabel.Text = "Done.";
            using var viewer = new TextViewerForm("Byte differences - " + row.FullPath, report);
            viewer.ShowDialog(this);
        }
        catch (Exception exception)
        {
            statusLabel.Text = "Failed.";
            ShowError(exception);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task ExtractSelectedAsync(bool fromSecondArchive)
    {
        CpkArchive? sourceArchive = fromSecondArchive ? secondArchive : firstArchive;
        if (sourceArchive == null)
            return;

        List<CpkEntry> entriesToExtract = GetSelectedOrShownRows()
            .Select(row => fromSecondArchive ? row.SecondEntry : row.FirstEntry)
            .Where(entry => entry != null)
            .Select(entry => entry!)
            .ToList();

        if (entriesToExtract.Count == 0)
        {
            MessageBox.Show(this, "None of the selected files exist in that archive.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = $"Choose the folder to extract {entriesToExtract.Count:N0} files into",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string outputDirectory = dialog.SelectedPath;
        using var cancellationSource = new CancellationTokenSource();
        operationCancellation = cancellationSource;
        SetBusy(true, "Extracting...");
        var progress = new Progress<CpkOperationProgress>(ShowProgress);

        try
        {
            IReadOnlyList<string> failures = await Task.Run(() =>
                new CpkExtractor(sourceArchive).Extract(entriesToExtract, outputDirectory, progress, cancellationSource.Token));
            statusLabel.Text = "Done.";
            MessageBox.Show(this, MainForm.DescribeExtractionResult(entriesToExtract.Count, outputDirectory, failures), Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "Cancelled.";
        }
        catch (Exception exception)
        {
            statusLabel.Text = "Failed.";
            ShowError(exception);
        }
        finally
        {
            operationCancellation = null;
            SetBusy(false, null);
        }
    }

    // ---------- Busy state ----------

    private void ShowProgress(CpkOperationProgress progress)
    {
        statusLabel.Text = progress.TotalItems > 0
            ? $"{progress.Message}  ({progress.ItemsProcessed:N0}/{progress.TotalItems:N0})"
            : progress.Message;
        progressBar.Value = (int)(progress.Fraction * ProgressBarResolution);
    }

    private void SetBusy(bool busy, string? text)
    {
        isBusy = busy;
        progressBar.Value = 0;
        if (text != null)
            statusLabel.Text = text;
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        bool hasResult = comparisonResult != null && !isBusy;
        bool hasRows = hasResult && resultListView.Items.Count > 0;
        compareButton.Enabled = !isBusy;
        firstBrowseButton.Enabled = !isBusy;
        secondBrowseButton.Enabled = !isBusy;
        cancelButton.Enabled = isBusy;
        exportCsvButton.Enabled = hasRows;
        copyNamesButton.Enabled = hasRows;
        byteDifferencesButton.Enabled = hasResult && resultListView.SelectedItems.Count == 1 &&
            resultListView.SelectedItems[0].Tag is CpkComparisonRow { FirstEntry: not null, SecondEntry: not null };
        extractFromSecondButton.Enabled = hasRows;
        extractFromFirstButton.Enabled = hasRows;
    }

    private void ShowError(Exception exception) =>
        MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
}

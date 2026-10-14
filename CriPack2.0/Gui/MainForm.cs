using System.Globalization;
using CpkToolkit.Core;

namespace CpkToolkit.Gui;

/// <summary>The main window: browse a CPK's files, header and sections, extract files and repack.</summary>
public sealed class MainForm : Form
{
    private const string ApplicationTitle = "CPK Toolkit";
    private const int ProgressBarResolution = 1000;

    private readonly ListViewColumnSorter entryColumnSorter = new();

    private readonly ToolStrip toolStrip = new();
    private readonly ToolStripButton openButton = new("Open CPK...");
    private readonly ToolStripButton extractAllButton = new("Extract all...");
    private readonly ToolStripButton extractSelectedButton = new("Extract selected...");
    private readonly ToolStripButton repackButton = new("Repack from folder...");
    private readonly ToolStripButton verifyButton = new("Verify layout");
    private readonly ToolStripButton compareButton = new("Compare CPKs...");
    private readonly ToolStripButton randomPaddingButton = new("Random padding: off")
    {
        CheckOnClick = true,
        ToolTipText = "When on, the gaps between files are filled with random bytes like the original CPK, instead of zeros."
    };
    private readonly ToolStripButton ignoreManifestButton = new("Ignore manifest: off")
    {
        CheckOnClick = true,
        ToolTipText = "When on, every file present in the folder replaces its archive entry, even if its content is identical to what was extracted."
    };
    private readonly ToolStripButton cancelButton = new("Cancel");

    private readonly StatusStrip statusStrip = new();
    private readonly ToolStripStatusLabel statusLabel = new("Open a CPK file to begin.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar progressBar = new() { Minimum = 0, Maximum = ProgressBarResolution, Width = 260, Visible = false };

    private readonly TabControl tabControl = new();

    private readonly SplitContainer contentsSplit = new();
    private readonly TreeView directoryTree = new();
    private readonly TextBox filterTextBox = new();
    private readonly CheckBox includeSubfoldersCheckBox = new();
    private readonly Label entryCountLabel = new();
    private readonly ListView entryListView = new();

    private readonly TextBox summaryTextBox = new();
    private readonly ListView headerListView = new();
    private readonly ListView sectionListView = new();
    private readonly TextBox sectionDetailsTextBox = new();

    private CpkArchive? currentArchive;
    private CancellationTokenSource? operationCancellation;
    private bool isBusy;

    public MainForm()
    {
        Text = ApplicationTitle;
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        BuildToolStrip();
        BuildTabs();
        BuildStatusStrip();

        // Docking is resolved back to front, so the Fill control is added first.
        Controls.Add(tabControl);
        Controls.Add(statusStrip);
        Controls.Add(toolStrip);

        UpdateButtonStates();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (isBusy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "An operation is still running. Cancel it first, then close the window.",
                ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        base.OnFormClosing(e);
    }

    // ---------- Layout ----------

    private void BuildToolStrip()
    {
        openButton.Click += OnOpenClicked;
        extractAllButton.Click += OnExtractAllClicked;
        extractSelectedButton.Click += OnExtractSelectedClicked;
        repackButton.Click += OnRepackClicked;
        verifyButton.Click += OnVerifyClicked;
        compareButton.Click += OnCompareClicked;
        randomPaddingButton.CheckedChanged += (_, _) =>
            randomPaddingButton.Text = randomPaddingButton.Checked ? "Random padding: on" : "Random padding: off";
        ignoreManifestButton.CheckedChanged += (_, _) =>
            ignoreManifestButton.Text = ignoreManifestButton.Checked ? "Ignore manifest: on" : "Ignore manifest: off";
        cancelButton.Click += (_, _) => operationCancellation?.Cancel();
        cancelButton.Alignment = ToolStripItemAlignment.Right;

        foreach (ToolStripButton button in new[] { openButton, extractAllButton, extractSelectedButton, repackButton, randomPaddingButton, ignoreManifestButton, verifyButton, compareButton, cancelButton })
        {
            button.DisplayStyle = ToolStripItemDisplayStyle.Text;
            toolStrip.Items.Add(button);
            if (button == openButton || button == extractSelectedButton)
                toolStrip.Items.Add(new ToolStripSeparator());
        }

        toolStrip.Dock = DockStyle.Top;
        toolStrip.GripStyle = ToolStripGripStyle.Hidden;
    }

    private void BuildStatusStrip()
    {
        statusStrip.Items.Add(statusLabel);
        statusStrip.Items.Add(progressBar);
        statusStrip.Dock = DockStyle.Bottom;
    }

    private void BuildTabs()
    {
        tabControl.Dock = DockStyle.Fill;
        tabControl.TabPages.Add(BuildContentsTab());
        tabControl.TabPages.Add(BuildSummaryTab());
        tabControl.TabPages.Add(BuildHeaderTab());
        tabControl.TabPages.Add(BuildSectionsTab());
    }

    private TabPage BuildContentsTab()
    {
        var page = new TabPage("Contents");

        contentsSplit.Dock = DockStyle.Fill;
        contentsSplit.FixedPanel = FixedPanel.Panel1;
        SetInitialSplitterDistance(contentsSplit, 0.22);

        directoryTree.Dock = DockStyle.Fill;
        directoryTree.HideSelection = false;
        directoryTree.AfterSelect += (_, _) => RefreshEntryList();
        contentsSplit.Panel1.Controls.Add(directoryTree);

        var filterPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
        var filterLabel = new Label { Text = "Filter:", AutoSize = true, Left = 6, Top = 9 };
        filterTextBox.SetBounds(52, 5, 260, 24);
        filterTextBox.TextChanged += (_, _) => RefreshEntryList();
        includeSubfoldersCheckBox.Text = "Include subfolders";
        includeSubfoldersCheckBox.Checked = true;
        includeSubfoldersCheckBox.AutoSize = true;
        includeSubfoldersCheckBox.Left = 330;
        includeSubfoldersCheckBox.Top = 7;
        includeSubfoldersCheckBox.CheckedChanged += (_, _) => RefreshEntryList();
        entryCountLabel.AutoSize = true;
        entryCountLabel.Left = 480;
        entryCountLabel.Top = 9;
        filterPanel.Controls.AddRange(new Control[] { filterLabel, filterTextBox, includeSubfoldersCheckBox, entryCountLabel });

        ConfigureEntryListView();
        contentsSplit.Panel2.Controls.Add(entryListView);
        contentsSplit.Panel2.Controls.Add(filterPanel);

        page.Controls.Add(contentsSplit);
        return page;
    }

    private void ConfigureEntryListView()
    {
        entryListView.Dock = DockStyle.Fill;
        entryListView.View = View.Details;
        entryListView.FullRowSelect = true;
        entryListView.GridLines = true;
        entryListView.HideSelection = false;
        entryListView.MultiSelect = true;

        entryListView.Columns.Add("Name", 260);
        entryListView.Columns.Add("Folder", 220);
        entryListView.Columns.Add("Stored size", 100, HorizontalAlignment.Right);
        entryListView.Columns.Add("Original size", 100, HorizontalAlignment.Right);
        entryListView.Columns.Add("Compressed", 80);
        entryListView.Columns.Add("Offset", 100);
        entryListView.Columns.Add("ID", 70, HorizontalAlignment.Right);

        entryListView.ColumnClick += (_, args) =>
        {
            if (args.Column == entryColumnSorter.SortColumnIndex)
            {
                entryColumnSorter.IsAscending = !entryColumnSorter.IsAscending;
            }
            else
            {
                entryColumnSorter.SortColumnIndex = args.Column;
                entryColumnSorter.IsAscending = true;
            }

            entryListView.Sort();
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Extract selected...", null, OnExtractSelectedClicked);
        entryListView.ContextMenuStrip = contextMenu;
    }

    private TabPage BuildSummaryTab()
    {
        var page = new TabPage("Summary");
        summaryTextBox.Dock = DockStyle.Fill;
        summaryTextBox.Multiline = true;
        summaryTextBox.ReadOnly = true;
        summaryTextBox.ScrollBars = ScrollBars.Both;
        summaryTextBox.WordWrap = false;
        summaryTextBox.Font = new Font("Consolas", 10f);
        page.Controls.Add(summaryTextBox);
        return page;
    }

    private TabPage BuildHeaderTab()
    {
        var page = new TabPage("CPK header");
        headerListView.Dock = DockStyle.Fill;
        headerListView.View = View.Details;
        headerListView.FullRowSelect = true;
        headerListView.GridLines = true;
        headerListView.Columns.Add("Field", 220);
        headerListView.Columns.Add("Type", 80);
        headerListView.Columns.Add("Value", 260, HorizontalAlignment.Right);
        headerListView.Columns.Add("Hex", 200);
        page.Controls.Add(headerListView);
        return page;
    }

    private TabPage BuildSectionsTab()
    {
        var page = new TabPage("Sections");
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        SetInitialSplitterDistance(split, 0.3);

        sectionListView.Dock = DockStyle.Fill;
        sectionListView.View = View.Details;
        sectionListView.FullRowSelect = true;
        sectionListView.GridLines = true;
        sectionListView.HideSelection = false;
        sectionListView.MultiSelect = false;
        sectionListView.Columns.Add("Section", 80);
        sectionListView.Columns.Add("Offset", 110);
        sectionListView.Columns.Add("Size", 110, HorizontalAlignment.Right);
        sectionListView.Columns.Add("End", 110);
        sectionListView.Columns.Add("Position", 150);
        sectionListView.Columns.Add("Description", 520);
        sectionListView.SelectedIndexChanged += (_, _) => ShowSelectedSectionDetails();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Save raw section bytes...", null, OnSaveSectionClicked);
        sectionListView.ContextMenuStrip = contextMenu;

        sectionDetailsTextBox.Dock = DockStyle.Fill;
        sectionDetailsTextBox.Multiline = true;
        sectionDetailsTextBox.ReadOnly = true;
        sectionDetailsTextBox.ScrollBars = ScrollBars.Both;
        sectionDetailsTextBox.WordWrap = false;
        sectionDetailsTextBox.Font = new Font("Consolas", 10f);

        split.Panel1.Controls.Add(sectionListView);
        split.Panel2.Controls.Add(sectionDetailsTextBox);
        page.Controls.Add(split);
        return page;
    }

    /// <summary>
    /// A SplitContainer rejects a splitter position that does not fit its current size, and controls on tabs
    /// that have never been shown still have their default size. So the position is applied on the first real resize.
    /// </summary>
    private static void SetInitialSplitterDistance(SplitContainer split, double fraction)
    {
        void ApplyOnFirstRealSize(object? sender, EventArgs args)
        {
            int length = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
            if (length < 300)
                return;

            int distance = (int)(length * fraction);
            int maximum = length - split.Panel2MinSize - split.SplitterWidth;
            split.SplitterDistance = Math.Clamp(distance, split.Panel1MinSize, maximum);
            split.SizeChanged -= ApplyOnFirstRealSize;
        }

        split.SizeChanged += ApplyOnFirstRealSize;
    }

    // ---------- Showing an archive ----------

    private void ShowArchive(CpkArchive archive)
    {
        currentArchive = archive;
        Text = $"{ApplicationTitle} - {archive.ArchivePath}";
        summaryTextBox.Text = archive.BuildSummaryText();
        PopulateHeaderList(archive);
        PopulateSectionList(archive);
        BuildDirectoryTree(archive);
        statusLabel.Text = $"{archive.Entries.Count:N0} files, {ByteSizeFormatter.Format(archive.ArchiveLength)}";
        UpdateButtonStates();
    }

    private void PopulateHeaderList(CpkArchive archive)
    {
        headerListView.BeginUpdate();
        headerListView.Items.Clear();
        foreach (KeyValuePair<string, UtfCell> field in archive.HeaderFields)
        {
            UtfCell cell = field.Value;
            string hexText = string.Empty;
            string valueText = cell.ToDisplayString();

            if (cell.Value != null && cell.Type is UtfColumnType.Byte or UtfColumnType.UShort or UtfColumnType.UInt or UtfColumnType.ULong)
            {
                ulong number = cell.AsUInt64();
                valueText = number.ToString("N0", CultureInfo.InvariantCulture);
                hexText = $"0x{number:X}";
            }

            var item = new ListViewItem(field.Key);
            item.SubItems.Add(cell.Type.ToString());
            item.SubItems.Add(valueText);
            item.SubItems.Add(hexText);
            headerListView.Items.Add(item);
        }

        headerListView.EndUpdate();
    }

    private void PopulateSectionList(CpkArchive archive)
    {
        sectionListView.BeginUpdate();
        sectionListView.Items.Clear();
        foreach (CpkSection section in archive.Sections)
        {
            string position = section.Offset < archive.ContentOffset
                ? "Before file data"
                : "After file data";

            var item = new ListViewItem(section.DisplayName) { Tag = section };
            item.SubItems.Add($"0x{section.Offset:X8}");
            item.SubItems.Add($"{section.Size:N0}");
            item.SubItems.Add($"0x{section.EndOffset:X8}");
            item.SubItems.Add(position);
            item.SubItems.Add(section.Description);
            sectionListView.Items.Add(item);
        }

        sectionListView.EndUpdate();
        sectionDetailsTextBox.Text = "Select a section to see how it is built.";
    }

    private void ShowSelectedSectionDetails()
    {
        if (currentArchive == null || sectionListView.SelectedItems.Count == 0)
            return;

        var section = (CpkSection)sectionListView.SelectedItems[0].Tag!;
        try
        {
            sectionDetailsTextBox.Text = currentArchive.DescribeSection(section);
        }
        catch (Exception exception)
        {
            sectionDetailsTextBox.Text = $"The section could not be read: {exception.Message}";
        }
    }

    private void BuildDirectoryTree(CpkArchive archive)
    {
        directoryTree.BeginUpdate();
        directoryTree.Nodes.Clear();

        var rootNode = new TreeNode($"(all files) - {archive.Entries.Count:N0}") { Tag = string.Empty };
        var nodesByPath = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (string directoryPath in archive.Entries.Select(entry => NormalizeDirectory(entry.DirectoryName)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (directoryPath.Length == 0)
                continue;

            TreeNode parentNode = rootNode;
            string pathSoFar = string.Empty;
            foreach (string segment in directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                pathSoFar = pathSoFar.Length == 0 ? segment : pathSoFar + "/" + segment;
                if (!nodesByPath.TryGetValue(pathSoFar, out TreeNode? node))
                {
                    node = new TreeNode(segment) { Tag = pathSoFar };
                    parentNode.Nodes.Add(node);
                    nodesByPath[pathSoFar] = node;
                }

                parentNode = node;
            }
        }

        directoryTree.Nodes.Add(rootNode);
        rootNode.Expand();
        directoryTree.SelectedNode = rootNode;
        directoryTree.EndUpdate();

        RefreshEntryList();
    }

    private void RefreshEntryList()
    {
        if (currentArchive == null)
            return;

        string selectedDirectory = directoryTree.SelectedNode?.Tag as string ?? string.Empty;
        bool includeSubfolders = includeSubfoldersCheckBox.Checked;
        string filterText = filterTextBox.Text.Trim();

        var items = new List<ListViewItem>();
        foreach (CpkEntry entry in currentArchive.Entries)
        {
            string entryDirectory = NormalizeDirectory(entry.DirectoryName);
            bool isInScope = selectedDirectory.Length == 0
                ? includeSubfolders || entryDirectory.Length == 0
                : entryDirectory.Equals(selectedDirectory, StringComparison.OrdinalIgnoreCase) ||
                  (includeSubfolders && entryDirectory.StartsWith(selectedDirectory + "/", StringComparison.OrdinalIgnoreCase));

            if (!isInScope)
                continue;
            if (filterText.Length > 0 && entry.FullPath.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            items.Add(CreateEntryItem(entry, entryDirectory));
        }

        entryListView.BeginUpdate();
        entryListView.ListViewItemSorter = null;
        entryListView.Items.Clear();
        entryListView.Items.AddRange(items.ToArray());
        entryListView.ListViewItemSorter = entryColumnSorter;
        entryListView.EndUpdate();

        entryCountLabel.Text = $"{items.Count:N0} files shown";
    }

    private static ListViewItem CreateEntryItem(CpkEntry entry, string directory)
    {
        var item = new ListViewItem(entry.FileName) { Tag = entry };
        item.SubItems[0].Tag = entry.FileName;
        AddSubItem(item, directory, directory);
        AddSubItem(item, ByteSizeFormatter.Format(entry.StoredSize), entry.StoredSize);
        AddSubItem(item, ByteSizeFormatter.Format(entry.ExtractSize), entry.ExtractSize);
        AddSubItem(item, entry.IsCompressed ? "Yes" : "No", entry.IsCompressed ? 1 : 0);
        AddSubItem(item, $"0x{entry.AbsoluteOffset:X}", entry.AbsoluteOffset);
        AddSubItem(item, entry.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, entry.Id ?? -1L);
        return item;
    }

    private static void AddSubItem(ListViewItem item, string text, IComparable sortKey)
    {
        ListViewItem.ListViewSubItem subItem = item.SubItems.Add(text);
        subItem.Tag = sortKey;
    }

    private static string NormalizeDirectory(string? directoryName) =>
        (directoryName ?? string.Empty).Replace('\\', '/').Trim('/');

    // ---------- Button handlers ----------

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open CPK archive",
            Filter = "CPK archives (*.cpk)|*.cpk|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string archivePath = dialog.FileName;
        SetBusy(true, "Reading CPK tables...", allowCancel: false);
        try
        {
            CpkArchive archive = await Task.Run(() => new CpkArchive(archivePath));
            ShowArchive(archive);
        }
        catch (Exception exception)
        {
            statusLabel.Text = "Could not open the file.";
            ShowError(exception);
        }
        finally
        {
            SetBusy(false, null, allowCancel: false);
        }
    }

    private async void OnExtractAllClicked(object? sender, EventArgs e)
    {
        if (currentArchive != null)
            await ExtractAsync(currentArchive.Entries);
    }

    private async void OnExtractSelectedClicked(object? sender, EventArgs e)
    {
        List<CpkEntry> selectedEntries = entryListView.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => (CpkEntry)item.Tag!)
            .ToList();

        if (selectedEntries.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files in the Contents tab first.", ApplicationTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await ExtractAsync(selectedEntries);
    }

    private async Task ExtractAsync(IReadOnlyList<CpkEntry> entriesToExtract)
    {
        if (currentArchive == null || entriesToExtract.Count == 0)
            return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder to extract into",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        CpkArchive archive = currentArchive;
        string outputDirectory = dialog.SelectedPath;

        await RunOperationAsync("Extracting...", (progress, cancellationToken) =>
        {
            IReadOnlyList<string> failures = new CpkExtractor(archive).Extract(entriesToExtract, outputDirectory, progress, cancellationToken);
            return DescribeExtractionResult(entriesToExtract.Count, outputDirectory, failures);
        });
    }

    internal static string DescribeExtractionResult(int requestedCount, string outputDirectory, IReadOnlyList<string> failures)
    {
        string message = $"Extracted {requestedCount - failures.Count:N0} files to:\n{outputDirectory}";
        if (failures.Count == 0)
            return message;

        return message +
            $"\n\n{failures.Count:N0} compressed file(s) could not be decompressed. Their stored bytes were saved in the " +
            $"'{CpkExtractor.FailedFilesFolderName}' folder (extension .crilayla).\n\nFirst failures:\n" +
            string.Join("\n", failures.Take(5));
    }

    private async void OnRepackClicked(object? sender, EventArgs e)
    {
        if (currentArchive == null)
            return;

        using var folderDialog = new FolderBrowserDialog
        {
            Description = "Choose the folder that contains the modified files",
            UseDescriptionForTitle = true
        };
        if (folderDialog.ShowDialog(this) != DialogResult.OK)
            return;

        using var saveDialog = new SaveFileDialog
        {
            Title = "Save the new CPK as",
            Filter = "CPK archives (*.cpk)|*.cpk",
            FileName = Path.GetFileNameWithoutExtension(currentArchive.ArchivePath) + "_modified.cpk"
        };
        if (saveDialog.ShowDialog(this) != DialogResult.OK)
            return;

        CpkArchive archive = currentArchive;
        string modifiedDirectory = folderDialog.SelectedPath;
        string outputPath = saveDialog.FileName;
        bool useRandomPadding = randomPaddingButton.Checked;
        bool ignoreManifest = ignoreManifestButton.Checked;

        await RunOperationAsync("Repacking...", (progress, cancellationToken) =>
        {
            var detector = new ModifiedFileDetector(modifiedDirectory, ignoreManifest);
            IReadOnlyList<string> unmatchedFiles = detector.FindFilesNotInArchive(archive);
            string? manifestMismatch = detector.DescribeManifestSourceMismatch(archive);
            var decisionLog = new RepackDecisionLog();

            CpkRepackReport report = new CpkRepacker(archive).Repack(
                outputPath,
                entry =>
                {
                    ReplacementDecision decision = detector.Decide(entry, cancellationToken);
                    decisionLog.Add(decision);
                    return decision.ReplacementPath;
                },
                progress,
                cancellationToken,
                fillPaddingWithRandomBytes: useRandomPadding);

            // Re-open the result and check that its layout is sound.
            IReadOnlyList<string> problems = new CpkArchive(outputPath).ValidateLayout();

            string reportPath = Path.ChangeExtension(outputPath, null) + "_repack_report.csv";
            string? reportError = null;
            try
            {
                decisionLog.WriteCsv(reportPath);
            }
            catch (IOException exception)
            {
                reportError = exception.Message;
            }
            catch (UnauthorizedAccessException exception)
            {
                reportError = exception.Message;
            }

            IReadOnlyList<ReplacementDecision> skipped = decisionLog.SkippedAsUnedited;

            var message = new System.Text.StringBuilder();
            message.AppendLine($"Created {report.OutputPath}");
            message.AppendLine($"Size: {ByteSizeFormatter.Format(report.OutputLength)}");
            message.AppendLine($"Replaced files: {report.ReplacedEntryCount:N0}");
            message.AppendLine($"Copied unchanged: {report.KeptEntryCount:N0}");
            message.AppendLine(useRandomPadding ? "Padding between files: random bytes" : "Padding between files: zeros");
            message.AppendLine();
            message.AppendLine($"Files in the folder that match an archive entry: {decisionLog.FilesPresentInFolder:N0}");
            message.AppendLine($"  used as replacements: {decisionLog.ReplacedCount:N0}");
            message.AppendLine($"  skipped because their content is identical to the extracted original (SHA-256): {skipped.Count:N0}");
            if (skipped.Count > 0)
            {
                message.AppendLine("  (the archive's original data was kept for these; turn on \"Ignore manifest\" to force them in)");
                foreach (ReplacementDecision decision in skipped.Take(15))
                    message.AppendLine($"    - {decision.Entry.FullPath}  [{decision.Kind}]");
                if (skipped.Count > 15)
                    message.AppendLine($"    ... and {skipped.Count - 15:N0} more");
            }

            message.AppendLine($"Archive entries with no file in the folder (left exactly as they were): {decisionLog.CountOf(ReplacementDecisionKind.NoFileInFolder):N0}");
            message.AppendLine(reportError == null
                ? $"\nPer-file report: {reportPath}"
                : $"\nThe per-file report could not be written: {reportError}");
            if (detector.ManifestWasIgnored)
                message.AppendLine("\nThe extraction manifest was ignored, so every file present in the folder was treated as modified.");
            else if (!detector.HasManifest)
                message.AppendLine("\nNo extraction manifest was found, so every file present in the folder was treated as modified.");
            if (manifestMismatch != null)
                message.AppendLine($"\nWARNING: {manifestMismatch}");
            if (unmatchedFiles.Count > 0)
                message.AppendLine($"\n{unmatchedFiles.Count:N0} files in the folder are not part of the archive and were ignored (first: {unmatchedFiles[0]}).");
            foreach (string note in report.Notes)
                message.AppendLine($"\nNote: {note}");
            message.AppendLine(problems.Count == 0
                ? "\nLayout check of the new file: no problems found."
                : $"\nLayout check found {problems.Count} problem(s):\n" + string.Join("\n", problems.Take(15)));
            return message.ToString();
        });
    }

    private void OnCompareClicked(object? sender, EventArgs e)
    {
        using var compareForm = new CompareForm(currentArchive?.ArchivePath);
        compareForm.ShowDialog(this);
    }

    private void OnVerifyClicked(object? sender, EventArgs e)
    {
        if (currentArchive == null)
            return;

        IReadOnlyList<string> problems = currentArchive.ValidateLayout();
        string message = problems.Count == 0
            ? "No problems found: all sections and files lie inside the archive, are aligned and do not overlap."
            : $"{problems.Count} problem(s) found:\n\n" + string.Join("\n", problems.Take(30));
        MessageBox.Show(this, message, ApplicationTitle, MessageBoxButtons.OK,
            problems.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void OnSaveSectionClicked(object? sender, EventArgs e)
    {
        if (currentArchive == null || sectionListView.SelectedItems.Count == 0)
            return;

        var section = (CpkSection)sectionListView.SelectedItems[0].Tag!;
        using var dialog = new SaveFileDialog
        {
            Title = "Save raw section bytes",
            FileName = section.DisplayName + ".bin"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.WriteAllBytes(dialog.FileName, currentArchive.ReadSectionBytes(section));
            statusLabel.Text = $"Saved {section.DisplayName} to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    // ---------- Running background work ----------

    private async Task RunOperationAsync(string busyText, Func<IProgress<CpkOperationProgress>, CancellationToken, string> work)
    {
        using var cancellationSource = new CancellationTokenSource();
        operationCancellation = cancellationSource;
        SetBusy(true, busyText, allowCancel: true);

        var progress = new Progress<CpkOperationProgress>(ShowProgress);
        try
        {
            string resultMessage = await Task.Run(() => work(progress, cancellationSource.Token));
            statusLabel.Text = "Done.";
            MessageBox.Show(this, resultMessage, ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            SetBusy(false, null, allowCancel: false);
        }
    }

    private void ShowProgress(CpkOperationProgress progress)
    {
        statusLabel.Text = progress.TotalItems > 0
            ? $"{progress.Message}  ({progress.ItemsProcessed:N0}/{progress.TotalItems:N0})"
            : progress.Message;
        progressBar.Value = (int)(progress.Fraction * ProgressBarResolution);
    }

    private void SetBusy(bool busy, string? text, bool allowCancel)
    {
        isBusy = busy;
        progressBar.Visible = busy;
        progressBar.Value = 0;
        cancelButton.Enabled = busy && allowCancel;
        if (text != null)
            statusLabel.Text = text;
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        bool hasArchive = currentArchive != null;
        openButton.Enabled = !isBusy;
        extractAllButton.Enabled = hasArchive && !isBusy;
        extractSelectedButton.Enabled = hasArchive && !isBusy;
        repackButton.Enabled = hasArchive && !isBusy;
        verifyButton.Enabled = hasArchive && !isBusy;
        randomPaddingButton.Enabled = !isBusy;
        ignoreManifestButton.Enabled = !isBusy;
        compareButton.Enabled = !isBusy;
        if (!isBusy)
            cancelButton.Enabled = false;
    }

    private void ShowError(Exception exception) =>
        MessageBox.Show(this, exception.Message, ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
}

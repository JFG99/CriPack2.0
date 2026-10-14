namespace CpkToolkit.Gui;

/// <summary>A simple window that shows read-only text in a fixed-width font and can save it to a file.</summary>
public sealed class TextViewerForm : Form
{
    private readonly TextBox textBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        Dock = DockStyle.Fill
    };

    public TextViewerForm(string title, string text)
    {
        Text = title;
        Width = 1000;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;
        textBox.Text = text;

        var saveButton = new Button { Text = "Save as text file...", AutoSize = true, Dock = DockStyle.Bottom };
        saveButton.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "report.txt" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                File.WriteAllText(dialog.FileName, textBox.Text);
        };

        Controls.Add(textBox);
        Controls.Add(saveButton);
        textBox.Select(0, 0);
    }
}

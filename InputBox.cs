namespace GhostFeed;

static class InputBox
{
    /// <returns>The entered text, or null if cancelled.</returns>
    public static string? Show(IWin32Window owner, string prompt, string initial)
    {
        using var f = new Form
        {
            Text = "GhostFeed",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(420, 108),
        };
        var label = new Label { Text = prompt, Left = 12, Top = 12, Width = 396, AutoSize = false, Height = 20 };
        var box = new TextBox { Text = initial, Left = 12, Top = 36, Width = 396 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 252, Top = 70, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 333, Top = 70, Width = 75 };
        f.Controls.AddRange(new Control[] { label, box, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

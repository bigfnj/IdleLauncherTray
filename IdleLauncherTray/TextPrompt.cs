using System.Drawing;
using System.Windows.Forms;

namespace IdleLauncherTray;

/// <summary>
/// Tiny WinForms input prompt helper (so we don't need Microsoft.VisualBasic's InputBox).
/// </summary>
internal static class TextPrompt
{
    /// <summary>
    /// Shows a modal dialog that lets the user enter a single line of text.
    /// Returns true if the user clicked OK.
    /// </summary>
    public static bool Show(string title, string message, ref string value)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(560, 155)
        };

        var lbl = new Label
        {
            Left = 12,
            Top = 12,
            Width = form.ClientSize.Width - 24,
            Height = 40,
            Text = message
        };

        var tb = new TextBox
        {
            Left = 12,
            Top = 56,
            Width = form.ClientSize.Width - 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = value ?? string.Empty
        };

        var btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 90,
            Height = 28,
            Left = form.ClientSize.Width - (12 + 90 + 10 + 90),
            Top = 104,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 90,
            Height = 28,
            Left = form.ClientSize.Width - (12 + 90),
            Top = 104,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };

        form.Controls.Add(lbl);
        form.Controls.Add(tb);
        form.Controls.Add(btnOk);
        form.Controls.Add(btnCancel);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        form.Shown += (_, _) =>
        {
            try
            {
                tb.Focus();
                tb.SelectAll();
            }
            catch
            {
                // Ignore.
            }
        };

        var res = form.ShowDialog();
        if (res == DialogResult.OK)
        {
            value = tb.Text ?? string.Empty;
            return true;
        }

        return false;
    }
}

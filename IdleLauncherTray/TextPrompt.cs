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
        // Deliberately OWNERLESS, despite the obvious-looking fix of passing an owner to
        // ShowDialog. There are two separate problems here and ownership solves neither:
        //
        //   Reachability. The dialog is raised from a tray menu, so there is no window of ours
        //   for it to sit above -- it opens behind whatever the user was working in. Only
        //   TopMost fixes z-order against ANOTHER application's windows.
        //
        //   Discoverability. Once it is behind something, the user needs a way back to it, and
        //   that means a taskbar button. Only ShowInTaskbar fixes that.
        //
        // The two rejected alternatives, for the next person who reads the backlog entry:
        //
        //   ShowDialog(GetForegroundWindow()) hands the z-order AND the lifetime of our modal
        //   to an HWND owned by a different process. This machine runs entirely over RDP, where
        //   the session is torn down several times a day; an owner that dies while we are modal
        //   is exactly the stranded, un-dismissable dialog this is meant to prevent.
        //
        //   A hidden owner form makes discoverability strictly WORSE. An owned window without
        //   WS_EX_APPWINDOW gets no taskbar button at all, and Alt-Tab lists the OWNER instead
        //   of the owned window -- and our owner would be the hidden one. The dialog would then
        //   be reachable by neither route.
        //
        // Manual smoke test only: every claim above is about window-manager behaviour, and a
        // test that asserts TopMost == true after the line that sets TopMost = true proves
        // nothing except that assignment works.
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            ShowInTaskbar = true,
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
                // TopMost puts the window in front; it does not give it the keyboard. Activate
                // must come first, because Focus() on a control of an inactive form sets the
                // form's internal "active control" and nothing else -- the user would be typing
                // into whatever app still held focus.
                form.Activate();
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

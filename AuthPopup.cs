using Microsoft.Web.WebView2.WinForms;

namespace GhostFeed;

/// <summary>Plain window for third-party sign-in popups (e.g. "Sign in with Google") opened by x.com.</summary>
sealed class AuthPopup : Form
{
    public WebView2 View { get; } = new() { Dock = DockStyle.Fill };

    public AuthPopup(Form? owner)
    {
        Text = "GhostFeed — Sign in";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 700);
        ShowInTaskbar = true;
        TopMost = true; // main window is topmost; keep the sign-in above it
        if (owner is not null) Owner = owner;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(View);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        View.Dispose();
        base.OnFormClosed(e);
    }
}

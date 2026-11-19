using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GhostFeed;

/// <summary>Hosts player.html: one visible video element plus one preloading the next item.</summary>
sealed class PlayerHost
{
    public WebView2 View { get; } = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };

    /// <summary>Messages from the page, e.g. { type: "ended" | "error" | "key" | "wheel" | "dbl" | "menu" | "drag" | "paused" }.</summary>
    public event Action<JsonElement>? Message;

    public async Task InitAsync(CoreWebView2Environment env)
    {
        await View.EnsureCoreWebView2Async(env);
        var c = View.CoreWebView2;
        var st = c.Settings;
        st.AreDefaultContextMenusEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        st.IsZoomControlEnabled = false;
        st.IsStatusBarEnabled = false;
        st.IsSwipeNavigationEnabled = false;
        st.IsPinchZoomEnabled = false;
        st.AreDevToolsEnabled = Log.Verbose;

        var ready = new TaskCompletionSource();
        c.WebMessageReceived += (_, e) =>
        {
            JsonElement msg;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                msg = doc.RootElement.Clone();
            }
            catch { return; }
            if (msg.TryGetProperty("type", out var t) && t.GetString() == "ready") ready.TrySetResult();
            Message?.Invoke(msg);
        };

        c.NavigateToString(LoadHtml());
        await Task.WhenAny(ready.Task, Task.Delay(5000));
    }

    public void Send(object msg)
    {
        try { View.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); }
        catch (Exception ex) { Log.Error("player send", ex); }
    }

    static string LoadHtml()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("player.html")
            ?? throw new InvalidOperationException("player.html resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

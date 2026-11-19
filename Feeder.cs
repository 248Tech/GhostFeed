using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GhostFeed;

/// <summary>
/// The x.com browser. Normally sits hidden behind the player; its GraphQL timeline
/// responses are captured host-side and turned into videos. Scrolling it loads more.
/// </summary>
sealed class Feeder
{
    const int MaxEmptyScrolls = 25;

    // The home tabs share https://x.com/home, so they get pseudo-URLs: navigate to /home, click the tab,
    // and only accept that tab's timeline operation (the page loads whichever tab was last used first).
    public const string ForYouUrl = "x-home:foryou";
    public const string FollowingUrl = "x-home:following";

    readonly Settings _s;
    string? _opFilter;      // only accept videos from this GraphQL operation
    string? _pendingTab;    // home tab to click once the page has loaded
    CoreWebView2Environment? _env;
    bool _visible;
    string _lastSource = "";
    DateTime _lastScroll = DateTime.MinValue;
    int _scrollsSinceItems;

    public WebView2 View { get; } = new() { Dock = DockStyle.Fill };
    public bool Exhausted => _scrollsSinceItems >= MaxEmptyScrolls;

    public event Action<List<VideoItem>, string>? ItemsFound;   // items, GraphQL operation name
    public event Action<string>? SourceChanged;                  // navigated to a feed-like page
    public event Action? LoginRequired;

    public Feeder(Settings s) => _s = s;

    public async Task InitAsync(CoreWebView2Environment env, string url)
    {
        _env = env;
        await View.EnsureCoreWebView2Async(env);
        var c = View.CoreWebView2;
        c.Settings.IsStatusBarEnabled = false;
        c.IsMuted = true;

        c.WebResourceResponseReceived += OnResponse;
        // While hidden, don't let the timeline download its own inline videos: saves bandwidth.
        c.AddWebResourceRequestedFilter("https://video.twimg.com/*", CoreWebView2WebResourceContext.All);
        c.WebResourceRequested += OnRequest;
        c.SourceChanged += (_, _) => OnSourceChanged();
        c.NavigationCompleted += (_, e) => { if (e.IsSuccess) ClickPendingTab(); };
        c.NewWindowRequested += OnNewWindow;

        Navigate(url);
    }

    public void Navigate(string url)
    {
        _scrollsSinceItems = 0;
        _lastSource = "";
        _opFilter = null;
        _pendingTab = null;
        if (url == ForYouUrl || url == FollowingUrl)
        {
            bool following = url == FollowingUrl;
            _pendingTab = following ? "following" : "foryou";
            _opFilter = following ? "HomeLatestTimeline" : "HomeTimeline";
            url = "https://x.com/home";
        }
        View.CoreWebView2?.Navigate(url);
    }

    void ClickPendingTab()
    {
        if (_pendingTab is null || View.CoreWebView2 is null) return;
        bool following = _pendingTab == "following";
        _pendingTab = null;
        // Match the tab by label, falling back to position (For you = 0, Following = 1) for other UI languages.
        string js = $$"""
            (() => {
              const want = {{(following ? "'following'" : "'for you'")}}, idx = {{(following ? 1 : 0)}};
              let n = 0;
              const t = setInterval(() => {
                let tabs = [...document.querySelectorAll('[data-testid="primaryColumn"] [role="tab"]')];
                if (!tabs.length) tabs = [...document.querySelectorAll('[role="tab"]')];
                const el = tabs.find(x => x.textContent.trim().toLowerCase() === want) || tabs[idx];
                if (el) { clearInterval(t); if (el.getAttribute('aria-selected') !== 'true') el.click(); }
                if (++n > 60) clearInterval(t);
              }, 250);
            })();
            """;
        _ = View.CoreWebView2.ExecuteScriptAsync(js);
    }

    public string CurrentUrl => View.CoreWebView2?.Source ?? "";

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) _opFilter = null; // the user is browsing: whatever they open is the feed
        if (View.CoreWebView2 is { } c) c.IsMuted = !visible;
    }

    /// <summary>Scroll the hidden timeline so X fetches the next page. Rate-limited.</summary>
    public void LoadMore()
    {
        var c = View.CoreWebView2;
        if (c is null || _visible || Exhausted) return;
        if ((DateTime.UtcNow - _lastScroll).TotalSeconds < 2) return;
        _lastScroll = DateTime.UtcNow;
        _scrollsSinceItems++;
        // Every few tries jump to the very bottom in case X's loader sentinel was skipped over.
        string js = _scrollsSinceItems % 4 == 0
            ? "window.scrollTo(0, document.documentElement.scrollHeight);"
            : "window.scrollBy(0, Math.max(window.innerHeight * 3, 1500));";
        _ = c.ExecuteScriptAsync(js);
    }

    /// <summary>
    /// Sign-in popups (Google, Apple, X) must stay inside this browser environment: they report back
    /// to x.com through window.opener and share its cookies. Everything else opens in the default browser.
    /// </summary>
    async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (_env is null || !IsAuthUrl(e.Uri))
        {
            e.Handled = true;
            OpenExternal(e.Uri);
            return;
        }

        Log.Info("auth popup: " + e.Uri);
        var deferral = e.GetDeferral();
        try
        {
            var popup = new AuthPopup(View.FindForm());
            popup.Show();
            await popup.View.EnsureCoreWebView2Async(_env);
            var pc = popup.View.CoreWebView2;
            pc.WindowCloseRequested += (_, _) => popup.Close();
            // Nested popups from the sign-in page stay in the same window.
            pc.NewWindowRequested += (_, ne) => { ne.Handled = true; pc.Navigate(ne.Uri); };
            e.NewWindow = pc;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error("auth popup", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    static readonly string[] AuthHosts =
        { "google.com", "youtube.com", "apple.com", "x.com", "twitter.com" };

    static bool IsAuthUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        AuthHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                           u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_visible || _env is null) return;
        e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
    }

    async void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        string uri = e.Request.Uri;
        int at = uri.IndexOf("/i/api/graphql/", StringComparison.Ordinal);
        if (at < 0 || e.Response.StatusCode != 200) return;
        string op = OpName(uri, at);
        if (_opFilter is not null && op != _opFilter)
        {
            Log.Info($"graphql {op}: skipped (want {_opFilter})");
            return;
        }
        try
        {
            using var stream = await e.Response.GetContentAsync();
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync();
            var items = await Task.Run(() => FeedParser.Parse(json, _s));
            Log.Info($"graphql {op}: {json.Length:N0} bytes, {items.Count} videos");
            if (Log.Verbose) DumpResponse(op, json);
            if (items.Count > 0) _scrollsSinceItems = 0;
            ItemsFound?.Invoke(items, op);
        }
        catch (Exception ex)
        {
            Log.Info($"graphql {op}: body unavailable ({ex.Message})");
        }
    }

    void OnSourceChanged()
    {
        string url = CurrentUrl;
        if (url == _lastSource) return;
        _lastSource = url;
        Log.Info("feeder source: " + url);
        if (IsLogin(url)) LoginRequired?.Invoke();
        else if (IsFeedUrl(url)) SourceChanged?.Invoke(url);
    }

    /// <summary>Verbose mode keeps the latest raw response per operation, for fixing the parser when X changes shape.</summary>
    static void DumpResponse(string op, string json)
    {
        try
        {
            var dir = Path.Combine(Settings.Dir, "dumps");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(op.Where(char.IsLetterOrDigit));
            File.WriteAllText(Path.Combine(dir, (safe.Length > 0 ? safe : "op") + ".json"), json);
        }
        catch { }
    }

    static string OpName(string uri, int at)
    {
        // .../i/api/graphql/<hash>/<OperationName>?variables=...
        var path = uri[(at + "/i/api/graphql/".Length)..];
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        int slash = path.IndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    static bool IsXHost(Uri u) =>
        u.Host.Equals("x.com", StringComparison.OrdinalIgnoreCase) ||
        u.Host.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase) ||
        u.Host.EndsWith("twitter.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsLogin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsXHost(u)) return false;
        string p = u.AbsolutePath.TrimEnd('/');
        return p.Length == 0 || p.StartsWith("/i/flow/login") || p.StartsWith("/i/flow/signup") ||
               p == "/login" || p == "/logout";
    }

    static readonly string[] NotFeeds =
        { "/status/", "/i/flow", "/compose", "/settings", "/messages", "/notifications", "/login", "/logout", "/i/premium" };

    public static bool IsFeedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsXHost(u)) return false;
        string p = u.AbsolutePath.ToLowerInvariant();
        return p != "/" && !NotFeeds.Any(p.Contains);
    }

    public static void OpenExternal(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open " + url, ex); }
    }
}

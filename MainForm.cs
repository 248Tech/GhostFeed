using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace GhostFeed;

/// <summary>
/// Borderless always-on-top overlay. Holds two WebView2s stacked in the same client area:
/// the player (normally on top) and the x.com feeder (shown on demand with Ctrl+Alt+B).
/// </summary>
sealed class MainForm : Form
{
    const int LowWater = 5;          // keep at least this many unplayed videos queued
    const int MaxConsecutiveErrors = 6;

    readonly Settings _s;
    readonly VideoQueue _q = new();
    readonly Feeder _feeder;
    readonly PlayerHost _player = new();
    readonly System.Windows.Forms.Timer _feedTimer = new() { Interval = 1500 };
    HotkeyManager? _hk;

    string _sourceUrl;
    bool _ready, _browserVisible, _clickThrough, _hidden, _paused = true, _menuOpen, _fullscreen;
    Rectangle _rectBeforeFull;
    bool _wasPlaying, _sourceChangedWhileBrowsing, _autoShownForLogin;
    int _errors;
    string? _preloadedUrl, _playingUrl;
    DateTime _startedAt = DateTime.UtcNow;
    Rectangle _rectBeforeBrowser;

    public MainForm(string[] args)
    {
        _s = Settings.Load();
        Log.Verbose = _s.VerboseLog || args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        _sourceUrl = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? _s.SourceUrl;
        _clickThrough = _s.ClickThroughOnStart;

        Text = "GhostFeed";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = _s.ShowInTaskbar;
        TopMost = _s.TopMost;
        MinimumSize = new Size(160, 120);
        Bounds = InitialBounds();
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _feeder = new Feeder(_s);
        Controls.Add(_feeder.View);
        Controls.Add(_player.View);
        _player.View.BringToFront();
    }

    // ------------------------------------------------------------------ window plumbing

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED;
            if (_s is { ShowInTaskbar: false }) cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // _s is null during base ctor // also keeps it out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyOpacity();
        ApplyClickThrough();
        _hk?.Dispose();
        _hk = new HotkeyManager(Handle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.SetTopMost(Handle, _s.TopMost);
        var failed = _hk!.Register(_s.Hotkeys);

        CoreWebView2Environment env;
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostFeed", "WebView2");
            var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            env = await CoreWebView2Environment.CreateAsync(null, dataDir, opts);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "GhostFeed needs the Microsoft Edge WebView2 Runtime.\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "GhostFeed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        await _player.InitAsync(env);
        _player.Message += OnPlayerMessage;
        PushState();
        Status("Loading feed…");

        _feeder.ItemsFound += OnItems;
        _feeder.SourceChanged += OnSourceChanged;
        _feeder.LoginRequired += OnLoginRequired;
        await _feeder.InitAsync(env, _sourceUrl);

        _feedTimer.Tick += (_, _) => FeedTick();
        _feedTimer.Start();
        _ready = true;
        _startedAt = DateTime.UtcNow;

        if (failed.Count > 0) Flash("Hotkeys unavailable: " + string.Join(", ", failed));
        if (_clickThrough) Flash($"Click-through ON — {Hk(Act.ClickThrough)} to exit");
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_HOTKEY:
                if (_hk is not null && _hk.TryGet((int)m.WParam, out var act)) Do(act);
                return;
            case Native.WM_SIZING:
                base.WndProc(ref m);
                KeepAspect((int)m.WParam, m.LParam);
                m.Result = 1;
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var r = _fullscreen ? _rectBeforeFull : _browserVisible ? _rectBeforeBrowser : Bounds;
        if (!_hidden && WindowState == FormWindowState.Normal)
        {
            _s.X = r.X; _s.Y = r.Y; _s.Width = r.Width; _s.Height = r.Height;
        }
        _s.Save();
        _feedTimer.Stop();
        _hk?.Dispose();
        base.OnFormClosing(e);
    }

    Rectangle InitialBounds()
    {
        var size = new Size(_s.Width, _s.Height);
        if (_s.X != int.MinValue)
        {
            var r = new Rectangle(new Point(_s.X, _s.Y), size);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) return r;
        }
        var wa = Screen.PrimaryScreen!.WorkingArea;
        size = new Size(Math.Min(size.Width, wa.Width), Math.Min(size.Height, wa.Height));
        return new Rectangle(wa.Right - size.Width - 24, wa.Bottom - size.Height - 24, size.Width, size.Height);
    }

    void ApplyOpacity()
    {
        if (!IsHandleCreated) return;
        byte a = _browserVisible ? (byte)255 : (byte)(_s.Opacity * 255 / 100);
        Native.SetLayeredWindowAttributes(Handle, 0, a, Native.LWA_ALPHA);
    }

    void ApplyClickThrough()
    {
        if (!IsHandleCreated) return;
        bool on = _clickThrough && !_browserVisible && !_menuOpen; // the fly-out menu needs the mouse
        int ex = Native.GetExStyle(Handle);
        int want = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        if (want != ex) Native.SetExStyle(Handle, want);
    }

    /// <summary>Edge-drag resizing keeps the current video's aspect ratio unless Shift is held.</summary>
    void KeepAspect(int edge, IntPtr lp)
    {
        double aspect = _q.Current?.Aspect ?? 0;
        if (aspect <= 0 || _browserVisible || (ModifierKeys & Keys.Shift) != 0) return;
        var r = Marshal.PtrToStructure<Native.RECT>(lp);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        switch (edge)
        {
            case Native.WMSZ_LEFT:
            case Native.WMSZ_RIGHT:
                r.Bottom = r.Top + (int)Math.Round(w / aspect);
                break;
            case Native.WMSZ_TOP:
            case Native.WMSZ_BOTTOM:
                r.Right = r.Left + (int)Math.Round(h * aspect);
                break;
            case Native.WMSZ_TOPLEFT:
            case Native.WMSZ_TOPRIGHT:
                r.Top = r.Bottom - (int)Math.Round(w / aspect);
                break;
            default: // bottom corners
                r.Bottom = r.Top + (int)Math.Round(w / aspect);
                break;
        }
        Marshal.StructureToPtr(r, lp, false);
    }

    /// <summary>Reshape to the video's aspect ratio, keeping the window's area and its nearest screen corner.</summary>
    void FitTo(double aspect)
    {
        if (aspect <= 0 || _fullscreen || WindowState != FormWindowState.Normal) return;
        var r = Bounds;
        var wa = Screen.FromRectangle(r).WorkingArea;
        double area = (double)r.Width * r.Height;
        int w = (int)Math.Round(Math.Sqrt(area * aspect));
        int h = (int)Math.Round(w / aspect);
        if (w > wa.Width) { w = wa.Width; h = (int)(w / aspect); }
        if (h > wa.Height) { h = wa.Height; w = (int)(h * aspect); }

        bool right = r.Left + r.Width / 2 > wa.Left + wa.Width / 2;
        bool bottom = r.Top + r.Height / 2 > wa.Top + wa.Height / 2;
        int x = right ? r.Right - w : r.Left;
        int y = bottom ? r.Bottom - h : r.Top;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        var next = new Rectangle(x, y, w, h);
        if (next != r) Bounds = next;
    }

    // ------------------------------------------------------------------ feed

    void OnItems(List<VideoItem> items, string op)
    {
        int added = _q.Add(items);
        if (added == 0) return;
        Log.Info($"queue +{added} from {op} (pos {_q.Index + 1}/{_q.Count})");
        _errors = 0;

        if (_browserVisible) return; // start once the user hides the browser
        if (_q.Current is null || _waitingForMore)
        {
            _waitingForMore = false;
            PlayNext();
        }
        else Preload();
    }
    bool _waitingForMore;

    void OnSourceChanged(string url)
    {
        if (_autoShownForLogin)
        {
            // X lands on /home after a successful login; anything else (2FA, account checks) is still the login flow.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.AbsolutePath.TrimEnd('/') != "/home") return;
            _autoShownForLogin = false;
            HideBrowser();
            Navigate(_sourceUrl);
            return;
        }
        if (!_browserVisible) return; // redirects while hidden don't change the source
        _q.ResetUpcoming();
        _preloadedUrl = null;
        _sourceUrl = url;
        _s.SourceUrl = url;
        _sourceChangedWhileBrowsing = true;
    }

    void OnLoginRequired()
    {
        Status($"Log in to X in the browser view.\n({Hk(Act.Browser)} toggles it)", clear: true);
        if (!_browserVisible)
        {
            _autoShownForLogin = true;
            ShowBrowser();
        }
    }

    void Navigate(string url)
    {
        _sourceUrl = url;
        _s.SourceUrl = url;
        _q.ResetUpcoming();
        _preloadedUrl = null;
        _feeder.Navigate(url);
        if (_q.Current is null) Status("Loading feed…");
        PushUi();
    }

    void FeedTick()
    {
        if (!_ready || _browserVisible || _hidden) return;
        if (_q.Remaining < LowWater) _feeder.LoadMore();

        if (_q.Count == 0 && (DateTime.UtcNow - _startedAt).TotalSeconds > 15)
            Status(_feeder.Exhausted
                ? $"No videos found on this page.\n{Hk(Act.Browser)} to browse, or pick a source from the right-click menu."
                : "Looking for videos…");
        else if (_waitingForMore && _feeder.Exhausted)
            Flash("End of feed");
    }

    // ------------------------------------------------------------------ playback

    void PlayNext()
    {
        var it = _q.Next();
        if (it is null)
        {
            _waitingForMore = true;
            if (_q.Count == 0) Status("Loading feed…");
            else Flash("Loading more…");
            _feeder.LoadMore();
            return;
        }
        Play(it);
    }

    void PlayPrev()
    {
        var it = _q.Prev();
        if (it is null) { Flash("Start of feed"); return; }
        Play(it);
    }

    void Play(VideoItem it)
    {
        var next = _q.PeekNext;
        _playingUrl = UrlFor(it);
        _preloadedUrl = next is null ? null : UrlFor(next);
        _player.Send(new
        {
            cmd = "load",
            item = new { url = _playingUrl, poster = it.Poster, text = it.Text },
            next = _preloadedUrl,
            osd = Headline(it),
        });
        _paused = false;
        if (_s.FitToVideo && !_browserVisible) FitTo(it.Aspect);
        if (_q.Remaining < LowWater) _feeder.LoadMore();
        PushUi();
    }

    void Preload()
    {
        var next = _q.PeekNext;
        if (next is null) return;
        string url = UrlFor(next);
        if (url == _preloadedUrl) return;
        _preloadedUrl = url;
        _player.Send(new { cmd = "preload", url });
    }

    /// <summary>Fullscreen gets the best rendition X has (up to 4K); the window respects the quality cap.</summary>
    VideoVariant VariantFor(VideoItem it) =>
        _fullscreen && _s.FullscreenBest ? it.Best : it.VariantFor(_s.MaxBitrate);

    string UrlFor(VideoItem it) => VariantFor(it).Url;

    /// <summary>Swap the playing clip to the rendition the current mode wants, keeping position (no restart).</summary>
    void UpgradeCurrent()
    {
        if (_q.Current is not { } it) return;
        string url = UrlFor(it);
        if (url == _playingUrl) return;
        _playingUrl = url;
        _preloadedUrl = null; // the page reuses its preload slot for the swap
        _player.Send(new { cmd = "upgrade", url });
    }

    string Headline(VideoItem it) =>
        $"@{it.Author} · {_q.Index + 1}/{_q.Count}{(it.IsGif ? " · GIF" : "")}{(_s.Autoplay ? "" : " · loop")}";

    void TogglePlay()
    {
        if (_q.Current is null) { PlayNext(); return; }
        _player.Send(new { cmd = "toggle" });
    }

    void Seek(double delta)
    {
        _player.Send(new { cmd = "seek", delta });
        Flash(delta > 0 ? $"+{delta:0}s" : $"{delta:0}s");
    }

    void PushState()
    {
        _player.Send(new { cmd = "state", state = new { volume = _s.Volume / 100.0, muted = _s.Muted, loop = !_s.Autoplay, scrub = _s.ShowScrubber } });
        PushUi();
    }

    void ChangeVolume(int d)
    {
        _s.Volume = Math.Clamp(_s.Volume + d, 0, 100);
        if (d > 0) _s.Muted = false;
        PushState();
        Flash($"Volume {_s.Volume}%");
    }

    void ToggleMute()
    {
        _s.Muted = !_s.Muted;
        PushState();
        Flash(_s.Muted ? "Muted" : $"Volume {_s.Volume}%");
    }

    void ToggleAutoplay()
    {
        _s.Autoplay = !_s.Autoplay;
        PushState();
        Flash(_s.Autoplay ? "Autoplay ON" : "Autoplay OFF — looping current video");
    }

    void ToggleScrubber()
    {
        _s.ShowScrubber = !_s.ShowScrubber;
        PushState();
        Flash(_s.ShowScrubber ? "Scrub bar ON" : "Scrub bar OFF");
    }

    void SetOpacity(int pct)
    {
        _s.Opacity = Math.Clamp(pct, 10, 100);
        ApplyOpacity();
        Flash($"Opacity {_s.Opacity}%");
        PushUi();
    }

    void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ApplyClickThrough();
        Flash(on ? $"Click-through ON — {Hk(Act.ClickThrough)} to exit" : "Click-through OFF");
        PushUi();
    }

    void ToggleHidden()
    {
        if (!_hidden)
        {
            _wasPlaying = !_paused && _q.Current is not null;
            _player.Send(new { cmd = "pause" });
            _hidden = true;
            Hide();
        }
        else
        {
            _hidden = false;
            Show();
            if (_wasPlaying) _player.Send(new { cmd = "play" });
        }
    }

    void ToggleBrowser()
    {
        if (_browserVisible) HideBrowser();
        else ShowBrowser();
    }

    void ShowBrowser()
    {
        if (_browserVisible) return;
        if (_fullscreen) ToggleFullscreen();
        if (_hidden) ToggleHidden();
        _wasPlaying = !_paused && _q.Current is not null;
        _player.Send(new { cmd = "pause" });
        _browserVisible = true;
        _sourceChangedWhileBrowsing = false;
        if (_menuOpen) { _menuOpen = false; _player.Send(new { cmd = "menu", open = false }); }
        _rectBeforeBrowser = Bounds;

        // Give x.com room to lay out, staying on the same screen.
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        int w = Math.Min(Math.Max(Width, LogicalToDeviceUnits(600)), wa.Width);
        int h = Math.Min(Math.Max(Height, LogicalToDeviceUnits(860)), wa.Height);
        int x = Math.Clamp(Left, wa.Left, wa.Right - w);
        int y = Math.Clamp(Top, wa.Top, wa.Bottom - h);
        Bounds = new Rectangle(x, y, w, h);

        ApplyOpacity();
        ApplyClickThrough();
        _feeder.SetVisible(true);
        _feeder.View.BringToFront();
        Activate();
        _feeder.View.Focus();
    }

    void HideBrowser()
    {
        if (!_browserVisible) return;
        _browserVisible = false;
        _feeder.SetVisible(false);
        _player.View.BringToFront();
        Bounds = _rectBeforeBrowser;
        ApplyOpacity();
        ApplyClickThrough();
        _player.View.Focus();

        if (_q.Current is null || _sourceChangedWhileBrowsing)
        {
            _sourceChangedWhileBrowsing = false;
            if (_q.Remaining > 0) PlayNext();
            else { _waitingForMore = true; Status("Loading feed…"); }
        }
        else
        {
            if (_s.FitToVideo) FitTo(_q.Current.Aspect);
            if (_wasPlaying) _player.Send(new { cmd = "play" });
            Preload();
        }
    }

    void OpenTweet()
    {
        if (_q.Current is { } it) Feeder.OpenExternal(it.TweetUrl);
    }

    void RefreshFeed()
    {
        Flash("Refreshing feed…");
        Navigate(_sourceUrl);
    }

    void Do(Act act)
    {
        switch (act)
        {
            case Act.PlayPause: TogglePlay(); break;
            case Act.Next: PlayNext(); break;
            case Act.Prev: PlayPrev(); break;
            case Act.SeekForward: Seek(5); break;
            case Act.SeekBack: Seek(-5); break;
            case Act.Autoplay: ToggleAutoplay(); break;
            case Act.Mute: ToggleMute(); break;
            case Act.VolumeUp: ChangeVolume(5); break;
            case Act.VolumeDown: ChangeVolume(-5); break;
            case Act.ClickThrough: SetClickThrough(!_clickThrough); break;
            case Act.OpacityUp: SetOpacity(_s.Opacity + 5); break;
            case Act.OpacityDown: SetOpacity(_s.Opacity - 5); break;
            case Act.Hide: ToggleHidden(); break;
            case Act.Browser: ToggleBrowser(); break;
            case Act.OpenTweet: OpenTweet(); break;
            case Act.Refresh: RefreshFeed(); break;
            case Act.Quit: Close(); break;
            case Act.Menu: ToggleMenu(); break;
            case Act.Fullscreen: ToggleFullscreen(); break;
            case Act.Scrubber: ToggleScrubber(); break;
        }
    }

    // ------------------------------------------------------------------ player page messages

    void OnPlayerMessage(JsonElement m)
    {
        string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "ended":
                _errors = 0;
                if (_s.Autoplay) PlayNext();
                break;
            case "error":
                Log.Info($"playback error on {_playingUrl}");
                if (++_errors >= MaxConsecutiveErrors)
                {
                    Status("Videos keep failing to load. Check your connection, then press Next.");
                    _errors = 0;
                    break;
                }
                Flash("Skipped a video that failed to load");
                PlayNext();
                break;
            case "upgraded":
                Preload();
                break;
            case "upgradeFailed":
                Log.Info("quality upgrade failed; staying on current rendition");
                _playingUrl = null; // let a later fullscreen toggle retry
                Preload();
                break;
            case "paused":
                _paused = m.GetProperty("value").GetBoolean();
                break;
            case "key":
                OnKey(m.GetProperty("key").GetString() ?? "", m.GetProperty("shift").GetBoolean());
                break;
            case "wheel":
                int dir = m.GetProperty("dy").GetDouble() < 0 ? 1 : -1;
                if (m.GetProperty("ctrl").GetBoolean()) SetOpacity(_s.Opacity + 5 * dir);
                else ChangeVolume(5 * dir);
                break;
            case "dbl":
                ToggleFullscreen();
                break;
            case "act":
                if (Enum.TryParse<Act>(m.GetProperty("act").GetString(), out var act)) Do(act);
                break;
            case "source":
                Navigate(m.GetProperty("url").GetString() ?? _sourceUrl);
                break;
            case "search":
                Search(
                    m.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? ""),
                    m.GetProperty("all").GetBoolean(),
                    m.GetProperty("latest").GetBoolean());
                break;
            case "set":
                OnSet(m.GetProperty("key").GetString() ?? "", m.GetProperty("value"));
                break;
            case "menuState":
                _menuOpen = m.GetProperty("open").GetBoolean();
                ApplyClickThrough();
                break;
            case "savePreset":
                BeginInvoke(SavePreset);
                break;
            case "menu":
                BeginInvoke(ShowMenu);
                break;
            case "drag":
                if (_fullscreen) break; // no moving/resizing while fullscreen
                int ht = m.GetProperty("ht").GetInt32();
                // Run after the WebView2 event returns: the move/size loop is modal.
                BeginInvoke(() =>
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, ht, IntPtr.Zero);
                });
                break;
        }
    }

    void OnKey(string key, bool shift)
    {
        switch (key)
        {
            case " ": case "k": case "K": TogglePlay(); break;
            case "ArrowRight": Seek(shift ? 15 : 5); break;
            case "ArrowLeft": Seek(shift ? -15 : -5); break;
            case "ArrowUp": ChangeVolume(5); break;
            case "ArrowDown": ChangeVolume(-5); break;
            case "n": case "N": case "PageDown": case "j": case "J": PlayNext(); break;
            case "p": case "P": case "PageUp": PlayPrev(); break;
            case "a": case "A": ToggleAutoplay(); break;
            case "m": case "M": ToggleMute(); break;
            case "o": case "O": OpenTweet(); break;
            case "b": case "B": ToggleBrowser(); break;
            case "r": case "R": RefreshFeed(); break;
            case "f": case "F": case "F11": ToggleFullscreen(); break;
            case "Escape": if (_fullscreen) ToggleFullscreen(); break;
            case "v": case "V": ToggleScrubber(); break;
            case "t": case "T": SetClickThrough(true); break;
            case "h": case "H": ToggleHidden(); break;
        }
    }

    // ------------------------------------------------------------------ fly-out menu (in player.html)

    /// <summary>Cover the whole monitor the window is on (taskbar included); toggling again restores the window.</summary>
    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            if (_browserVisible) HideBrowser();
            if (_hidden) ToggleHidden();
            _rectBeforeFull = Bounds;
            _fullscreen = true;
            Bounds = Screen.FromRectangle(Bounds).Bounds;
            UpgradeCurrent(); // the page preloads the next clip after the swap ("upgraded")
            Preload();
            string q = "";
            if (_q.Current is { } it)
            {
                var v = VariantFor(it);
                q = " · " + v.Label + (v == it.Best && _s.FullscreenBest ? (it.Best.Label is "4K" or "1440p" or "1080p" ? "" : " (max available)") : "");
            }
            Flash($"Fullscreen{q} — Esc or F to exit");
        }
        else
        {
            _fullscreen = false;
            Bounds = _rectBeforeFull;
            Preload(); // keep the current clip as is; the next one goes back to the window quality
            if (_s.FitToVideo && _q.Current is { } c) FitTo(c.Aspect);
        }
        PushUi();
    }

    void ToggleMenu()
    {
        if (_browserVisible) HideBrowser();
        if (_hidden) ToggleHidden();
        PushUi();
        // The hotkey usually fires while another app has focus: take focus so the search box accepts typing.
        Activate();
        _player.View.Focus();
        _player.Send(new { cmd = "menu", open = (bool?)null, focus = true });
    }

    void PushUi()
    {
        var cur = _q.Current;
        var search = SearchStateFor(_sourceUrl);
        _player.Send(new
        {
            cmd = "ui",
            state = new
            {
                sources = _s.Sources.Select(p => new { name = p.Name, url = p.Url }),
                source = _sourceUrl,
                sourceLabel = SourceLabel(_sourceUrl),
                now = cur is null ? null : new { author = cur.Author, text = cur.Text, pos = $"{_q.Index + 1}/{_q.Count}" },
                autoplay = _s.Autoplay,
                muted = _s.Muted,
                volume = _s.Volume,
                opacity = _s.Opacity,
                clickThrough = _clickThrough,
                clickThroughKey = Hk(Act.ClickThrough),
                fit = _s.FitToVideo,
                topMost = _s.TopMost,
                tags = search.Tags,
                matchAll = search.All,
                latest = search.Latest,
                recent = _s.RecentTags,
                fullscreen = _fullscreen,
                scrubber = _s.ShowScrubber,
            },
        });
    }

    void OnSet(string key, JsonElement value)
    {
        switch (key)
        {
            case "volume":
                _s.Volume = Math.Clamp(value.GetInt32(), 0, 100);
                if (_s.Volume > 0) _s.Muted = false;
                PushState();
                break;
            case "opacity":
                _s.Opacity = Math.Clamp(value.GetInt32(), 10, 100);
                ApplyOpacity();
                break;
            case "fit":
                _s.FitToVideo = value.GetBoolean();
                if (_s.FitToVideo && _q.Current is { } c) FitTo(c.Aspect);
                PushUi();
                break;
            case "topMost":
                _s.TopMost = value.GetBoolean();
                Native.SetTopMost(Handle, _s.TopMost);
                PushUi();
                break;
        }
    }

    /// <summary>"@name" plays that account's videos; anything else searches X for videos.</summary>
    /// <summary>
    /// Search X for videos matching several tags. Any = (a OR b), All = a b. "-word" excludes,
    /// "@name" means from:name, phrases with spaces are quoted. A lone @name opens that account's Media tab.
    /// </summary>
    void Search(IEnumerable<string> tags, bool all, bool latest)
    {
        var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0 && t != "-" && t != "@")
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clean.Count == 0) return;

        _s.SearchMatchAll = all;
        _s.SearchLatest = latest;
        _s.RecentTags = clean.Concat(_s.RecentTags).Distinct(StringComparer.OrdinalIgnoreCase).Take(14).ToList();

        if (clean.Count == 1 && clean[0].StartsWith('@') && !clean[0].Contains(' '))
        {
            Navigate($"https://x.com/{Uri.EscapeDataString(clean[0][1..])}/media");
            return;
        }
        string q = BuildQuery(clean, all) + " filter:native_video";
        Navigate($"https://x.com/search?q={Uri.EscapeDataString(q)}&src=typed_query&f={(latest ? "live" : "top")}");
    }

    /// <summary>
    /// Tags shown in the menu's tag box: the current feed's own search terms, so adding a tag extends the
    /// search you're watching instead of replacing it. Non-search feeds start empty.
    /// </summary>
    (List<string> Tags, bool All, bool Latest) SearchStateFor(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var path = u.AbsolutePath.Trim('/');
            if (path == "search")
            {
                var qs = System.Web.HttpUtility.ParseQueryString(u.Query);
                var (tags, all) = ParseQuery(qs["q"] ?? "");
                return (tags, all, qs["f"] == "live");
            }
            var parts = path.Split('/');
            if (parts.Length == 2 && parts[1] == "media" && parts[0].Length > 0)
                return (new List<string> { "@" + parts[0] }, false, _s.SearchLatest);
        }
        return (new List<string>(), _s.SearchMatchAll, _s.SearchLatest);
    }

    /// <summary>Inverse of <see cref="BuildQuery"/>: "(a OR from:b) -c filter:native_video" → [a, @b, -c], any.</summary>
    static (List<string> Tags, bool All) ParseQuery(string q)
    {
        q = q.Replace("filter:native_video", " ");
        var tokens = new List<string>();
        for (int i = 0; i < q.Length;)
        {
            char c = q[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')') { tokens.Add(c.ToString()); i++; continue; }
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            while (i < q.Length && (quoted || (!char.IsWhiteSpace(q[i]) && q[i] is not '(' and not ')')))
            {
                if (q[i] == '"') quoted = !quoted;
                sb.Append(q[i++]);
            }
            tokens.Add(sb.ToString());
        }

        static string Untag(string t)
        {
            bool neg = t.StartsWith('-');
            string body = neg ? t[1..] : t;
            if (body.StartsWith("from:", StringComparison.OrdinalIgnoreCase)) body = "@" + body[5..];
            body = body.Trim('"');
            return body.Length == 0 ? "" : (neg ? "-" : "") + body;
        }

        bool any = tokens.Contains("OR");
        var tags = tokens.Where(t => t is not "(" and not ")" and not "OR").Select(Untag).Where(t => t.Length > 0).ToList();
        return (tags, !any && tags.Count(t => !t.StartsWith('-')) > 1);
    }

    static string BuildQuery(List<string> tags, bool all)
    {
        static string Term(string t)
        {
            if (t.StartsWith('@') && !t.Contains(' ')) return "from:" + t[1..];
            if (t.Contains(' ') && !(t.StartsWith('"') && t.EndsWith('"'))) return $"\"{t.Trim('"')}\"";
            return t;
        }
        static string Negative(string t)
        {
            var body = t[1..];
            return "-" + (body.Contains(' ') && !body.StartsWith('"') ? $"\"{body}\"" : body);
        }

        var include = tags.Where(t => !t.StartsWith('-')).Select(Term).ToList();
        var exclude = tags.Where(t => t.StartsWith('-')).Select(Negative);
        string core = include.Count switch
        {
            0 => "",
            1 => include[0],
            _ => all ? string.Join(" ", include) : "(" + string.Join(" OR ", include) + ")",
        };
        return string.Join(" ", new[] { core }.Concat(exclude).Where(s => s.Length > 0));
    }

    void SavePreset()
    {
        var name = InputBox.Show(this, "Preset name:", SourceLabel(_sourceUrl));
        if (string.IsNullOrWhiteSpace(name)) return;
        _s.Sources.RemoveAll(p => p.Url == _sourceUrl);
        _s.Sources.Add(new SourcePreset { Name = name.Trim(), Url = _sourceUrl });
        _s.Save();
        PushUi();
    }

    static string SourceLabel(string url)
    {
        if (url == Feeder.ForYouUrl) return "For You";
        if (url == Feeder.FollowingUrl) return "Following";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var path = u.AbsolutePath.Trim('/');
        if (path == "search")
        {
            var q = System.Web.HttpUtility.ParseQueryString(u.Query)["q"] ?? "";
            q = q.Replace("filter:native_video", "").Trim();
            if (q.StartsWith('(') && q.Contains(')'))
            {
                int close = q.IndexOf(')');
                q = q[1..close].Replace(" OR ", " | ") + q[(close + 1)..];
            }
            return "Search: " + q.Replace("from:", "@").Trim();
        }
        if (path.EndsWith("/media")) return "@" + path[..^"/media".Length] + " videos";
        return path.Length > 0 ? path : url;
    }

    void Status(string text, bool clear = false) => _player.Send(new { cmd = "status", text, clear });
    void Flash(string text) => _player.Send(new { cmd = "flash", text });
    string Hk(Act a) => _s.Hotkeys.TryGetValue(a.ToString(), out var v) && v.Length > 0 ? v : "(unbound)";

    // ------------------------------------------------------------------ context menu

    void ShowMenu()
    {
        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var items = menu.Items;

        if (_q.Current is { } cur)
        {
            items.Add(Item($"Open @{cur.Author} on X", OpenTweet, Act.OpenTweet));
            items.Add(new ToolStripSeparator());
        }
        items.Add(Item("Play / Pause", TogglePlay, Act.PlayPause));
        items.Add(Item("Next", PlayNext, Act.Next));
        items.Add(Item("Previous", PlayPrev, Act.Prev));
        items.Add(new ToolStripSeparator());
        items.Add(Check("Autoplay (off = loop)", _s.Autoplay, ToggleAutoplay, Act.Autoplay));
        items.Add(Check("Mute", _s.Muted, ToggleMute, Act.Mute));
        items.Add(Check("Scrub bar", _s.ShowScrubber, ToggleScrubber, Act.Scrubber));

        var opacity = new ToolStripMenuItem("Opacity");
        for (int p = 100; p >= 20; p -= 10)
        {
            int pct = p;
            opacity.DropDownItems.Add(Check($"{pct}%", _s.Opacity == pct, () => SetOpacity(pct)));
        }
        items.Add(opacity);
        items.Add(Check("Click-through", _clickThrough, () => SetClickThrough(!_clickThrough), Act.ClickThrough));
        items.Add(Check("Always on top", _s.TopMost, () => { _s.TopMost = !_s.TopMost; Native.SetTopMost(Handle, _s.TopMost); PushUi(); }));
        items.Add(Check("Fullscreen", _fullscreen, ToggleFullscreen, Act.Fullscreen));
        items.Add(Item("Menu panel", ToggleMenu, Act.Menu));
        items.Add(Check("Fit window to video", _s.FitToVideo, () =>
        {
            _s.FitToVideo = !_s.FitToVideo;
            if (_s.FitToVideo && _q.Current is { } c) FitTo(c.Aspect);
            PushUi();
        }));

        var quality = new ToolStripMenuItem("Quality (window)");
        foreach (var (name, rate) in new[] { ("Best available (up to 4K)", int.MaxValue), ("1080p", 10_400_000), ("720p", 2_200_000), ("480p", 950_000), ("Data saver", 300_000) })
            quality.DropDownItems.Add(Check(name, _s.MaxBitrate == rate, () => { _s.MaxBitrate = rate; Preload(); }));
        quality.DropDownItems.Add(new ToolStripSeparator());
        quality.DropDownItems.Add(Check("Max quality in fullscreen", _s.FullscreenBest, () =>
        {
            _s.FullscreenBest = !_s.FullscreenBest;
            if (_fullscreen) { UpgradeCurrent(); Preload(); }
        }));
        items.Add(quality);
        items.Add(Check("Include GIFs", _s.IncludeGifs, () => _s.IncludeGifs = !_s.IncludeGifs));
        items.Add(Check("Skip ads", _s.SkipAds, () => _s.SkipAds = !_s.SkipAds));
        items.Add(new ToolStripSeparator());

        var sources = new ToolStripMenuItem("Source");
        foreach (var p in _s.Sources)
        {
            var preset = p;
            sources.DropDownItems.Add(Check(preset.Name, preset.Url == _sourceUrl, () => Navigate(preset.Url)));
        }
        sources.DropDownItems.Add(new ToolStripSeparator());
        sources.DropDownItems.Add(Item("User's videos…", () =>
        {
            var user = InputBox.Show(this, "Show videos from which account? (@name)", "");
            user = user?.Trim().TrimStart('@');
            if (!string.IsNullOrEmpty(user)) Navigate($"https://x.com/{Uri.EscapeDataString(user)}/media");
        }));
        sources.DropDownItems.Add(Item("Search videos…", () =>
        {
            var q = InputBox.Show(this, "Search X for videos about:", "");
            if (!string.IsNullOrWhiteSpace(q)) Search(q.Split(','), all: false, latest: false);
        }));
        sources.DropDownItems.Add(Item("Custom URL…", () =>
        {
            var url = InputBox.Show(this, "x.com page to pull videos from:", _sourceUrl);
            if (url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out _)) Navigate(url.Trim());
        }));
        sources.DropDownItems.Add(Item("Save current source as preset…", SavePreset));
        items.Add(sources);
        items.Add(Check("Show x.com browser", _browserVisible, ToggleBrowser, Act.Browser));
        items.Add(Item("Refresh feed", RefreshFeed, Act.Refresh));
        items.Add(Item("Hide", ToggleHidden, Act.Hide));
        items.Add(new ToolStripSeparator());
        items.Add(Item("Edit settings… (restart to apply hotkeys)", () =>
        {
            _s.Save();
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FileLocation}\"")); } catch { }
        }));
        items.Add(Item("Quit", Close, Act.Quit));

        menu.Show(Cursor.Position);
    }

    ToolStripMenuItem Item(string text, Action onClick, Act? hotkey = null)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => onClick();
        if (hotkey is { } h && _s.Hotkeys.TryGetValue(h.ToString(), out var combo) && combo.Length > 0)
            it.ShortcutKeyDisplayString = combo;
        return it;
    }

    ToolStripMenuItem Check(string text, bool on, Action onClick, Act? hotkey = null)
    {
        var it = Item(text, onClick, hotkey);
        it.Checked = on;
        return it;
    }
}

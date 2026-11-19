using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhostFeed;

public sealed class SourcePreset
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class Settings
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GhostFeed");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Window
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public int Width { get; set; } = 405;
    public int Height { get; set; } = 720;
    public int Opacity { get; set; } = 100;          // 10..100
    public bool TopMost { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = false;  // applied at startup
    public bool FitToVideo { get; set; } = true;      // reshape window to each video's aspect ratio
    public bool ClickThroughOnStart { get; set; } = false;

    // Playback
    public int Volume { get; set; } = 80;            // 0..100
    public bool Muted { get; set; } = false;
    public bool Autoplay { get; set; } = true;       // advance on end; off = loop current
    public int MaxBitrate { get; set; } = 10_400_000; // picks best mp4 variant at or below this
    public bool IncludeGifs { get; set; } = true;
    public bool SkipAds { get; set; } = true;

    // Feed
    public string SourceUrl { get; set; } = Feeder.ForYouUrl;
    public List<SourcePreset> Sources { get; set; } = new()
    {
        new() { Name = "For You", Url = Feeder.ForYouUrl },
        new() { Name = "Following", Url = Feeder.FollowingUrl },
        new() { Name = "Latest videos", Url = "https://x.com/search?q=filter%3Anative_video&src=typed_query&f=live" },
        new() { Name = "Popular videos", Url = "https://x.com/search?q=filter%3Anative_video%20min_faves%3A5000&src=typed_query&f=top" },
        new() { Name = "Bookmarks", Url = "https://x.com/i/bookmarks" },
    };

    // Global hotkeys: action name -> "Ctrl+Alt+Key". Empty string disables one.
    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

    public bool VerboseLog { get; set; } = false;

    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        [nameof(Act.PlayPause)] = "Ctrl+Alt+Space",
        [nameof(Act.Next)] = "Ctrl+Alt+Right",
        [nameof(Act.Prev)] = "Ctrl+Alt+Left",
        [nameof(Act.SeekForward)] = "Ctrl+Alt+Shift+Right",
        [nameof(Act.SeekBack)] = "Ctrl+Alt+Shift+Left",
        [nameof(Act.Autoplay)] = "Ctrl+Alt+A",
        [nameof(Act.Mute)] = "Ctrl+Alt+M",
        [nameof(Act.VolumeUp)] = "Ctrl+Alt+Shift+Up",
        [nameof(Act.VolumeDown)] = "Ctrl+Alt+Shift+Down",
        [nameof(Act.ClickThrough)] = "Ctrl+Alt+T",
        [nameof(Act.OpacityUp)] = "Ctrl+Alt+Up",
        [nameof(Act.OpacityDown)] = "Ctrl+Alt+Down",
        [nameof(Act.Hide)] = "Ctrl+Alt+H",
        [nameof(Act.Browser)] = "Ctrl+Alt+B",
        [nameof(Act.OpenTweet)] = "Ctrl+Alt+O",
        [nameof(Act.Refresh)] = "Ctrl+Alt+R",
        [nameof(Act.Quit)] = "Ctrl+Alt+Q",
        [nameof(Act.Menu)] = "Ctrl+Alt+S",
        [nameof(Act.Fullscreen)] = "Ctrl+Alt+F",
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex) { Log.Error("settings load", ex); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public static string FileLocation => FilePath;

    void Normalize()
    {
        Opacity = Math.Clamp(Opacity, 10, 100);
        Volume = Math.Clamp(Volume, 0, 100);
        Width = Math.Max(Width, 160);
        Height = Math.Max(Height, 120);
        // New actions added in later versions get their default binding.
        foreach (var (k, v) in DefaultHotkeys())
            Hotkeys.TryAdd(k, v);
        if (string.IsNullOrWhiteSpace(SourceUrl) || SourceUrl.TrimEnd('/') == "https://x.com/home")
            SourceUrl = Feeder.ForYouUrl;

        // v0.1 had a single "Home timeline" preset; split it into the two home tabs.
        int home = Sources.FindIndex(p => p.Url.TrimEnd('/') == "https://x.com/home");
        if (home >= 0) Sources.RemoveAt(home);
        if (!Sources.Any(p => p.Url == Feeder.FollowingUrl))
            Sources.Insert(0, new SourcePreset { Name = "Following", Url = Feeder.FollowingUrl });
        if (!Sources.Any(p => p.Url == Feeder.ForYouUrl))
            Sources.Insert(0, new SourcePreset { Name = "For You", Url = Feeder.ForYouUrl });
    }
}

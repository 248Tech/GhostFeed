namespace GhostFeed;

public enum Act
{
    PlayPause, Next, Prev, SeekForward, SeekBack, Autoplay, Mute, VolumeUp, VolumeDown,
    ClickThrough, OpacityUp, OpacityDown, Hide, Browser, OpenTweet, Refresh, Quit, Menu, Fullscreen,
}

/// <summary>System-wide hotkeys via RegisterHotKey; they fire even when the window is click-through or unfocused.</summary>
sealed class HotkeyManager : IDisposable
{
    // Actions that make sense to auto-repeat while the key is held.
    static readonly HashSet<Act> Repeatable = new()
    {
        Act.SeekForward, Act.SeekBack, Act.VolumeUp, Act.VolumeDown, Act.OpacityUp, Act.OpacityDown,
    };

    readonly IntPtr _hwnd;
    readonly Dictionary<int, Act> _ids = new();

    public HotkeyManager(IntPtr hwnd) => _hwnd = hwnd;

    /// <returns>Bindings that failed to register (bad syntax or taken by another app).</returns>
    public List<string> Register(Dictionary<string, string> map)
    {
        UnregisterAll();
        var failed = new List<string>();
        int id = 1;
        foreach (var (name, combo) in map)
        {
            if (string.IsNullOrWhiteSpace(combo)) continue;
            if (!Enum.TryParse<Act>(name, true, out var act) || !TryParse(combo, out uint mods, out uint vk))
            {
                failed.Add($"{name}={combo}");
                continue;
            }
            if (!Repeatable.Contains(act)) mods |= Native.MOD_NOREPEAT;
            if (Native.RegisterHotKey(_hwnd, id, mods, vk)) _ids[id++] = act;
            else failed.Add(combo);
        }
        return failed;
    }

    public bool TryGet(int id, out Act act) => _ids.TryGetValue(id, out act);

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; continue;
                case "alt": mods |= Native.MOD_ALT; continue;
                case "shift": mods |= Native.MOD_SHIFT; continue;
                case "win": case "windows": mods |= Native.MOD_WIN; continue;
            }
            if (vk != 0) return false; // two non-modifier keys
            string token = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Keys>(token, true, out var key)) return false;
            vk = (uint)key;
        }
        return vk != 0;
    }

    void UnregisterAll()
    {
        foreach (var id in _ids.Keys) Native.UnregisterHotKey(_hwnd, id);
        _ids.Clear();
    }

    public void Dispose() => UnregisterAll();
}

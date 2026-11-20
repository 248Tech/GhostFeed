# GhostFeed

GhostFeed plays videos from X (x.com) one after another in a borderless, always-on-top window, like GhostReel does for local files. You can fade it out or make it click-through and keep working underneath.

It logs in to X as you, inside an embedded Edge (WebView2) browser that normally stays hidden behind the player. Point it at any x.com page (your home timeline, a search, a list, someone's Media tab, your bookmarks) and it plays the videos from that page. It reads X's own timeline API responses rather than the page layout, so X redesigns don't break it.

## Run

1. `.\build.ps1` → `dist\GhostFeed.exe` (about 1.4 MB). This needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.DesktopRuntime.8`).
   `.\build.ps1 -SelfContained` makes a 65 MB exe that runs without .NET installed.
2. On first launch the x.com login page opens inside the window. Log in. Once X lands on your home timeline, the browser hides itself and playback starts.

Your login is kept in `%LOCALAPPDATA%\GhostFeed\WebView2`. Settings are in `%APPDATA%\GhostFeed\settings.json`.

You can pass a page to start from: `GhostFeed.exe "https://x.com/search?q=cats%20filter%3Anative_video"`.

## Controls

Global hotkeys work even when GhostFeed is click-through or another app has focus. You can rebind them in `settings.json` (restart to apply):

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+Space` | Play / pause |
| `Ctrl+Alt+Right` / `Left` | Next / previous video |
| `Ctrl+Alt+Shift+Right` / `Left` | Seek ±5 s |
| `Ctrl+Alt+A` | Autoplay on/off (off = loop the current video) |
| `Ctrl+Alt+M` | Mute |
| `Ctrl+Alt+Shift+Up` / `Down` | Volume ±5% |
| `Ctrl+Alt+Up` / `Down` | Opacity ±5% |
| `Ctrl+Alt+T` | Click-through on/off |
| `Ctrl+Alt+H` | Hide / show (pauses while hidden) |
| `Ctrl+Alt+B` | Show / hide the x.com browser |
| `Ctrl+Alt+O` | Open the current post in your default browser |
| `Ctrl+Alt+R` | Refresh the feed |
| `Ctrl+Alt+S` | Open / close the menu panel (focuses search) |
| `Ctrl+Alt+F` | Fullscreen on/off |
| `Ctrl+Alt+Q` | Quit |

When the player has focus:

| Key | Action |
|---|---|
| `S` / `/` / `Tab` | Menu panel (`Esc` closes) |
| `Space` / `K` | Play / pause |
| `N` / `J` / `PgDn` | Next |
| `P` / `PgUp` | Previous |
| `←` / `→` | Seek ±5 s (with Shift: ±15 s) |
| `↑` / `↓` | Volume |
| `A` | Autoplay |
| `M` | Mute |
| `O` | Open post on X |
| `B` | x.com browser |
| `F` / `F11` | Fullscreen (`Esc` exits) |
| `V` | Scrub bar on/off |
| `T` | Click-through on |
| `H` | Hide |
| `R` | Refresh |

Mouse (when not click-through):

| Input | Action |
|---|---|
| Drag | Move the window |
| Drag an edge or corner | Resize (keeps the video's shape; hold Shift to resize freely) |
| Wheel | Volume |
| Ctrl + wheel | Opacity |
| Click | Play / pause |
| Scrub bar (bottom, on hover) | Click or drag to seek; shows elapsed / total time and a tooltip under the cursor |
| Double-click | Fullscreen on/off |
| ☰ (top-left, shows on hover) | Menu panel: search, feeds, now playing, transport, volume/opacity, toggles |
| Right-click | Menu: sources, opacity, quality, autoplay, GIFs, ads, always on top, … |

## Choosing what plays

- **Menu panel** (☰, `S`, or `Ctrl+Alt+S`): build a tag search. Type a word, `#hashtag`, `@user` or phrase and press Enter or comma to add it as a chip. Start a tag with `-` to exclude it. **Any** matches videos with any tag, **All** requires every tag; Top/Latest sets the order. Press Enter on an empty box (or *Search videos*) to run it. Recent tags appear below for one-click reuse. A lone `@name` opens that account's Media tab. You can also pick For You, Following, Latest, Popular, Bookmarks or your saved presets. While the panel is open, click-through is paused so you can use it.
- **Right-click → Source** lists the same presets (For You, Following, Latest videos, Popular videos, Bookmarks), plus *User's videos…*, *Search videos…* and *Custom URL…*. *Save current source as preset…* adds the current source to the list.
- Or press `Ctrl+Alt+B`, browse to any page on x.com, and press `Ctrl+Alt+B` again. That page becomes the source.
- X has no URL for the home tabs. *For You* and *Following* load /home, click the tab, and only accept videos from that tab's timeline.
- **Quality:** the window plays the best version at or below the *Quality (window)* setting (default 1080p). Fullscreen always switches to the best version X has, up to 4K. A clip that is already playing upgrades in place when you enter fullscreen, without restarting. Turn this off with *Quality → Max quality in fullscreen*.
- **Fit window to video** (on by default) reshapes the window for each video: portrait clips get a tall window, landscape a wide one. The window keeps the same area and stays anchored to its nearest screen corner.

## How it works

- `Feeder.cs`: hidden x.com WebView2. `WebResourceResponseReceived` captures `/i/api/graphql/*` responses. When fewer than 5 unplayed videos are queued, it scrolls the page so X loads more. While the browser is hidden, it blocks the timeline's own video downloads to save bandwidth.
- `FeedParser.cs`: finds every `video_info.variants` block in a response and picks the best mp4 at or below the quality cap. It skips ads (`promotedMetadata`) and HLS-only media.
- `PlayerHost.cs` + `Assets/player.html`: two `<video>` elements, so the next clip is already loading while the current one plays.
- `MainForm.cs`: the window behaviour (layered opacity, `WS_EX_TRANSPARENT` click-through, aspect-locked resizing, `RegisterHotKey`, menu).

Troubleshooting: run `GhostFeed.exe --verbose` (or set `"VerboseLog": true`). It writes `%APPDATA%\GhostFeed\log.txt` and saves the latest raw response for each GraphQL operation to `%APPDATA%\GhostFeed\dumps\`. Use these to fix the parser if X changes its JSON.

## Notes

- GhostFeed is for your own viewing. It uses your own X session at a normal scrolling pace and doesn't download or save videos.
- GhostFeed and GhostReel use the same default hotkeys. Only the app started first gets them, so rebind one of them if you run both.
- Exclusive-fullscreen games draw over every window, including always-on-top ones. Use borderless/windowed mode in the game.

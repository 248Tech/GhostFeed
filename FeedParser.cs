using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GhostFeed;

/// <summary>One mp4 rendition. Width/Height come from the URL (…/vid/avc1/720x1280/…) and may be 0.</summary>
public sealed record VideoVariant(long Bitrate, string Url, int Width, int Height)
{
    /// <summary>"720p", "1080p", "4K"… based on the short side.</summary>
    public string Label
    {
        get
        {
            int side = Math.Min(Width, Height);
            if (side <= 0) return Bitrate > 0 ? $"{Bitrate / 1000} kbps" : "original";
            return side >= 2000 ? "4K" : side >= 1400 ? "1440p" : $"{side}p";
        }
    }
}

public sealed record VideoItem(
    string MediaId,
    string TweetId,
    string Author,
    string Text,
    IReadOnlyList<VideoVariant> Variants, // mp4 only, ascending bitrate, never empty
    string Poster,
    int Width,
    int Height,
    int DurationMs,
    bool IsGif)
{
    public string TweetUrl => $"https://x.com/{(Author.Length > 0 ? Author : "i")}/status/{TweetId}";
    public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 0;
    public VideoVariant Best => Variants[^1];

    /// <summary>Best rendition at or below the bitrate cap; the smallest one if all exceed it.</summary>
    public VideoVariant VariantFor(long cap)
    {
        for (int i = Variants.Count - 1; i >= 0; i--)
            if (Variants[i].Bitrate <= cap) return Variants[i];
        return Variants[0];
    }
}

/// <summary>
/// Pulls videos out of X GraphQL timeline responses. Keys on the media shape
/// (<c>video_info.variants</c>) found anywhere in the tree rather than on
/// operation names or entry paths, so it keeps working when X reshuffles its JSON.
/// </summary>
static class FeedParser
{
    static readonly Regex TrailingTco = new(@"\s*https://t\.co/\w+\s*$", RegexOptions.Compiled);
    static readonly Regex UrlSize = new(@"/(\d{2,5})x(\d{2,5})/", RegexOptions.Compiled);

    public static List<VideoItem> Parse(string json, Settings s)
    {
        var found = new List<VideoItem>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return found; }
        Walk(root, null, s, found);
        return found;
    }

    sealed record TweetCtx(string Id, string Author, string Text);

    static void Walk(JsonNode? node, TweetCtx? tweet, Settings s, List<VideoItem> found)
    {
        switch (node)
        {
            case JsonArray arr:
                foreach (var child in arr) Walk(child, tweet, s, found);
                return;

            case JsonObject obj:
                // Timeline entries for ads carry promotedMetadata next to tweet_results.
                if (s.SkipAds && obj.ContainsKey("promotedMetadata")) return;

                if (obj["video_info"] is JsonObject vi && vi["variants"] is JsonArray)
                {
                    var item = BuildItem(obj, vi, tweet, s);
                    if (item is not null) found.Add(item);
                    return;
                }

                var ctx = AsTweet(obj) ?? tweet;
                foreach (var (_, child) in obj) Walk(child, ctx, s, found);
                return;
        }
    }

    /// <summary>A tweet result object: has rest_id plus legacy { full_text }.</summary>
    static TweetCtx? AsTweet(JsonObject o)
    {
        if (o["rest_id"] is not JsonValue idv || o["legacy"] is not JsonObject legacy || !legacy.ContainsKey("full_text"))
            return null;

        string author =
            Str(o["core"]?["user_results"]?["result"]?["core"]?["screen_name"]) ?? // 2025+ shape
            Str(o["core"]?["user_results"]?["result"]?["legacy"]?["screen_name"]) ??
            "";
        string text =
            Str(o["note_tweet"]?["note_tweet_results"]?["result"]?["text"]) ??
            Str(legacy["full_text"]) ?? "";
        text = TrailingTco.Replace(System.Net.WebUtility.HtmlDecode(text), "");
        return new TweetCtx(idv.ToString(), author, text);
    }

    static VideoItem? BuildItem(JsonObject media, JsonObject vi, TweetCtx? tweet, Settings s)
    {
        string type = Str(media["type"]) ?? "video";
        bool gif = type == "animated_gif";
        if (gif && !s.IncludeGifs) return null;

        // Keep every mp4 rendition so playback can pick per mode (window vs fullscreen). HLS-only media is skipped.
        var variants = new List<VideoVariant>();
        foreach (var v in (JsonArray)vi["variants"]!)
        {
            if (Str(v?["content_type"]) != "video/mp4") continue;
            string? url = Str(v?["url"]);
            if (url is null) continue;
            long rate = v?["bitrate"] is JsonValue b && b.TryGetValue<long>(out var r) ? r : 0;
            var m = UrlSize.Match(url);
            int vw = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            int vh = m.Success ? int.Parse(m.Groups[2].Value) : 0;
            variants.Add(new VideoVariant(rate, url, vw, vh));
        }
        if (variants.Count == 0) return null;
        // Order by bitrate, then pixel count (GIF renditions all report bitrate 0).
        variants.Sort((a, b) => a.Bitrate != b.Bitrate ? a.Bitrate.CompareTo(b.Bitrate)
                                                       : ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height));
        string chosen = variants[^1].Url;

        int w = 0, h = 0;
        if (media["original_info"] is JsonObject oi)
        {
            w = Int(oi["width"]);
            h = Int(oi["height"]);
        }
        if ((w == 0 || h == 0) && vi["aspect_ratio"] is JsonArray ar && ar.Count == 2)
        {
            w = Int(ar[0]);
            h = Int(ar[1]);
        }

        string mediaId = Str(media["id_str"]) ?? Str(media["media_key"]) ?? chosen;
        return new VideoItem(
            MediaId: mediaId,
            TweetId: tweet?.Id ?? "",
            Author: tweet?.Author ?? "",
            Text: tweet?.Text ?? "",
            Variants: variants,
            Poster: Str(media["media_url_https"]) ?? "",
            Width: w,
            Height: h,
            DurationMs: Int(vi["duration_millis"]),
            IsGif: gif);
    }

    static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static int Int(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }
}

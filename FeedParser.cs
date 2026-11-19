using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GhostFeed;

public sealed record VideoItem(
    string MediaId,
    string TweetId,
    string Author,
    string Text,
    string Url,
    string Poster,
    int Width,
    int Height,
    int DurationMs,
    bool IsGif)
{
    public string TweetUrl => $"https://x.com/{(Author.Length > 0 ? Author : "i")}/status/{TweetId}";
    public double Aspect => Width > 0 && Height > 0 ? (double)Width / Height : 0;
}

/// <summary>
/// Pulls videos out of X GraphQL timeline responses. Keys on the media shape
/// (<c>video_info.variants</c>) found anywhere in the tree rather than on
/// operation names or entry paths, so it keeps working when X reshuffles its JSON.
/// </summary>
static class FeedParser
{
    static readonly Regex TrailingTco = new(@"\s*https://t\.co/\w+\s*$", RegexOptions.Compiled);

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

        // Best mp4 at or below the bitrate cap; otherwise the smallest mp4. HLS-only media is skipped.
        string? best = null, smallest = null;
        long bestRate = -1, smallestRate = long.MaxValue;
        foreach (var v in (JsonArray)vi["variants"]!)
        {
            if (Str(v?["content_type"]) != "video/mp4") continue;
            string? url = Str(v?["url"]);
            if (url is null) continue;
            long rate = v?["bitrate"] is JsonValue b && b.TryGetValue<long>(out var r) ? r : 0;
            if (rate <= s.MaxBitrate && rate > bestRate) { best = url; bestRate = rate; }
            if (rate < smallestRate) { smallest = url; smallestRate = rate; }
        }
        string? chosen = best ?? smallest;
        if (chosen is null) return null;

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
            Url: chosen,
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

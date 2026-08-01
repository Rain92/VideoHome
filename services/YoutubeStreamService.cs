using System.Collections.Concurrent;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace VideoHome.Services;

// Resolving a YouTube video yields a URL signed for the resolving machine's IP and good for
// only a few hours. Handing that straight to the other person's browser - which is what used
// to happen - could never have worked for them, and persisting it would store something that
// is already dead. So the resolved URL stays on the server and both browsers are pointed at
// /youtube/{id} here instead.
public sealed class YoutubeStreamService
{
    // Comfortably inside the window the signed URL stays valid for, so a cached entry is
    // never itself the reason playback fails.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    private readonly YoutubeClient _youtube = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ILogger<YoutubeStreamService> _logger;

    private sealed record CacheEntry(IStreamInfo Stream, string Title, DateTimeOffset ResolvedAt);

    public YoutubeStreamService(ILogger<YoutubeStreamService> logger) => _logger = logger;

    // Accepts everything YouTube hands out - full watch URLs, youtu.be links, /shorts/ and a
    // bare id. The old check demanded the literal substrings "youtube." and "watch?v=", so a
    // youtu.be or shorts link was silently ignored.
    public static string? TryParseVideoId(string? input) => VideoId.TryParse(input)?.Value;

    public async Task<(IStreamInfo Stream, string Title)?> ResolveAsync(string videoId, CancellationToken ct)
    {
        if (_cache.TryGetValue(videoId, out var cached) &&
            DateTimeOffset.UtcNow - cached.ResolvedAt < CacheLifetime)
            return (cached.Stream, cached.Title);

        var video = await _youtube.Videos.GetAsync(videoId, ct);
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);

        // Muxed streams carry picture and sound in one file, which is all a <video> element
        // can play without stitching them together client side. That caps quality at around
        // 360p; going higher would mean muxing the separate tracks server side.
        var stream = manifest.GetMuxedStreams()
            .OrderByDescending(s => s.VideoQuality)
            .FirstOrDefault();

        if (stream is null)
        {
            _logger.LogWarning("YouTube video {VideoId} offers no muxed stream.", videoId);
            return null;
        }

        _cache[videoId] = new CacheEntry(stream, video.Title, DateTimeOffset.UtcNow);
        _logger.LogInformation("Resolved YouTube video {VideoId} ({Title}) at {Quality}.",
            videoId, video.Title, stream.VideoQuality.Label);

        return (stream, video.Title);
    }

    public async ValueTask<Stream> OpenAsync(IStreamInfo stream, CancellationToken ct) =>
        await _youtube.Videos.Streams.GetAsync(stream, ct);
}

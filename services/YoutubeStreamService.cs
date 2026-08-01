using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace VideoHome.Services;

// A byte range as DASH writes it: inclusive on both ends.
public readonly record struct ByteRange(long From, long To)
{
    public override string ToString() => $"{From}-{To}";
}

// One selectable rung of the ladder - a single video resolution, or the audio track.
// Width/Height/Framerate are 0 on audio, which is what IsAudio reads.
public sealed record DashRepresentation(
    string Id,
    IStreamInfo Stream,
    string Codec,
    long Bandwidth,
    ByteRange Init,
    ByteRange Index,
    int Width,
    int Height,
    int Framerate)
{
    public bool IsAudio => Height == 0;
}

public sealed record DashSet(
    string Title,
    TimeSpan Duration,
    IReadOnlyList<DashRepresentation> Video,
    DashRepresentation Audio)
{
    public DashRepresentation? Find(string id) =>
        Video.FirstOrDefault(r => r.Id == id) ?? (Audio.Id == id ? Audio : null);
}

// Resolving a YouTube video yields a URL signed for the resolving machine's IP and good for
// only a few hours. Handing that straight to the other person's browser - which is what used
// to happen - could never have worked for them, and persisting it would store something that
// is already dead. So the resolved URL stays on the server and both browsers are pointed at
// /youtube/{id}/... here instead.
//
// YouTube stopped publishing muxed (picture and sound in one file) streams above 360p, so HD
// only exists as separate video-only and audio-only tracks. Rather than muxing them back
// together server side, both are described to the browser as a DASH manifest and dash.js
// feeds them to one <video> element through Media Source Extensions. Nothing is transcoded;
// the bytes are passed through untouched.
public sealed class YoutubeStreamService
{
    // Comfortably inside the window the signed URL stays valid for, so a cached entry is
    // never itself the reason playback fails.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    // H.264 tops out at 1080p on YouTube anyway; VP9 and AV1 go to 2160p but cost 12-25
    // Mbit/s and decode in software on older hardware, which the iPad cannot keep up with.
    private const int MaxHeight = 1080;

    // The ftyp + moov + sidx prologue of these streams measures a few kB. 8 is slack.
    private const int ProbeBytes = 8 * 1024;

    private readonly YoutubeClient _youtube = new();
    private readonly ConcurrentDictionary<string, MuxedCacheEntry> _muxedCache = new();
    private readonly ConcurrentDictionary<string, DashCacheEntry> _dashCache = new();
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<YoutubeStreamService> _logger;

    private sealed record MuxedCacheEntry(IStreamInfo Stream, string Title, DateTimeOffset ResolvedAt);
    private sealed record DashCacheEntry(DashSet Set, DateTimeOffset ResolvedAt);

    public YoutubeStreamService(IHttpClientFactory httpFactory, ILogger<YoutubeStreamService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // Accepts everything YouTube hands out - full watch URLs, youtu.be links, /shorts/ and a
    // bare id. The old check demanded the literal substrings "youtube." and "watch?v=", so a
    // youtu.be or shorts link was silently ignored.
    public static string? TryParseVideoId(string? input) => VideoId.TryParse(input)?.Value;

    // ---------- DASH: the HD path ----------

    public async Task<DashSet?> ResolveDashAsync(string videoId, CancellationToken ct)
    {
        if (_dashCache.TryGetValue(videoId, out var cached) &&
            DateTimeOffset.UtcNow - cached.ResolvedAt < CacheLifetime)
            return cached.Set;

        var video = await _youtube.Videos.GetAsync(videoId, ct);
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);

        if (video.Duration is not { } duration || duration <= TimeSpan.Zero)
        {
            // A live stream has no fixed length, so it cannot be described by the static
            // manifest built below.
            _logger.LogWarning("YouTube video {VideoId} has no fixed duration; DASH needs one.", videoId);
            return null;
        }

        // One rung per resolution. YouTube sometimes lists the same size twice at different
        // bitrates, and handing the player duplicate choices only muddies its ABR decisions.
        var videoStreams = manifest.GetVideoOnlyStreams()
            .Where(s => s.Container == Container.Mp4)
            .Where(s => s.VideoCodec.StartsWith("avc1", StringComparison.Ordinal))
            .Where(s => s.VideoResolution.Height <= MaxHeight)
            .GroupBy(s => s.VideoResolution.Height)
            .Select(g => g.OrderByDescending(s => s.Bitrate).First())
            .OrderBy(s => s.VideoResolution.Height)
            .ToList();

        // AAC, and not a dubbed alternate: IsAudioLanguageDefault is null when the video has
        // only one track and false on the extra languages, so "not false" covers both.
        var audioStream = manifest.GetAudioOnlyStreams()
            .Where(s => s.Container == Container.Mp4)
            .Where(s => s.AudioCodec.StartsWith("mp4a", StringComparison.Ordinal))
            .Where(s => s.IsAudioLanguageDefault != false)
            .OrderByDescending(s => s.Bitrate)
            .FirstOrDefault();

        if (videoStreams.Count == 0 || audioStream is null)
        {
            _logger.LogWarning(
                "YouTube video {VideoId} offers no H.264/AAC pair ({VideoCount} video, audio={HasAudio}).",
                videoId, videoStreams.Count, audioStream is not null);
            return null;
        }

        // Each probe is one small ranged request; run them together rather than paying the
        // round trip once per rung.
        var audioProbeTask = ProbeSegmentIndexAsync(audioStream, ct);
        var videoProbes = await Task.WhenAll(videoStreams.Select(s => ProbeSegmentIndexAsync(s, ct)));
        var audioProbe = await audioProbeTask;

        if (audioProbe is not { } audioRanges)
        {
            _logger.LogWarning("Could not read the segment index of the audio track for {VideoId}.", videoId);
            return null;
        }

        var representations = new List<DashRepresentation>();
        for (var i = 0; i < videoStreams.Count; i++)
        {
            if (videoProbes[i] is not { } ranges)
            {
                // One unreadable rung is survivable - drop it and keep the rest of the ladder.
                _logger.LogWarning("Skipping the {Quality} rung of {VideoId}: no segment index.",
                    videoStreams[i].VideoQuality.Label, videoId);
                continue;
            }

            var s = videoStreams[i];
            representations.Add(new DashRepresentation(
                Id: $"v{s.VideoResolution.Height}",
                Stream: s,
                Codec: s.VideoCodec,
                Bandwidth: (long)s.Bitrate.BitsPerSecond,
                Init: ranges.Init,
                Index: ranges.Index,
                Width: s.VideoResolution.Width,
                Height: s.VideoResolution.Height,
                Framerate: s.VideoQuality.Framerate));
        }

        if (representations.Count == 0)
        {
            _logger.LogWarning("No usable video rung survived probing for {VideoId}.", videoId);
            return null;
        }

        var audio = new DashRepresentation(
            Id: "a",
            Stream: audioStream,
            Codec: audioStream.AudioCodec,
            Bandwidth: (long)audioStream.Bitrate.BitsPerSecond,
            Init: audioRanges.Init,
            Index: audioRanges.Index,
            Width: 0, Height: 0, Framerate: 0);

        var set = new DashSet(video.Title, duration, representations, audio);
        _dashCache[videoId] = new DashCacheEntry(set, DateTimeOffset.UtcNow);

        _logger.LogInformation("Resolved YouTube video {VideoId} ({Title}) as DASH: {Ladder} + {AudioCodec}.",
            videoId, video.Title,
            string.Join(", ", representations.OrderByDescending(r => r.Height).Select(Label)),
            audio.Codec);

        return set;
    }

    // "1080p60" the way YouTube writes it - the framerate is only spelled out above 30.
    private static string Label(DashRepresentation r) =>
        r.Framerate > 30 ? $"{r.Height}p{r.Framerate}" : $"{r.Height}p";

    // Forwards the browser's Range request straight to googlevideo. dash.js asks for the
    // init segment, then the index, then media a chunk at a time, so pass-through beats
    // wrapping the whole thing in a seekable stream: every request is already a range.
    public async Task<HttpResponseMessage> OpenRangeAsync(
        DashRepresentation representation, string? rangeHeader, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, representation.Stream.Url);

        if (!string.IsNullOrEmpty(rangeHeader) && RangeHeaderValue.TryParse(rangeHeader, out var range))
            request.Headers.Range = range;

        // Headers-only completion: the body is streamed to the client, never buffered. A
        // 1080p rung is a quarter of a gigabyte.
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // These streams are fragmented MP4: ftyp, moov, then a sidx holding the subsegment
    // index, then the media. A DASH manifest has to state where the first two end and where
    // the sidx sits, and neither is in YoutubeExplode's metadata - so read the prologue and
    // walk the box headers.
    private async Task<(ByteRange Init, ByteRange Index)?> ProbeSegmentIndexAsync(
        IStreamInfo stream, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
            request.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            response.EnsureSuccessStatusCode();

            var head = await response.Content.ReadAsByteArrayAsync(ct);
            return WalkToSegmentIndex(head);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning("Probing the segment index failed: {Error}", ex.Message);
            return null;
        }
    }

    internal static (ByteRange Init, ByteRange Index)? WalkToSegmentIndex(ReadOnlySpan<byte> head)
    {
        long offset = 0;
        while (offset + 8 <= head.Length)
        {
            var size = (long)head[(int)offset] << 24 | (long)head[(int)offset + 1] << 16 |
                       (long)head[(int)offset + 2] << 8 | head[(int)offset + 3];
            var type = Encoding.ASCII.GetString(head.Slice((int)offset + 4, 4));

            // 0 means "runs to end of file" and 1 means a 64-bit size follows the type.
            // Neither is legal for the boxes ahead of the sidx, so anything but a plain
            // size means this is not the layout expected and guessing further is worse
            // than reporting failure.
            if (size < 8)
                return null;

            if (type == "sidx")
                return (new ByteRange(0, offset - 1), new ByteRange(offset, offset + size - 1));

            offset += size;
        }

        return null;
    }

    // ---------- Muxed: the 360p fallback ----------

    // Still here for browsers without Media Source Extensions - dash.js cannot run there, so
    // the player falls back to this single-file stream. It is capped at 360p because that is
    // the best muxed quality YouTube publishes.
    public async Task<(IStreamInfo Stream, string Title)?> ResolveAsync(string videoId, CancellationToken ct)
    {
        if (_muxedCache.TryGetValue(videoId, out var cached) &&
            DateTimeOffset.UtcNow - cached.ResolvedAt < CacheLifetime)
            return (cached.Stream, cached.Title);

        var video = await _youtube.Videos.GetAsync(videoId, ct);
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);

        var stream = manifest.GetMuxedStreams()
            .OrderByDescending(s => s.VideoQuality)
            .FirstOrDefault();

        if (stream is null)
        {
            _logger.LogWarning("YouTube video {VideoId} offers no muxed stream.", videoId);
            return null;
        }

        _muxedCache[videoId] = new MuxedCacheEntry(stream, video.Title, DateTimeOffset.UtcNow);
        _logger.LogInformation("Resolved YouTube video {VideoId} ({Title}) at {Quality} (muxed fallback).",
            videoId, video.Title, stream.VideoQuality.Label);

        return (stream, video.Title);
    }

    public async ValueTask<Stream> OpenAsync(IStreamInfo stream, CancellationToken ct) =>
        await _youtube.Videos.Streams.GetAsync(stream, ct);
}

using System.Text.Json;

namespace VideoHome.Services;

// On-disk snapshot of the shared video state. Deliberately not VideoStateDto: Author
// (a SignalR ConnectionId) and RecievedTime (an echo-detection clock value) are
// process-scoped bookkeeping and are meaningless - actively harmful - once reloaded.
public sealed class PersistedVideoState
{
    public string? Source { get; set; }
    public string? Title { get; set; }
    public double VideoTimestamp { get; set; }
    public bool IsPlaying { get; set; }
    public List<string> CaptionsLang { get; set; } = new();
    public List<string> CaptionsPath { get; set; } = new();
}

// Keeps the shared video state across a restart or an upgrade: loads it before the
// server starts listening, and writes it back whenever it changes.
public sealed class VideoStatePersistenceService : BackgroundService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly VideoStateProvider _provider;
    private readonly ILogger<VideoStatePersistenceService> _logger;
    private readonly string _filePath;
    private readonly string _videoPath;
    private readonly string _mapTo;
    private readonly TimeSpan _interval;

    // What the file already holds, so an unchanged state is not rewritten every tick.
    private string? _lastWrittenKey;

    public VideoStatePersistenceService(
        VideoStateProvider provider,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<VideoStatePersistenceService> logger)
    {
        _provider = provider;
        _logger = logger;

        var configured = config.GetSection("VideoState")["FilePath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            _filePath = Path.Combine(env.ContentRootPath, "videostate.json");
            _logger.LogWarning(
                "VideoState:FilePath is not configured; using {Path}, which lives in the deployment " +
                "directory and will not survive an upgrade.", _filePath);
        }
        else if (!Path.IsPathRooted(configured))
        {
            // A relative path would resolve against the working directory, which for a
            // service unit is usually "/" - not where anyone would look for it.
            _filePath = Path.Combine(env.ContentRootPath, configured);
            _logger.LogWarning(
                "VideoState:FilePath '{Configured}' is relative; resolved to {Path}.", configured, _filePath);
        }
        else
        {
            _filePath = configured;
        }

        _videoPath = config.GetSection("VideoMapping")["VideoPath"] ?? "";
        _mapTo = config.GetSection("VideoMapping")["MapTo"] ?? "";
        _interval = TimeSpan.FromSeconds(
            Math.Max(1, config.GetSection("VideoState").GetValue("SaveIntervalSeconds", 2)));
    }

    // Loading here rather than in ExecuteAsync: a hosted service's StartAsync completes
    // before Kestrel binds its socket, so the state is in place before any client can ask
    // for it. ExecuteAsync gives no such guarantee - StartAsync returns at its first await.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Save();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Final flush, so the last interval's worth of watching is not lost when the
        // process is stopped for an upgrade.
        Save();
    }

    private static string KeyOf(string? source, bool isPlaying, double timestamp, IEnumerable<string> captionPaths)
        => $"{source}|{isPlaying}|{timestamp:F1}|{string.Join(',', captionPaths)}";

    // Plain playback never reaches the server: the client reports only on play, pause and
    // seek. So a pair who start a film and watch it through leave the stored timestamp
    // frozen at the moment they pressed play, and restoring that would rewind them by
    // however long they had been watching. While the state says "playing", the playhead
    // has moved on by the elapsed real time since that report.
    private static double EffectiveTimestamp(VideoStateDto state) =>
        state.IsPlaying
            ? state.VideoTimestamp + Math.Max(0, (DateTimeOffset.UtcNow - state.RecievedTime).TotalSeconds)
            : state.VideoTimestamp;

    private void Save()
    {
        try
        {
            var state = _provider.CurrentVideoState;

            // Only ever persist a state we could actually hand back. This makes every
            // failure non-destructive: a boot while the library is missing, or a bogus
            // Source pushed by anything that can reach the hub, leaves the last good record
            // on disk instead of erasing it.
            if (!IsRestorable(state.Source))
                return;

            // Nobody is connected, so nothing is advancing. Freezing here keeps the record
            // at wherever the last client left it, instead of winding it back to the last
            // thing the server was told or extrapolating a playhead that stopped moving
            // when they closed the tab.
            if (_provider.NumConnectedClients == 0)
                return;

            var timestamp = EffectiveTimestamp(state);
            var key = KeyOf(state.Source, state.IsPlaying, timestamp, state.CaptionsPath);
            if (key == _lastWrittenKey)
                return;

            var json = JsonSerializer.Serialize(new PersistedVideoState
            {
                Source = state.Source,
                Title = state.Title,
                VideoTimestamp = timestamp,
                IsPlaying = state.IsPlaying,
                CaptionsLang = state.CaptionsLang,
                CaptionsPath = state.CaptionsPath
            }, WriteOptions);

            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            // Write-then-rename, so a crash mid-write cannot leave a truncated file behind.
            // The temp file has to be a sibling: across filesystems File.Move stops being a
            // rename and loses the atomicity that makes this safe.
            var tmp = Path.Combine(dir, Path.GetFileName(_filePath) + ".tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);

            _lastWrittenKey = key;
        }
        catch (Exception e)
        {
            // Never rethrow: an exception escaping here would take the whole host down over
            // a convenience feature.
            _logger.LogWarning(e, "Could not persist video state to {Path}.", _filePath);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation("No persisted video state at {Path}; starting empty.", _filePath);
                return;
            }

            var saved = JsonSerializer.Deserialize<PersistedVideoState>(File.ReadAllText(_filePath));
            if (saved is null)
            {
                _logger.LogWarning("Persisted video state at {Path} was empty; ignoring.", _filePath);
                return;
            }

            // A restart is never a reason to start playing: whoever opens the page first
            // would be watching alone, which defeats the point of the app.
            var restored = new VideoStateDto { IsPlaying = false, Source = null, VideoTimestamp = 0 };

            if (IsRestorable(saved.Source))
            {
                restored.Source = saved.Source;
                restored.Title = saved.Title;
                restored.VideoTimestamp = saved.VideoTimestamp > 0 ? saved.VideoTimestamp : 0;

                // Pairwise, because the client zips these two lists back together.
                var langs = saved.CaptionsLang ?? new();
                var paths = saved.CaptionsPath ?? new();
                for (var i = 0; i < Math.Min(langs.Count, paths.Count); i++)
                {
                    if (TryMapToPhysical(paths[i], out var caption) && File.Exists(caption))
                    {
                        restored.CaptionsLang.Add(langs[i]);
                        restored.CaptionsPath.Add(paths[i]);
                    }
                    else
                    {
                        // Say so rather than quietly narrowing the record: the next save
                        // writes back what survived here.
                        _logger.LogWarning("Persisted subtitle track {Path} is missing; dropping it.", paths[i]);
                    }
                }

                _logger.LogInformation("Restored video state: {Source} at {Timestamp}s (paused).",
                    restored.Source, restored.VideoTimestamp);
            }
            else
            {
                // An unreachable source is poison - every joining client would try to load
                // it and end up with a dead player. Start empty instead; Save() refuses to
                // overwrite the file while the source is unavailable, so this is recoverable
                // by fixing the library and restarting.
                _logger.LogWarning(
                    "Persisted source {Source} is not an available local video (deleted, moved, library " +
                    "not mounted, or an expired stream URL); starting empty, keeping the saved record.",
                    saved.Source);
            }

            _lastWrittenKey = KeyOf(restored.Source, restored.IsPlaying, restored.VideoTimestamp, restored.CaptionsPath);
            _provider.RestoreState(restored);
        }
        catch (Exception e)
        {
            // A corrupt, truncated or hand-edited file must never stop the app from starting.
            _logger.LogError(e, "Could not read persisted video state from {Path}; starting empty.", _filePath);
        }
    }

    // What we are willing to write down and hand back after a restart. A YouTube source is
    // our own /youtube/{id} proxy path, which stays valid indefinitely because the stream
    // behind it is resolved fresh on request - unlike the signed googlevideo URL the app
    // used to broadcast, which expires within hours and could never be restored.
    private bool IsRestorable(string? source) =>
        IsYoutubeSource(source) || (TryMapToPhysical(source, out var physical) && File.Exists(physical));

    private static bool IsYoutubeSource(string? source) =>
        source is not null && source.StartsWith("/youtube/", StringComparison.Ordinal);

    // Reverse of the VideoMapping the file tree applies. Deliberately not the string
    // Replace used for captions in SyncVideo.razor: that one is unanchored and rewrites
    // every occurrence, so a file or folder named after the mount point comes back with
    // the library root spliced into the middle of the path.
    private bool TryMapToPhysical(string? webPath, out string physical)
    {
        physical = "";

        if (string.IsNullOrWhiteSpace(webPath))
            return false;

        // Remote sources (a resolved YouTube stream) cannot be restored: only the signed,
        // expiring URL was ever stored, never the video it came from. Tested by prefix
        // rather than with Uri.TryCreate, which happily parses a Linux path as a file: URI.
        if (webPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            webPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrEmpty(_mapTo) || string.IsNullOrEmpty(_videoPath))
            return false;

        if (!webPath.StartsWith(_mapTo, StringComparison.Ordinal))
            return false;

        // Anchored on a path segment, so a "/videos-elsewhere/..." path cannot ride in on
        // a "/video" mount point.
        var rest = webPath.Substring(_mapTo.Length);
        if (rest.Length > 0 && rest[0] != '/')
            return false;

        var root = Path.GetFullPath(_videoPath).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, rest.TrimStart('/')));

        // Keeps a garbage or hand-edited Source from turning File.Exists into a probe for
        // arbitrary paths outside the library.
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        physical = candidate;
        return true;
    }
}

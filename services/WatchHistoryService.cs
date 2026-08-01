using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoHome.Services;

// One row on the history page: everything watched of a given title on a given day,
// collapsed into a single span of the film.
//
// Only ever one entry per (day, title), by requirement - watch 0:00-10:00, stop, then
// 9:50-30:00 and the day shows 0:00-30:00, not two rows. That means the span is the
// outer envelope of the day's watching, so a gap in the middle (watch the start, skip
// to the end) is not represented: From is the earliest point reached and To the
// furthest. WatchedSeconds is therefore "how much of the film this covers", not
// "how long they sat there".
public sealed class WatchHistoryEntry
{
    public DateOnly Day { get; set; }
    public string Title { get; set; } = "";

    // Kept for the row's icon - a /youtube/ path gets a different one than a file.
    public string? Source { get; set; }

    public double FromSeconds { get; set; }
    public double ToSeconds { get; set; }

    // 0 when the browser never reported it, which is why the page always has a
    // fallback scale for the bars.
    public double DurationSeconds { get; set; }

    public DateTimeOffset LastWatchedUtc { get; set; }

    // Derived, so writing it would put a second copy of the same fact in the file that
    // a hand-edit could then contradict.
    [JsonIgnore]
    public double WatchedSeconds => Math.Max(0, ToSeconds - FromSeconds);
}

// Records what was watched, keyed by day and title, and keeps it across restarts.
//
// Everything happens here rather than in the page component because the record is
// shared: two people watching the same film in sync must produce one entry, not one
// each. The hub is the only place every state change passes through exactly once, so
// that is what drives this.
public sealed class WatchHistoryService
{
    // Below this, a span is a stray click - play immediately followed by pause, or the
    // pause that arrives as part of switching source - and not something anyone would
    // call "watched".
    private const double MinimumSpanSeconds = 5;

    // How far the extrapolated playhead may sit from a freshly reported one before we
    // treat the report as a seek rather than a continuation. Play and Playing both fire
    // on resume, so back-to-back reports at the same spot are normal and must not be
    // mistaken for two separate stretches.
    private const double DriftToleranceSeconds = 3;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ILogger<WatchHistoryService> _logger;
    private readonly string _filePath;

    private readonly Lock _lock = new();
    private readonly List<WatchHistoryEntry> _entries = new();

    // The stretch currently being watched, if any. Committed to an entry when playback
    // stops - which is the sync trigger - so nothing is written while a film is running.
    private OpenSpan? _open;

    private sealed record OpenSpan(
        string Title,
        string? Source,
        double DurationSeconds,
        double FromSeconds,       // where this stretch of watching began
        double ReportedPosition,  // playhead as of the last report
        DateTimeOffset ReportedUtc);

    public WatchHistoryService(IConfiguration config, IWebHostEnvironment env, ILogger<WatchHistoryService> logger)
    {
        _logger = logger;

        var configured = config.GetSection("WatchHistory")["FilePath"];

        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
        {
            _filePath = configured;
        }
        else if (!string.IsNullOrWhiteSpace(configured))
        {
            // A relative path resolves against the working directory, which for a service
            // unit is usually "/" - not where anyone would look for it.
            _filePath = Path.Combine(env.ContentRootPath, configured);
            _logger.LogWarning(
                "WatchHistory:FilePath '{Configured}' is relative; resolved to {Path}.", configured, _filePath);
        }
        else
        {
            // Default beside the video state rather than into the deployment directory,
            // so history survives an upgrade for anyone who configured that one path.
            var stateFile = config.GetSection("VideoState")["FilePath"];
            var dir = !string.IsNullOrWhiteSpace(stateFile) && Path.IsPathRooted(stateFile)
                ? Path.GetDirectoryName(stateFile)!
                : env.ContentRootPath;

            _filePath = Path.Combine(dir, "watchhistory.json");
        }

        Load();
    }

    // Called for every state change the hub accepts. Closes the open stretch when
    // playback stops, moves elsewhere, or switches film; opens a new one when playback
    // starts.
    public void Observe(VideoStateDto state)
    {
        var now = DateTimeOffset.UtcNow;
        var title = TitleOf(state);

        lock (_lock)
        {
            if (_open is not null)
            {
                var sameTitle = string.Equals(_open.Title, title, StringComparison.Ordinal);
                var extrapolated = Playhead(_open, now);

                // Plain playback never reports anything, so a report means something
                // happened: a pause, a seek, or a different film. The one exception is
                // the duplicate report on resume, which the tolerance absorbs.
                var continues =
                    state.IsPlaying &&
                    sameTitle &&
                    Math.Abs(extrapolated - state.VideoTimestamp) <= DriftToleranceSeconds;

                if (continues)
                {
                    // Re-anchor on the fresh report so the extrapolation cannot drift.
                    _open = _open with { ReportedPosition = state.VideoTimestamp, ReportedUtc = now };
                    return;
                }

                // Where the stretch actually ended. A stop reports exactly where the
                // playhead was, which beats extrapolating from the last report - and it
                // stays right even if the film was running at something other than 1x,
                // which the extrapolation cannot know about. A seek is the opposite case:
                // it reports where the playhead is *going*, so the position before the
                // jump has to be extrapolated. A switch to another film reports the new
                // one's position, which says nothing about the old one.
                var end = !state.IsPlaying && sameTitle ? state.VideoTimestamp : extrapolated;

                Commit(_open, end, now);
                _open = null;
            }

            if (state.IsPlaying && title.Length > 0)
            {
                _open = new OpenSpan(
                    title,
                    state.Source,
                    state.Duration,
                    state.VideoTimestamp,
                    state.VideoTimestamp,
                    now);
            }
        }
    }

    // For when playback ends without anyone pausing - everybody just closed the tab.
    // Without this, finishing a film and walking away would record nothing at all.
    public void FlushOpenSpan(string reason)
    {
        lock (_lock)
        {
            if (_open is null)
                return;

            var now = DateTimeOffset.UtcNow;
            _logger.LogInformation("Closing the open watch span for '{Title}' ({Reason}).", _open.Title, reason);
            Commit(_open, Playhead(_open, now), now);
            _open = null;
        }
    }

    // Copies, so the page cannot mutate the store it is rendering. Newest day first,
    // and within a day the most recently watched first.
    public List<WatchHistoryEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries
                .Select(e => new WatchHistoryEntry
                {
                    Day = e.Day,
                    Title = e.Title,
                    Source = e.Source,
                    FromSeconds = e.FromSeconds,
                    ToSeconds = e.ToSeconds,
                    DurationSeconds = e.DurationSeconds,
                    LastWatchedUtc = e.LastWatchedUtc
                })
                .OrderByDescending(e => e.Day)
                .ThenByDescending(e => e.LastWatchedUtc)
                .ToList();
        }
    }

    // A local file's title is its filename; a YouTube source carries the real one in
    // the state. Falls back to the last path segment so a state with no title still
    // produces something recognisable rather than an empty row.
    private static string TitleOf(VideoStateDto state)
    {
        if (!string.IsNullOrWhiteSpace(state.Title))
            return state.Title.Trim();

        var fromSource = state.Source?.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(fromSource) ? "" : fromSource.Trim();
    }

    // Where the playhead is now. The client only reports on play, pause and seek, so
    // during playback the position has to be extrapolated from the last report - the
    // same reasoning as VideoStatePersistenceService.EffectiveTimestamp.
    private static double Playhead(OpenSpan open, DateTimeOffset now)
    {
        var elapsed = Math.Max(0, (now - open.ReportedUtc).TotalSeconds);
        var position = open.ReportedPosition + elapsed;

        // Extrapolation runs past the end if the film finished while nobody reported it.
        return open.DurationSeconds > 0 ? Math.Min(position, open.DurationSeconds) : position;
    }

    // Caller holds _lock.
    private void Commit(OpenSpan open, double end, DateTimeOffset now)
    {
        var from = Math.Max(0, Math.Min(open.FromSeconds, end));
        var to = Math.Max(open.FromSeconds, end);

        if (to - from < MinimumSpanSeconds)
            return;

        // The day it was watched, in local time: "segmented by day" means the viewer's
        // day, not UTC's.
        var day = DateOnly.FromDateTime(now.ToLocalTime().DateTime);

        var existing = _entries.FirstOrDefault(
            e => e.Day == day && string.Equals(e.Title, open.Title, StringComparison.Ordinal));

        if (existing is null)
        {
            _entries.Add(new WatchHistoryEntry
            {
                Day = day,
                Title = open.Title,
                Source = open.Source,
                FromSeconds = from,
                ToSeconds = to,
                DurationSeconds = open.DurationSeconds,
                LastWatchedUtc = now
            });
        }
        else
        {
            // One entry per title per day: widen the span instead of adding a row.
            existing.FromSeconds = Math.Min(existing.FromSeconds, from);
            existing.ToSeconds = Math.Max(existing.ToSeconds, to);
            existing.LastWatchedUtc = now;

            // A later span may know the length when the first one did not.
            if (existing.DurationSeconds <= 0)
                existing.DurationSeconds = open.DurationSeconds;

            existing.Source ??= open.Source;
        }

        _logger.LogInformation(
            "Watch history: {Title} on {Day} now covers {From:F0}s-{To:F0}s.",
            open.Title, day, from, to);

        Save();
    }

    // Caller holds _lock. Written on every commit rather than on a timer: commits only
    // happen when playback stops, so this is rare enough that losing the last one to a
    // crash would be a worse trade than the write itself.
    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, WriteOptions);

            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            // Write-then-rename, so a crash mid-write cannot leave a truncated file
            // behind. The temp file has to be a sibling: across filesystems File.Move
            // stops being a rename and loses the atomicity that makes this safe.
            var tmp = Path.Combine(dir, Path.GetFileName(_filePath) + ".tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception e)
        {
            // Never rethrow: this runs off a hub call, and an exception escaping would
            // break playback syncing over a record-keeping feature.
            _logger.LogWarning(e, "Could not persist watch history to {Path}.", _filePath);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation("No watch history at {Path}; starting empty.", _filePath);
                return;
            }

            var saved = JsonSerializer.Deserialize<List<WatchHistoryEntry>>(File.ReadAllText(_filePath));
            if (saved is null)
            {
                _logger.LogWarning("Watch history at {Path} was empty; ignoring.", _filePath);
                return;
            }

            // Drop anything unusable rather than rendering blank rows or negative bars
            // from a hand-edited file.
            foreach (var entry in saved)
            {
                if (string.IsNullOrWhiteSpace(entry.Title) || entry.ToSeconds <= entry.FromSeconds)
                    continue;

                _entries.Add(entry);
            }

            _logger.LogInformation("Loaded {Count} watch history entries from {Path}.", _entries.Count, _filePath);
        }
        catch (Exception e)
        {
            // A corrupt, truncated or hand-edited file must never stop the app starting.
            _logger.LogError(e, "Could not read watch history from {Path}; starting empty.", _filePath);
        }
    }
}

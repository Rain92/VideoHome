using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoHome.Services;

// The three verdicts, ordered from best to worst: the day's mood is the worst review
// it collected, so "worst" is simply the highest value here.
//
// Not named just "Mood": the page component /mood compiles to VideoHome.Pages.Mood,
// which would shadow this enum inside its own file - the same trap the History page's
// component name dodges.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoodRating
{
    Happy = 0,
    Soso = 1,
    Shit = 2,
}

// One person's verdict on one day. Written once and never touched again - there is no
// update or delete anywhere in this service, which is what makes the page's "cannot be
// changed" promise true rather than merely enforced by the UI.
public sealed class MoodReview
{
    public DateOnly Day { get; set; }

    // Compared case-insensitively on insert: usernames come out of the auth cookie, and
    // "andreas" and "Andreas" must not buy a second vote.
    public string User { get; set; } = "";

    public MoodRating Rating { get; set; }
    public DateTimeOffset SubmittedUtc { get; set; }
}

// Stores how everyone felt about each day, one JSON file, kept across restarts.
//
// Same shape as WatchHistoryService: a singleton holding everything in memory behind a
// lock, written through on every change (a review is rare enough that losing the last
// one to a crash is a worse trade than the write), loaded at startup.
//
// The window and one-review-per-user rules live here rather than in the page, so the
// file cannot be talked into states the page would never have produced.
public sealed class MoodService
{
    // Today plus this many days back may still be reviewed.
    public const int ReviewWindowDays = 3;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ILogger<MoodService> _logger;
    private readonly string _filePath;

    private readonly Lock _lock = new();
    private readonly List<MoodReview> _reviews = new();

    public MoodService(IConfiguration config, IWebHostEnvironment env, ILogger<MoodService> logger)
    {
        _logger = logger;

        var configured = config.GetSection("Mood")["FilePath"];

        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
        {
            _filePath = configured;
        }
        else if (!string.IsNullOrWhiteSpace(configured))
        {
            _filePath = Path.Combine(env.ContentRootPath, configured);
            _logger.LogWarning(
                "Mood:FilePath '{Configured}' is relative; resolved to {Path}.", configured, _filePath);
        }
        else
        {
            // Default beside the video state, exactly as the watch history does, so both
            // sit wherever that one path already points.
            var stateFile = config.GetSection("VideoState")["FilePath"];
            var dir = !string.IsNullOrWhiteSpace(stateFile) && Path.IsPathRooted(stateFile)
                ? Path.GetDirectoryName(stateFile)!
                : env.ContentRootPath;

            _filePath = Path.Combine(dir, "mood.json");
        }

        Load();
    }

    // Records a verdict. Returns false - changing nothing - when this user has already
    // reviewed the day, or the day lies outside the reviewable window.
    public bool TryAddReview(DateOnly day, string user, MoodRating rating)
    {
        if (string.IsNullOrWhiteSpace(user))
            return false;

        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (day > today || day < today.AddDays(-ReviewWindowDays))
                return false;

            if (_reviews.Any(r =>
                    r.Day == day &&
                    string.Equals(r.User, user, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _reviews.Add(new MoodReview
            {
                Day = day,
                User = user,
                Rating = rating,
                SubmittedUtc = DateTimeOffset.UtcNow
            });

            Save();
            return true;
        }
    }

    // Copies, so the page cannot mutate the store it is rendering. Oldest first, the
    // order streaks and calendars read naturally in.
    public List<MoodReview> Snapshot()
    {
        lock (_lock)
        {
            return _reviews
                .Select(r => new MoodReview
                {
                    Day = r.Day,
                    User = r.User,
                    Rating = r.Rating,
                    SubmittedUtc = r.SubmittedUtc
                })
                .OrderBy(r => r.Day)
                .ToList();
        }
    }

    // Caller holds _lock. Write-then-rename, sibling temp file - same reasoning as the
    // watch history: a crash mid-write must not leave a truncated file behind.
    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_reviews, WriteOptions);

            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            var tmp = Path.Combine(dir, Path.GetFileName(_filePath) + ".tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception e)
        {
            // Never rethrow: a full disk must not take the page down with it. The
            // in-memory copy keeps serving; the restart loses only unflushed reviews.
            _logger.LogWarning(e, "Could not persist mood reviews to {Path}.", _filePath);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation("No mood reviews at {Path}; starting empty.", _filePath);
                return;
            }

            var saved = JsonSerializer.Deserialize<List<MoodReview>>(File.ReadAllText(_filePath));
            if (saved is null)
            {
                _logger.LogWarning("Mood file at {Path} was empty; ignoring.", _filePath);
                return;
            }

            // Drop anything unusable rather than rendering rows for them: a blank user
            // could never have been produced by TryAddReview, so it is a hand-edit.
            foreach (var entry in saved)
            {
                if (string.IsNullOrWhiteSpace(entry.User) || !Enum.IsDefined(entry.Rating))
                    continue;

                _reviews.Add(entry);
            }

            _logger.LogInformation("Loaded {Count} mood reviews from {Path}.", _reviews.Count, _filePath);
        }
        catch (Exception e)
        {
            // A corrupt or hand-edited file must never stop the app starting.
            _logger.LogError(e, "Could not read mood reviews from {Path}; starting empty.", _filePath);
        }
    }
}

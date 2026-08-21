using System.Collections.Concurrent;
using VideoHome.Data;
namespace VideoHome.Services;
public class VideoStateDto
{
    public bool IsPlaying { get; set; }
    public string? Source { get; set; }

    // What to show above the player. For a local file the filename says it all, but a
    // YouTube source is just /youtube/<id>, so the real title has to travel with the state.
    // Anything added here must also be copied in SyncVideo's GetStateDto, which builds a
    // fresh DTO - a field it misses is wiped by the next update any client sends.
    public string? Title { get; set; }
    public List<string> CaptionsLang { get; set; } = new();
    public List<string> CaptionsPath { get; set; } = new();
    public double VideoTimestamp { get; set; }

    // Length of the current video, or 0 before the browser has read its metadata.
    // Only the watch history uses it: it is what lets a recorded span be shown as a
    // portion of the whole film rather than a pair of bare numbers.
    public double Duration { get; set; }
    public DateTimeOffset RecievedTime { get; set; }
    public string? Author { get; set; }

    // Sync plumbing, not content - exempt from the copy-in-GetStateDto rule above.
    // Version is stamped by the server on every accepted update and only ever grows;
    // BasedOnVersion is the last Version the sender had applied when it composed this
    // update; ClientSequence orders one client's own sends among themselves. Together
    // they let the server drop a report that was overtaken on the network instead of
    // executing it and rewinding everyone.
    public long Version { get; set; }
    public long BasedOnVersion { get; set; }
    public long ClientSequence { get; set; }

    // Server-stamped: this state resumes playback somewhere the clients may not have
    // buffered yet, so they hold at it until all of them report they have. False for a
    // plain resume where the playhead has not moved - there the media is already in
    // hand on both sides and holding would only add a stutter for nothing.
    public bool RequiresSync { get; set; }

    public override string ToString() =>
        $"Playing: {IsPlaying} {VideoTimestamp}s Recieved: {RecievedTime:mm:ss} by: {Author} v{Version}";
}

// Outcome of a client reporting that it has buffered at the current sync point.
public enum ReadyReport
{
    // Still waiting on at least one other client.
    Waiting,

    // The last one we needed - everyone may resume now.
    ReleaseAll,

    // This client is late to a sync point the others have already left; it alone
    // needs releasing, or it would sit paused until its own fallback fires.
    AlreadyReleased,

    // A report for a sync point that is no longer the current one.
    Stale
}

public record VideoHomeUser(string ConnectionId, string Username, int UserConnectionNum, int Latency)
{
    public override string ToString() => $"{Username} {UserConnectionNum}";
};

public class VideoStateProvider
{
    // How long after an update a matching report from *another* client still
    // counts as that update echoing back instead of a new action.
    private const double ECHO_WINDOW_SECONDS = 2;

    // How far two reported positions may differ and still mean "the same spot".
    private const double POSITION_TOLERANCE_SECONDS = 2;

    private readonly Lock _stateLock = new();

    // maps the conneted clients to their username
    public ConcurrentDictionary<string, VideoHomeUser> ConnectedClients { get; } = new();

    public List<UserConnectionCount> ListConnectedUsers() =>
                ConnectedClients.Values
                .GroupBy(u => u.Username)
                .Select(g => new UserConnectionCount { Username = g.Key, NumConnctions = g.Count() })
                .ToList();

    public VideoHomeUser GetUser(string connectionId)
    {
        if(ConnectedClients.TryGetValue(connectionId, out var user))
            return user;
        else
            return new(connectionId, "NotFound", 0, 0);
    }

    public void AddUser(string connectionId, string username)
    {
        var userConnectionNum = ConnectedClients.Values
                                .Select(u => u.UserConnectionNum)
                                .DefaultIfEmpty(0)
                                .Max() + 1;

        // Indexer rather than Add: registering the same connection twice (a client
        // retry, a re-register after reconnect) must not throw out of the hub call.
        ConnectedClients[connectionId] = new(connectionId, username, userConnectionNum, 200);

        // A page that has just loaded - or come back from a reconnect - has buffered
        // none of the current video, so treat the next resume as one that needs
        // coordinating even if the playhead has not moved. Reopening a tab is exactly
        // when the other side would otherwise be left waiting on it.
        lock (_stateLock)
            _positionMovedSinceBarrier = true;
    }

    public void RemoveUser(string connectionId) => ConnectedClients.TryRemove(connectionId, out _);

    public void UpdateUserLatency(string connectionId, int latency)
    {
        if(ConnectedClients.TryGetValue(connectionId, out var u))
            ConnectedClients[connectionId] = u with { Latency = latency };
    }

    public int NumConnectedClients => ConnectedClients.Count;

    public VideoStateDto CurrentVideoState { get; private set; } = new() { RecievedTime = DateTimeOffset.UtcNow};

    // Grows by one per accepted update. Process-scoped on purpose: the clients whose
    // BasedOnVersion values it is compared against die with the process too.
    private long _version;

    // The sync barrier. A resume that lands somewhere the clients may not have buffered
    // opens one: they hold at the agreed position, buffer there, and report in, and only
    // once all of them have does anyone resume. Without it the client that did the
    // seeking - which already holds the media it seeked into - plays on while the other
    // is still fetching, and the gap that opens is exactly however long that fetch took.
    //
    // Deliberately not every resume. Playing on from a spot both sides are already
    // parked at needs no coordinating: they have the media, and holding would buy a
    // stutter on every press of play in exchange for nothing.
    //
    // A single current barrier rather than one per version: an older sync point stops
    // mattering the moment a newer state is accepted, so there is nothing to keep.
    private long _barrierVersion;
    private bool _barrierReleased;
    private readonly HashSet<string> _readyClients = new();

    // A move nobody has synced on yet. Seeking while paused is the case that needs it:
    // it buffers nothing at the time (the barrier only guards resuming), so the resume
    // that follows is the first moment the clients can be out of step.
    private bool _positionMovedSinceBarrier;

    // How far a reported position may sit from where playback was headed before it
    // counts as a move rather than as time passing. Anything inside this is media the
    // clients already hold, so it cannot cost them a fetch.
    private const double SEEK_DETECTION_TOLERANCE_SECONDS = 1.5;

    // The version everyone is currently holding at, or 0 if nobody is holding.
    public long BarrierVersion
    {
        get { lock (_stateLock) return _barrierVersion; }
    }

    // Called with the lock held. Extra entries in _readyClients are fine and expected -
    // a client can report and then disconnect.
    private bool AllConnectedClientsReady() =>
        ConnectedClients.Keys.All(_readyClients.Contains);

    public ReadyReport MarkClientReady(string connectionId, long version)
    {
        lock (_stateLock)
        {
            if (version <= 0 || version != _barrierVersion)
                return ReadyReport.Stale;

            if (_barrierReleased)
                return ReadyReport.AlreadyReleased;

            _readyClients.Add(connectionId);

            if (!AllConnectedClientsReady())
                return ReadyReport.Waiting;

            _barrierReleased = true;
            return ReadyReport.ReleaseAll;
        }
    }

    // A client that leaves mid-barrier can be the only one still missing, and it is
    // never going to answer. Returns the version to release everyone else at, or null
    // if the barrier is not waiting on anyone.
    public long? ClientLeftBarrier(string connectionId)
    {
        lock (_stateLock)
        {
            _readyClients.Remove(connectionId);

            if (_barrierVersion == 0 || _barrierReleased || ConnectedClients.IsEmpty)
                return null;

            if (!AllConnectedClientsReady())
                return null;

            _barrierReleased = true;
            return _barrierVersion;
        }
    }

    // A client that has waited long enough asking for everyone to be let go at once.
    // Without this each client falls back on its own timer and they resume seconds
    // apart - the very thing the barrier exists to prevent.
    public bool ForceReleaseBarrier(long version)
    {
        lock (_stateLock)
        {
            if (version <= 0 || version != _barrierVersion || _barrierReleased)
                return false;

            _barrierReleased = true;
            return true;
        }
    }

    // Did the playhead move, as opposed to playback simply having carried on? Compared
    // against where the previous state was headed by now, because a pause reported after
    // ten minutes of watching names a position ten minutes on from the last state we were
    // told about - and that is not a seek.
    private static bool PositionMoved(VideoStateDto previous, VideoStateDto next, DateTimeOffset now)
    {
        if (previous.Source != next.Source)
            return true;

        var advanced = previous.IsPlaying
            ? Math.Max(0, (now - previous.RecievedTime).TotalSeconds)
            : 0;

        var expected = previous.VideoTimestamp + advanced;
        return Math.Abs(next.VideoTimestamp - expected) > SEEK_DETECTION_TOLERANCE_SECONDS;
    }

    // Called with the lock held, on every accepted state: the sync point moves with it.
    private void ResetBarrier(VideoStateDto state, bool positionMoved)
    {
        _readyClients.Clear();
        _barrierReleased = false;

        if (positionMoved)
            _positionMovedSinceBarrier = true;

        // A pause never needs coordinating - whoever applies it late simply stops a
        // moment later, and the next resume settles it. Nor does resuming from a spot
        // nobody has moved away from.
        if (state.IsPlaying && _positionMovedSinceBarrier)
        {
            _barrierVersion = state.Version;

            // This barrier is the sync for that move; a later resume from the same spot
            // is a plain one again.
            _positionMovedSinceBarrier = false;
        }
        else
        {
            _barrierVersion = 0;
        }

        state.RequiresSync = _barrierVersion != 0;
    }

    // Seeds the state from disk at startup. Author stays null on purpose: the connection
    // that produced this state died with the previous process, and a null Author is
    // exactly what tells UpdateVideoState that nothing can be echoing it. RecievedTime is
    // stamped now only so logs read sensibly - the echo window it feeds is already
    // neutralised by the null Author.
    public void RestoreState(VideoStateDto restored)
    {
        lock (_stateLock)
        {
            restored.Author = null;
            restored.RecievedTime = DateTimeOffset.UtcNow;

            // Stamped like any accepted state, so version numbers stay monotonic within
            // this process. The values that came off disk belonged to a dead process.
            restored.Version = ++_version;
            restored.ClientSequence = 0;
            CurrentVideoState = restored;

            // Nothing has been played yet, so the first resume has to be coordinated:
            // no client holds any of this video.
            ResetBarrier(restored, positionMoved: true);
        }
    }

    public bool UpdateVideoState(VideoStateDto newstate)
    {
        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            newstate.RecievedTime = now;

            var current = CurrentVideoState;

            // Same client, lower sequence: an older report that overtook a newer one on
            // the way here. The client's event handlers are fire-and-forget, so two of
            // its sends can leave in either order; executing the older one would rewind
            // everyone to a position its author has already left.
            if (current.Author is not null &&
                current.Author == newstate.Author &&
                newstate.ClientSequence <= current.ClientSequence)
                return false;

            // Other client, composed before it had applied the state we currently hold:
            // its report crossed our broadcast on the network. First to arrive wins -
            // the hub pushes the winning state back to the loser, which converges
            // instead of dragging everyone to a position based on stale information.
            if (current.Author is not null &&
                current.Author != newstate.Author &&
                newstate.BasedOnVersion < current.Version)
                return false;

            // A client reporting the state we just handed it is echoing, not acting.
            // Rebroadcasting that would bounce the same update between the clients.
            //
            // A state nobody sent - the initial one, or one restored from disk after a
            // restart - has no Author, so by definition no client can be echoing it back:
            // the first report after startup is always a real action. Without this guard
            // the restored RecievedTime alone decides the outcome, and both choices are
            // wrong. Keep it as the *last* thing that could match, i.e. fail open: a
            // missed echo costs one redundant round-trip that settles by itself, while a
            // false echo silently swallows a real action and leaves the two sides desynced.
            var isEcho =
                current.Author is not null &&
                current.Source == newstate.Source &&
                current.IsPlaying == newstate.IsPlaying &&
                current.Author != newstate.Author &&
                (now - current.RecievedTime).TotalSeconds < ECHO_WINDOW_SECONDS &&
                Math.Abs(current.VideoTimestamp - newstate.VideoTimestamp) < POSITION_TOLERANCE_SECONDS;

            if (isEcho)
                return false;

            var moved = PositionMoved(current, newstate, now);

            newstate.Version = ++_version;
            CurrentVideoState = newstate;
            ResetBarrier(newstate, moved);
            return true;
        }
    }
}

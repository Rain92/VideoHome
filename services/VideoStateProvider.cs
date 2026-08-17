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

    public override string ToString() =>
        $"Playing: {IsPlaying} {VideoTimestamp}s Recieved: {RecievedTime:mm:ss} by: {Author} v{Version}";
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

            newstate.Version = ++_version;
            CurrentVideoState = newstate;
            return true;
        }
    }
}

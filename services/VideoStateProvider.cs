using System.Collections.Concurrent;
using VideoHome.Data;
namespace VideoHome.Services;
public class VideoStateDto
{
    public bool IsPlaying { get; set; }
    public string? Source { get; set; }
    public List<string> CaptionsLang { get; set; } = new();
    public List<string> CaptionsPath { get; set; } = new();
    public double VideoTimestamp { get; set; }
    public DateTimeOffset RecievedTime { get; set; }
    public string? Author { get; set; }

    public override string ToString() =>
        $"Playing: {IsPlaying} {VideoTimestamp}s Recieved: {RecievedTime:mm:ss} by: {Author}";
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

    public bool UpdateVideoState(VideoStateDto newstate)
    {
        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            newstate.RecievedTime = now;

            var current = CurrentVideoState;

            // A client reporting the state we just handed it is echoing, not acting.
            // Rebroadcasting that would bounce the same update between the clients.
            var isEcho =
                current.Source == newstate.Source &&
                current.IsPlaying == newstate.IsPlaying &&
                current.Author != newstate.Author &&
                (now - current.RecievedTime).TotalSeconds < ECHO_WINDOW_SECONDS &&
                Math.Abs(current.VideoTimestamp - newstate.VideoTimestamp) < POSITION_TOLERANCE_SECONDS;

            if (isEcho)
                return false;

            CurrentVideoState = newstate;
            return true;
        }
    }
}

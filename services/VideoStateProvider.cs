using VideoHome.Data;
namespace VideoHome.Services;
public class VideoStateDto
{
    public bool IsPlaying { get; set; }
    public string? Source { get; set; }
    public List<string>? Captions { get; set; }
    public double VideoTimestamp { get; set; }
    public DateTimeOffset RecievedTime { get; set; }
    public string? Author { get; set; }

    public override string ToString() =>
        $"Playing: {IsPlaying} {VideoTimestamp}s Recieved: {RecievedTime.ToString("mm:ss")} by: ${Author}";
}

public record VideoHomeUser(string ConnectionId, string Username, int UserConnectionNum, int Latency)
{
    public override string ToString() => $"{Username} {UserConnectionNum}";
};

public class VideoStateProvider
{
    private const double UPDATE_HYSTESIS_SECONDS = 2;

    // maps the conneted clients to their username
    public Dictionary<string, VideoHomeUser> ConnectedClients { get; private set; } = new();

    public List<UserConnectioncount> ListConnectedUsers() =>
                ConnectedClients.Values
                .Select(u => u.Username)
                .GroupBy(u => u)
                .Select(g => new UserConnectioncount { Username = g.Key, NumConnctions = g.Count() })
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

        ConnectedClients.Add(connectionId, new(connectionId, username, userConnectionNum, 200));
    }

    public void RemoveUser(string connectionId)
    {
        if(ConnectedClients.ContainsKey(connectionId))
            ConnectedClients.Remove(connectionId);
    }

    public void UpdateUserLatency(string connectionId, int latency)
    {
        if(ConnectedClients.TryGetValue(connectionId, out var u))
            ConnectedClients[connectionId] = new(u.ConnectionId, u.Username, u.UserConnectionNum, latency);
    }

    public int NumConnectedClients => ConnectedClients.Count;

    public VideoStateDto CurrentVideoState { get; set; } = new() { RecievedTime = DateTimeOffset.UtcNow};

    public bool UpdateVideoState(VideoStateDto newstate)
    {
        newstate.RecievedTime = DateTimeOffset.UtcNow;

        if (CurrentVideoState.Source == newstate.Source && CurrentVideoState.IsPlaying == newstate.IsPlaying)
        {
            if (CurrentVideoState.Author != newstate.Author &&
                (CurrentVideoState.RecievedTime - newstate.RecievedTime).TotalSeconds < UPDATE_HYSTESIS_SECONDS && 
                Math.Abs(CurrentVideoState.VideoTimestamp - newstate.VideoTimestamp) < UPDATE_HYSTESIS_SECONDS)
            {
                return false;
            }
        }

        CurrentVideoState = newstate;

        return true;
    }
}
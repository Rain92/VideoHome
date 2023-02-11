namespace VideoHome.Services;
public class VideoStateDto
{
    public bool IsPlaying {get; set;}
    public string? Source {get; set;}
    public List<string> Captions {get; set;}
    public double VideoTimestamp {get; set;}
    public DateTimeOffset LastUpdated {get; set;}
    public DateTimeOffset GlobalTime {get; set;}
}

public class VideoStateProvider
{
    // maps the conneted clients to their latency
    public Dictionary<string, int> ConnectedClients { get; private set; } = new();

    public int NumConnectedClients => ConnectedClients.Count;

    public VideoStateDto CurrentVideoState { get; set; } = new();


}
namespace VideoHome.Data;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;

public class VideoStateDto
{
    public bool IsPlaying {get; set;}
    public string? Source {get; set;}
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
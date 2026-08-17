using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using VideoHome.Services;

namespace VideoHome.Server.Hubs
{
    // Only our own server-side components may drive playback. The browser never connects
    // here (see AppHubToken); before this, anyone who could reach the port could change the
    // video, seek it, or register themselves as a user.
    [Authorize(AuthenticationSchemes = AppHubToken.SchemeName)]
    public class SyncVideoHub : Hub
    {
        private readonly ILogger _logger;
        private const int Numpings = 5;

        private readonly VideoStateProvider _stateProvider;
        private readonly WatchHistoryService _history;

        public SyncVideoHub(VideoStateProvider stateProvider, WatchHistoryService history, ILogger<SyncVideoHub> logger)
        {
            _logger = logger;
            _stateProvider = stateProvider;
            _history = history;
        }

        public async Task RequestState()
        {
            await Clients.Caller.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"User connected: {Context.ConnectionId}.");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var user = _stateProvider.GetUser(Context.ConnectionId);
            _stateProvider.RemoveUser(Context.ConnectionId);

            _logger.LogInformation($"User disconnected:  {user}. Number of users is {_stateProvider.NumConnectedClients}");

            // Nobody left to report a pause, so whatever was playing has effectively
            // stopped. Without this, watching a film to the end and just closing the tab
            // would leave no trace in the history at all.
            if (_stateProvider.NumConnectedClients == 0)
                _history.FlushOpenSpan("last client disconnected");

            // Everyone still here needs the new list - the caller is the one leaving.
            await Clients.All.SendAsync("ConnectedUsersChanged", _stateProvider.ListConnectedUsers());
            await base.OnDisconnectedAsync(exception);
        }

        public async Task RegisterUser(string username)
        {
            _stateProvider.AddUser(Context.ConnectionId, username);
            _logger.LogInformation($"User registered: {Context.ConnectionId} {_stateProvider.GetUser(Context.ConnectionId)}. Number of users is {_stateProvider.NumConnectedClients}");

            // Not just the caller: the clients already watching have to see the newcomer.
            await Clients.All.SendAsync("ConnectedUsersChanged", _stateProvider.ListConnectedUsers());
        }

        public async Task Pong(int n, DateTimeOffset initialtime)
        {
            if (n <= 0)
            {
                await Clients.Caller.SendAsync("Ping", 1, DateTimeOffset.UtcNow);
            }
            else if (n < Numpings)
            {
                await Clients.Caller.SendAsync("Ping", n + 1, initialtime);
            }
            else
            {
                var timediff = DateTimeOffset.UtcNow - initialtime;
                var latency = (int)(timediff.TotalMilliseconds / (Numpings * 2));

                _stateProvider.UpdateUserLatency(Context.ConnectionId, latency);
                _logger.LogInformation($"Client {_stateProvider.GetUser(Context.ConnectionId)} latency was measured at {latency}ms");
            }
        }

        public async Task UpdateState(VideoStateDto newstate)
        {
            // Stamped here, not client side, so the echo detection can rely on it.
            newstate.Author = Context.ConnectionId;

            if (_stateProvider.UpdateVideoState(newstate))
            {
                // Only accepted updates: an echo is one client repeating what it was
                // handed, and counting it would record the same watching twice.
                _history.Observe(newstate);

                _logger.LogInformation($"State received by {_stateProvider.GetUser(Context.ConnectionId)}. Updating other clients.");
                await Clients.Others.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);
            }
            else
            {
                _logger.LogInformation($"Ignored state received by {_stateProvider.GetUser(Context.ConnectionId)}.");

                // Rejected means the caller acted on - or echoed - information that is no
                // longer current. Hand it the state that won so it converges; a client that
                // has already applied this version skips it by the version check, so this
                // costs nothing in the common echo case.
                await Clients.Caller.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);
            }
        }
    }
}

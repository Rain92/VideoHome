using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using VideoHome.Services;

namespace VideoHome.Server.Hubs
{
    // [Authorize]
    public class SyncVideoHub : Hub
    {
        private readonly ILogger _logger;
        private const int NUMPINGS = 5;

        private readonly VideoStateProvider _stateProvider;
        private readonly AuthenticationStateProvider _authenticationStateProvider;
        public SyncVideoHub(VideoStateProvider stateProvider, ILogger<WebsiteAuthenticator> logger, AuthenticationStateProvider authenticationStateProvider)
        {
            _logger = logger;
            _stateProvider = stateProvider;
            _authenticationStateProvider = authenticationStateProvider;
        }

        public async Task RequestState()
        {
            await Clients.Caller.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);
        }

        public override async Task OnConnectedAsync()
        {
            // var authstate = await _authenticationStateProvider.GetAuthenticationStateAsync();
            // var username = authstate.User?.Identity?.Name ?? "Andi";
            var username = "Andi";
            if (username == null)
            {
                throw new Exception("No user authenticated!");
            }

            _stateProvider.AddUser(Context.ConnectionId, username);
          
            _logger.LogInformation($"User connected: {_stateProvider.GetUser(Context.ConnectionId)}. Number of users is {_stateProvider.NumConnectedClients}");

            await base.OnConnectedAsync();
        }


        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation($"User disconnected:  {_stateProvider.GetUser(Context.ConnectionId)}. Number of users is {_stateProvider.NumConnectedClients}");

            _stateProvider.RemoveUser(Context.ConnectionId);

            await base.OnDisconnectedAsync(exception);
        }

        public async Task Pong(int n, DateTimeOffset initialtime)
        {
            if (n <= 0)
            {
                await Clients.Caller.SendAsync("Ping", 1, DateTimeOffset.UtcNow);
            }
            else if (n < NUMPINGS)
            {
                await Clients.Caller.SendAsync("Ping", n + 1, initialtime);
            }
            else
            {
                var timediff = DateTimeOffset.UtcNow - initialtime;
                var latency = (int)(timediff.TotalMilliseconds / (NUMPINGS * 2));

                _stateProvider.UpdateUserLatency(Context.ConnectionId, latency);

                _logger.LogInformation($"Client {_stateProvider.GetUser(Context.ConnectionId)} latency was measured at {latency}ms");
            }
        }

        public async Task UpdateState(VideoStateDto newstate)
        {
            if (_stateProvider.UpdateVideoState(newstate))
            {
                _logger.LogInformation($"State recieved by {_stateProvider.GetUser(Context.ConnectionId)}. Updating other clients.");
                await Clients.Others.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);
            }
            else
            {
                _logger.LogInformation($"Ignored state recieved by {_stateProvider.GetUser(Context.ConnectionId)}.");
            }
        }
    }
}

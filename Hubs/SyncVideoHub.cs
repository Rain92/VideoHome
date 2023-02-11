using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using VideoHome.Services;

namespace VideoHome.Server.Hubs
{
    // [Authorize]
    public class SyncVideoHub : Hub
    {
        private readonly ILogger _logger;
        private const int NUMPINGS = 5;
        private const double UPDATE_HYSTESIS_SECONDS = 1;

        private readonly VideoStateProvider _stateProvider;
        public SyncVideoHub(VideoStateProvider stateProvider, ILogger<WebsiteAuthenticator> logger)
        {
            _logger = logger;
            _stateProvider = stateProvider;
        }
        
        public async Task RequestState()
        {
            await Clients.Caller.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);
        }


        public override async Task OnConnectedAsync()
        {
            _stateProvider.ConnectedClients.Add(Context.ConnectionId, 200);
            await Clients.Caller.SendAsync("Ping", 1, DateTimeOffset.UtcNow);

            _logger.LogInformation($"User connected: {Context.ConnectionId}. Number of users is {_stateProvider.NumConnectedClients}");
        
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _stateProvider.ConnectedClients.Remove(Context.ConnectionId);

            _logger.LogInformation($"User disconnected: {Context.ConnectionId}. Number of users is {_stateProvider.NumConnectedClients}");
        
            await base.OnDisconnectedAsync(exception);
        }

        public async Task Pong(int n, DateTimeOffset initialtime)
        {
            if(n < NUMPINGS)
            {
                await Clients.Caller.SendAsync("Ping", n+1, initialtime);
            }
            else
            {
                var timediff = DateTimeOffset.UtcNow - initialtime;
                var latency = (int)(timediff.TotalMilliseconds / (NUMPINGS*2));
                

                _stateProvider.ConnectedClients[Context.ConnectionId] = latency;

                _logger.LogInformation($"Client {Context.ConnectionId} latency was measured at {latency}ms");
            }
        }

        public async Task UpdateState(VideoStateDto newstate)
        {
            if (newstate.LastUpdated < _stateProvider.CurrentVideoState.LastUpdated)
            {
                // stale update, ignore
                _logger.LogInformation($"Stale state recieved, ignoring.");
                return;
            }
            var td = (_stateProvider.CurrentVideoState.LastUpdated - newstate.LastUpdated).TotalSeconds;
            // if (td > 0 && td < UPDATE_HYSTESIS_SECONDS)
            // {
            //     // update is too soon, ignore
            //     _logger.LogInformation($"State recieved too soon {td}, ignoring.");
            //     return;
            // }

            _stateProvider.CurrentVideoState = newstate;
            _logger.LogInformation($"State recieved. Updating other clients.");
            await Clients.Others.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);

        }
    }
}

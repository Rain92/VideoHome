using Microsoft.AspNetCore.SignalR;
using VideoHome.Data;
using System.Diagnostics;

namespace VideoHome.Server.Hubs
{
    public class SyncVideoHub : Hub
    {
        private const int NUMPINGS = 5;
        private const double UPDATE_HYSTESIS_SECONDS = 1;

        private readonly VideoStateProvider _stateProvider;
        public SyncVideoHub(VideoStateProvider stateProvider)
        {
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

            Console.WriteLine($"User connected: {Context.ConnectionId}. Number of users is {_stateProvider.NumConnectedClients}");
        
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _stateProvider.ConnectedClients.Remove(Context.ConnectionId);

            Console.WriteLine($"User disconnected: {Context.ConnectionId}. Number of users is {_stateProvider.NumConnectedClients}");
        
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

                Console.WriteLine($"Client {Context.ConnectionId} latency was measured at {latency}ms");
            }
        }

        public async Task UpdateState(VideoStateDto newstate)
        {
            if (newstate.LastUpdated < _stateProvider.CurrentVideoState.LastUpdated)
            {
                // stale update, ignore
                Console.WriteLine($"Stale state recieved, ignoring.");
                return;
            }
            var td = (_stateProvider.CurrentVideoState.LastUpdated - newstate.LastUpdated).TotalSeconds;
            // if (td > 0 && td < UPDATE_HYSTESIS_SECONDS)
            // {
            //     // update is too soon, ignore
            //     Console.WriteLine($"State recieved too soon {td}, ignoring.");
            //     return;
            // }

            _stateProvider.CurrentVideoState = newstate;
            Console.WriteLine($"State recieved. Updating other clients.");
            await Clients.Others.SendAsync("ReceiveState", _stateProvider.CurrentVideoState);

        }
    }
}

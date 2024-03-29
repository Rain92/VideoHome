﻿using Microsoft.AspNetCore.SignalR;
using VideoHome.Services;
using Microsoft.AspNetCore.Authorization;

namespace VideoHome.Server.Hubs
{
    // [Authorize]
    public class CounterHub : Hub
    {

        private CounterService CounterService { get; set; }

        public CounterHub(CounterService counterService)
        {
            CounterService = counterService;
        }

        public async Task IncrementCounter()
        {
            CounterService.IncrementCounter();
            await UpdateCounter();
        }

        public async Task UpdateCounter()
        {
            await Clients.All.SendAsync("UpdateCounter", CounterService.GlobalCounter);
        }

        public override async Task OnConnectedAsync()
        {
            await Clients.Caller.SendAsync("UpdateCounter", CounterService.GlobalCounter);
        }
    }
}

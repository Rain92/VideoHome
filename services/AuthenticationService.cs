using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using VideoHome.Data;
using System.Text.Json;

namespace VideoHome.Services
{
    public class WebsiteAuthenticator : AuthenticationStateProvider
    {
        private readonly ILogger _logger;
        private readonly ProtectedLocalStorage _protectedLocalStorage;
        private readonly UserService _userService;

        public WebsiteAuthenticator(ProtectedLocalStorage protectedLocalStorage, UserService userService, ILogger<WebsiteAuthenticator> logger)
        {
            _logger = logger;
            _protectedLocalStorage = protectedLocalStorage;
            _userService = userService;
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var principal = new ClaimsPrincipal();

            try
            {
                var storedPrincipal = await _protectedLocalStorage.GetAsync<string>("identity");

                if (storedPrincipal.Success)
                {
                    var user = JsonSerializer.Deserialize<User>(storedPrincipal.Value);
                    var isLookUpSuccess = _userService.CheckCredentials(user);

                    if (isLookUpSuccess)
                    {
                        var identity = CreateIdentityFromUser(user);
                        principal = new(identity);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
            }

            return new AuthenticationState(principal);
        }

        public async Task<bool> LoginAsync(User user)
        {
            var isSuccess = _userService.CheckCredentials(user);
            var principal = new ClaimsPrincipal();

            if (isSuccess)
            {
                var identity = CreateIdentityFromUser(user);
                principal = new ClaimsPrincipal(identity);
                await _protectedLocalStorage.SetAsync("identity", JsonSerializer.Serialize(user));
            }

            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));

            return isSuccess;
        }

        public async Task LogoutAsync()
        {
            await _protectedLocalStorage.DeleteAsync("identity");
            var principal = new ClaimsPrincipal();
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(principal)));
        }

        private ClaimsIdentity CreateIdentityFromUser(User user)
        {
            var result = new ClaimsIdentity(new Claim[]
            {
                new (ClaimTypes.Name, user.Username),
                new (ClaimTypes.Hash, user.Password),
            }, "BlazorSchool");

            var roles = _userService.GetUserRoles(user);

            foreach (string role in roles)
            {
                result.AddClaim(new(ClaimTypes.Role, role));
            }

            return result;
        }
    }
}
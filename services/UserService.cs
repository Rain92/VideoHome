using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Text.Json;
using VideoHome.Data;
using System.Diagnostics;

namespace VideoHome.Services
{
    public class UserService
    {
        private readonly ILogger _logger;
        const string USERFILEPATH = "users.json";
        Dictionary<string, string> _users;
        public UserService(ILogger<WebsiteAuthenticator> logger)
        {
            _logger = logger;
            
            _logger.LogDebug("Loading users.");
            try
            {
                var path = Path.Join(AppContext.BaseDirectory, USERFILEPATH);
                _users = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
                _logger.LogDebug("Loaded users:");
                foreach(var u in _users.Keys)
                {
                    _logger.LogDebug(u);
                }
            }
            catch(Exception e)
            {
                _logger.LogError(e.Message);
                _users = new();
            }
        }

        public bool CheckCredentials(User user)
        {
            var validUser = _users.TryGetValue(user.Username, out var pw) && pw == user.Password;
            _logger.LogDebug($"Checking credentials: {user.Username} {user.Password} -> {validUser}");
            return validUser;
        }

        public List<string> GetUserRoles(User user)
        {
            return new();
        }
    }
}
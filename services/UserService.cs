using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Text.Json;
using VideoHome.Data;

namespace VideoHome.Services
{
    public class UserService
    {
        const string USERFILEPATH = "users.json";
        private readonly ILogger _logger;
        Dictionary<string, string> _users;
        public UserService(ILogger<UserService> logger)
        {
            _logger = logger;

            _logger.LogInformation("Loading users:");
            try
            {
                var path = Path.Join(AppContext.BaseDirectory, USERFILEPATH);
                _users = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
                foreach(var u in _users.Keys)
                {
                    _logger.LogInformation(u);
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
            _logger.LogInformation($"Checking credentials: {user.Username} {user.Password} -> {validUser}");
            return validUser;
        }

        public List<string> GetUserRoles(User user)
        {
            return new();
        }
    }
}
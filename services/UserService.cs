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
        const string USERFILEPATH = "users.json";
        Dictionary<string, string> _users;
        public UserService()
        {
            try
            {
                _users = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(USERFILEPATH)) ?? new();
                foreach(var u in _users.Keys)
                    Debug.WriteLine(u);
            }
            catch(Exception e)
            {
                Debug.WriteLine(e.Message);
                _users = new();
            }
        }

        public bool CheckCredentials(User user)
        {
            return  _users.TryGetValue(user.Username, out var pw) && pw == user.Password;
        }

        public List<string> GetUserRoles(User user)
        {
            return new();
        }
    }
}
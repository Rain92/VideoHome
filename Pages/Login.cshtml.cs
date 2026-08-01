using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VideoHome.Data;
using VideoHome.Services;

namespace VideoHome.Pages;

// Signing in has to happen over a real HTTP request: a cookie cannot be set from inside a
// Blazor circuit, because by then the response headers are long gone. That is why this is a
// Razor Page and not a component - and it is the cookie that lets the browser prove who it
// is when it fetches the video files themselves, which the circuit could never do for it.
public class LoginModel : PageModel
{
    private readonly UserService _userService;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(UserService userService, ILogger<LoginModel> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? Error { get; private set; }

    public class InputModel
    {
        [Required]
        public string Username { get; set; } = "";

        [Required]
        public string Password { get; set; } = "";
    }

    public IActionResult OnGet(string? returnUrl = null)
    {
        // Already signed in and just visiting /login - send them where they were going.
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturnUrl(returnUrl));

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return Page();

        var user = new User { Username = Input.Username, Password = Input.Password };
        if (!_userService.CheckCredentials(user))
        {
            _logger.LogWarning("Failed login attempt for {Username}.", Input.Username);
            Error = "Wrong credentials!";
            return Page();
        }

        // Only the name and any roles go into the ticket. The old identity carried the
        // password in a ClaimTypes.Hash claim; nothing read it, and it has no business
        // travelling with every request.
        var claims = new List<Claim> { new(ClaimTypes.Name, user.Username) };
        claims.AddRange(_userService.GetUserRoles(user).Select(r => new Claim(ClaimTypes.Role, r)));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        _logger.LogInformation("User {Username} signed in.", user.Username);
        return Redirect(SafeReturnUrl(returnUrl));
    }

    // Only ever bounce back to somewhere on this site, so a crafted ?returnUrl cannot turn
    // the login form into a redirector to someone else's page.
    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}

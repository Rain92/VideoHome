using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace VideoHome.Services;

// The hubs are driven by the Blazor components, which run on the server: the HubConnection
// in SyncVideo/Counter is a .NET client talking back to this same process, and the browser
// never opens /syncvideohub at all. So there is no user cookie on those connections to
// authorise against - but the endpoints are still reachable by anyone who can open a socket
// to the server, which is how an outsider could drive playback or impersonate a user.
//
// This secret is minted at startup, only ever handed to our own components, and never
// reaches the browser. Checking it is what stops anyone else from talking to the hubs; the
// question of whether a *person* may act is already settled by the [Authorize] on the page
// that owns the component.
public sealed class AppHubToken
{
    public const string SchemeName = "AppHub";

    public string Value { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed class AppHubTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AppHubToken _token;

    public AppHubTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppHubToken token)
        : base(options, logger, encoder)
    {
        _token = token;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A WebSocket handshake cannot carry an Authorization header, so SignalR passes the
        // token as a query parameter there and as a bearer header on the other transports.
        var presented = Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            var header = Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                presented = header["Bearer ".Length..];
        }

        if (string.IsNullOrEmpty(presented))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(_token.Value)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid hub token."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "VideoHome")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

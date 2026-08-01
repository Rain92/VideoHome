using VideoHome.Services;
using VideoHome.Server.Hubs;
using Microsoft.AspNetCore.ResponseCompression;
using MudBlazor.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

#region snippet_ConfigureServices
var services = builder.Services;
services.AddRazorPages();
services.AddServerSideBlazor(
    options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(10);
        options.DetailedErrors = true;
    })
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(20);
        options.EnableDetailedErrors = true;
        options.HandshakeTimeout = TimeSpan.FromSeconds(20);
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.MaximumParallelInvocationsPerClient = 1;
        options.MaximumReceiveMessageSize = 2 * 1024 * 1024;
        options.StreamBufferCapacity = 30;
    });

services.AddSingleton<UserService>();

// A real cookie, rather than an identity kept in browser localStorage: only a cookie is
// sent with the plain HTTP requests the <video> element makes, so it is the only thing that
// can keep the video files themselves from being world-readable. Blazor Server picks the
// signed-in user up from the request that opens the circuit, so AuthenticationStateProvider
// needs no custom implementation any more.
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ReturnUrlParameter = "returnUrl";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
    })
    .AddScheme<AuthenticationSchemeOptions, AppHubTokenAuthHandler>(AppHubToken.SchemeName, null);

services.AddAuthorization();
services.AddSingleton<AppHubToken>();

services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/octet-stream" });
});
// services.AddMatBlazor();
services.AddMudServices();
services.AddBootstrapBlazor(options =>
    {
        options.ToastDelay = 4000;
    });
services.AddSingleton<VideoStateProvider>();
services.AddSingleton<YoutubeStreamService>();
services.AddSingleton<WatchHistoryService>();
services.AddHostedService<VideoStatePersistenceService>();

#endregion

var app = builder.Build();

// Resolved eagerly so the history file is read - and any problem with it logged - at
// startup, rather than by whoever happens to open the page first.
var watchHistory = app.Services.GetRequiredService<WatchHistoryService>();

// A stop while a film is running would otherwise lose that stretch of watching: the
// clients never get the chance to report the pause that would have recorded it.
app.Lifetime.ApplicationStopping.Register(() => watchHistory.FlushOpenSpan("server shutting down"));

#region snippet_Configure
app.UseResponseCompression();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

//app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

var extensionProvider = new FileExtensionContentTypeProvider();
extensionProvider.Mappings.Add(".vtt", "text/vtt");
extensionProvider.Mappings.Add(".mkv", "video/x-matroska");

var videoRequestPath = builder.Configuration.GetSection("VideoMapping")["MapTo"] ?? "/video";

// The library used to be served to anyone who could guess a filename. The <video> element
// makes an ordinary HTTP request, so the only thing that can gate it is the auth cookie -
// hence the branch here rather than an [Authorize] attribute, which static files never see.
// This has to sit after UseAuthentication so there is a user to check.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments(videoRequestPath),
    branch =>
    {
        branch.Use(async (context, next) =>
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                // Challenge rather than 403: a browser following a <video> src gets sent to
                // the login page instead of a bare error.
                await context.ChallengeAsync();
                return;
            }

            await next();
        });

        branch.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(builder.Configuration.GetSection("VideoMapping")["VideoPath"]!),
            RequestPath = new PathString(videoRequestPath),
            ContentTypeProvider = extensionProvider
        });
    });

app.MapBlazorHub();
app.MapHub<SyncVideoHub>("/syncvideohub");

// Both browsers play a YouTube video through here rather than being handed the signed
// googlevideo URL, which is bound to this server's IP and expires in a few hours. Range
// support is what makes the progress bar work, and it is why the underlying stream has to
// be seekable rather than just piped.
app.MapGet("/youtube/{videoId}", async (
        string videoId,
        YoutubeStreamService youtube,
        CancellationToken cancellationToken) =>
    {
        var id = YoutubeStreamService.TryParseVideoId(videoId);
        if (id is null)
            return Results.BadRequest("Not a YouTube video id.");

        var resolved = await youtube.ResolveAsync(id, cancellationToken);
        if (resolved is null)
            return Results.NotFound("That video has no stream this player can use.");

        var stream = await youtube.OpenAsync(resolved.Value.Stream, cancellationToken);

        return Results.Stream(
            stream,
            contentType: $"video/{resolved.Value.Stream.Container.Name}",
            enableRangeProcessing: true);
    })
    .RequireAuthorization();

app.MapRazorPages();
app.MapFallbackToPage("/_Host");

app.Run();
#endregion
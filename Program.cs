using VideoHome.Services;
using VideoHome.Server.Hubs;
using Microsoft.AspNetCore.ResponseCompression;
using MudBlazor.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Components.Authorization;

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
services.AddScoped<WebsiteAuthenticator>();
services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<WebsiteAuthenticator>());

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
services.AddSingleton<CounterService>();
services.AddSingleton<VideoStateProvider>();

#endregion

var app = builder.Build();

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

app.MapBlazorHub();
app.MapHub<CounterHub>("/counterhub");
app.MapHub<SyncVideoHub>("/syncvideohub");

app.MapFallbackToPage("/_Host");

var extensionProvider = new FileExtensionContentTypeProvider();
extensionProvider.Mappings.Add(".vtt", "text/vtt");

app.UseStaticFiles(new StaticFileOptions()
{
    FileProvider = new PhysicalFileProvider(builder.Configuration.GetSection("VideoMapping")["VideoPath"]),
    RequestPath = new PathString(builder.Configuration.GetSection("VideoMapping")["MapTo"]),
    ContentTypeProvider = extensionProvider
});

app.Run();
#endregion
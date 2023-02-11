using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using VideoHome.Data;
using VideoHome.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using VideoHome.Server.Hubs;
using Microsoft.AspNetCore.ResponseCompression;
using MatBlazor;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

#region snippet_ConfigureServices
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(
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
        options.MaximumReceiveMessageSize = 2 * 1024* 1024;
        options.StreamBufferCapacity = 30;
    });

builder.Services.AddSingleton<UserService>();
builder.Services.AddScoped<WebsiteAuthenticator>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<WebsiteAuthenticator>());

builder.Services.AddResponseCompression(opts =>
{
	opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
		new[] { "application/octet-stream" });
});
builder.Services.AddMatBlazor();
builder.Services.AddSingleton<CounterService>();
builder.Services.AddSingleton<VideoStateProvider>();

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

app.UseStaticFiles(new StaticFileOptions() {
    FileProvider = new PhysicalFileProvider(builder.Configuration.GetSection("VideoMapping")["VideoPath"]),
    RequestPath = new PathString(builder.Configuration.GetSection("VideoMapping")["MapTo"]),
    ContentTypeProvider = extensionProvider
});

app.Run();
#endregion


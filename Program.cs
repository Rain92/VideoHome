using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using VideoHome.Data;
using VideoHome.Server.Hubs;
using Microsoft.AspNetCore.ResponseCompression;
using MatBlazor;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

#region snippet_ConfigureServices
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(
    options => 
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(5);
        options.DetailedErrors = true;
    })
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(10);
        options.EnableDetailedErrors = true;
        options.HandshakeTimeout = TimeSpan.FromSeconds(10);
        options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        options.MaximumParallelInvocationsPerClient = 1;
        options.MaximumReceiveMessageSize = 2 * 1024* 1024;
        options.StreamBufferCapacity = 30;
    });

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

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapHub<ChatHub>("/chathub");
app.MapHub<CounterHub>("/counterhub");
app.MapHub<SyncVideoHub>("/syncvideohub");

app.MapFallbackToPage("/_Host");

app.UseStaticFiles(new StaticFileOptions() {
    FileProvider = new PhysicalFileProvider(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos")),
    RequestPath = new PathString("/video")
});

app.Run();
#endregion


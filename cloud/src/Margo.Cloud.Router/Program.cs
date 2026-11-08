using System.Net;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Margo.Cloud.Router;
using Margo.Cloud.Router.Authentication;

// Python (removed at the cutover): router/app.py main.
var builder = WebApplication.CreateBuilder(args);
RouterSettings settings;
try
{
    settings = RouterSettings.Load(builder.Configuration);
}
catch (RouterStartupException exc)
{
    // Without the blueprint there is no audience to check tokens against: accept nothing.
    await Console.Error.WriteLineAsync(exc.Message);
    return 1;
}

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss,fff ";
    options.UseUtcTimestamp = true;
});
builder.Logging.SetMinimumLevel(settings.LogLevel);
builder.Logging.AddFilter(category: null, settings.LogLevel); // LOG_LEVEL wins over any appsettings default

// Request lines carry the voice page's one-time sign-in code in the query string: never log them.
var quiet = settings.LogLevel > LogLevel.Warning ? settings.LogLevel : LogLevel.Warning;
builder.Logging.AddFilter("Microsoft.AspNetCore", quiet);
builder.Logging.AddFilter("System.Net.Http", quiet);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    var addresses = settings.Host.Length == 0
        ? [IPAddress.Any]
        : IPAddress.TryParse(settings.Host, out var address) ? [address] : Dns.GetHostAddresses(settings.Host);
    foreach (var listen in addresses)
    {
        kestrel.Listen(listen, settings.Port);
    }
});

TokenCredential credential = settings.ManagedIdentityClientId is { } clientId
    ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
    : new DefaultAzureCredential();
var routes = new BlobRouteSource(new BlobClient(new Uri(settings.RoutesUrl), credential));
var queue = new StorageQueueProvisioningSink(StorageQueueProvisioningSink.CreateClient(new Uri(settings.ProvisionQueueUrl), credential));
builder.Services.AddMargoRouter(new AgentAuthSettings(settings.ClientId, settings.TenantId), routes, queue, new HttpJwksSource());

var app = builder.Build();
app.UseMargoRouter();
await app.RunAsync();
return 0;

/// <summary>The entry point, visible to <c>WebApplicationFactory</c> in the tests.</summary>
public partial class Program;

using System.Net;
using Margo.Cloud.Host;

// Python: margo_autopilot.host.app.main (python -m margo_autopilot.host).
var builder = WebApplication.CreateBuilder(args);
HostSettings settings;
try
{
    settings = HostSettings.Load(builder.Configuration);
}
catch (HostStartupException exc)
{
    // Without the blueprint there is no audience to check tokens against: accept nothing.
    await Console.Error.WriteLineAsync(exc.Message);
    return 1;
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    var addresses = settings.ListenHost.Length == 0
        ? [IPAddress.Any]
        : IPAddress.TryParse(settings.ListenHost, out var address) ? [address] : Dns.GetHostAddresses(settings.ListenHost);
    foreach (var listen in addresses)
    {
        kestrel.Listen(listen, settings.Port);
    }
});

MargoHost.Configure(builder, settings);
WebApplication app;
try
{
    app = builder.Build();
    MargoHost.Map(app);
}
catch (Exception exc) when (exc is InvalidOperationException or ArgumentException or Margo.Cloud.State.Core.PyValueError or IOException or UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exc.GetType().Name + ": " + exc.Message);
    return 1;
}

await app.RunAsync();
return 0;

/// <summary>The entry point, visible to <c>WebApplicationFactory</c> in the tests.</summary>
public partial class Program;

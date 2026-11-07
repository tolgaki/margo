using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Azure.Storage.Queues;
using Margo.Cloud.Router.Authentication;
using Margo.Cloud.Router.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>Configuration, the entry point and the queue encoding.</summary>
public sealed class StartupTests
{
    private const string ClientIdKey = "CONNECTIONS:SERVICE_CONNECTION:SETTINGS:CLIENTID";
    private const string TenantIdKey = "CONNECTIONS:SERVICE_CONNECTION:SETTINGS:TENANTID";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static readonly (string, string)[] Complete =
    [
        (ClientIdKey, Tokens.ClientId),
        (TenantIdKey, Tokens.TenantId),
        ("MARGO_ROUTES_URL", "https://storage.example.com/margo-routes/routes.json"),
        ("MARGO_PROVISION_QUEUE_URL", "https://storage.example.com/margo-provision"),
    ];

    [Fact]
    public void The_router_refuses_to_start_without_the_blueprint()
    {
        foreach (var missing in (string[])[ClientIdKey, TenantIdKey])
        {
            var values = Complete.Where(v => v.Item1 != missing).ToArray();
            var exc = Assert.Throws<RouterStartupException>(() => RouterSettings.Load(Configuration(values)));
            Assert.Equal("the agent blueprint client ID is not set; see docs/autopilot-deployment.md", exc.Message);
            var empty = Complete.Select(v => v.Item1 == missing ? (v.Item1, string.Empty) : v).ToArray();
            Assert.Throws<RouterStartupException>(() => RouterSettings.Load(Configuration(empty)));
        }

        // The blueprint is checked first, as in Python.
        Assert.Equal(RouterSettings.NoBlueprintMessage, Assert.Throws<RouterStartupException>(() => RouterSettings.Load(Configuration())).Message);
        Assert.Throws<RouterStartupException>(() => RouterSettings.Load(Configuration(Complete[..3])));
        Assert.Throws<RouterStartupException>(() => RouterSettings.Load(Configuration([.. Complete[..2], Complete[3]])));
    }

    [Fact]
    public void Settings_come_from_the_environment_variables_main_bicep_sets()
    {
        var settings = RouterSettings.Load(Configuration([.. Complete, ("AZURE_CLIENT_ID", "fictional-identity")]));
        Assert.Equal(new RouterSettings(Tokens.ClientId, Tokens.TenantId, "https://storage.example.com/margo-routes/routes.json",
            "https://storage.example.com/margo-provision", "fictional-identity", "0.0.0.0", 8088, LogLevel.Information), settings);
        var custom = RouterSettings.Load(Configuration([.. Complete, ("AZURE_CLIENT_ID", ""), ("HOST", "127.0.0.1"), ("PORT", " 9000 "), ("LOG_LEVEL", "debug")]));
        Assert.Equal((null, "127.0.0.1", 9000, LogLevel.Debug), (custom.ManagedIdentityClientId, custom.Host, custom.Port, custom.LogLevel));
        Assert.Throws<RouterStartupException>(() => RouterSettings.Load(Configuration([.. Complete, ("PORT", "http")])));

        // The double-underscore names map to these keys; the bicep file sets exactly these variables.
        var bicep = File.ReadAllText(Path.Combine(RepositoryRoot(), "autopilot", "infra", "main.bicep"));
        foreach (var name in (string[])["AZURE_CLIENT_ID", "MARGO_ROUTES_URL", "MARGO_PROVISION_QUEUE_URL",
                     "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID", "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID"])
        {
            Assert.Contains("{ name: '" + name + "'", bicep, StringComparison.Ordinal);
        }

        Assert.Contains("targetPort: 8088", bicep, StringComparison.Ordinal);
        var environment = new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID", "x")]).Build();
        Assert.Null(environment[ClientIdKey]); // only the environment variable provider maps "__"
    }

    [Theory]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("ERROR", LogLevel.Error)]
    [InlineData("CRITICAL", LogLevel.Critical)]
    [InlineData("fatal", LogLevel.Critical)]
    [InlineData("NOTSET", LogLevel.Trace)]
    [InlineData("loud", LogLevel.Information)]
    public void Log_levels_follow_python_logging(string value, LogLevel expected) => Assert.Equal(expected, RouterSettings.ParseLogLevel(value));

    private sealed class RouterFactory(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
    {
        public FakeProvisioningSink Sink { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRouteSource>(new FakeRouteSource());
                services.AddSingleton<IProvisioningSink>(Sink);
                services.AddSingleton<IJwksSource>(new FakeJwksSource());
            });
        }
    }

    [Fact]
    public async Task The_entry_point_serves_the_router()
    {
        await using var factory = new RouterFactory(Complete);
        using var client = factory.CreateClient();
        var health = await client.GetAsync(new Uri("/liveness", UriKind.Relative), Cancel);
        Assert.Equal((HttpStatusCode.OK, "{\"status\": \"ok\"}"), (health.StatusCode, await health.Content.ReadAsStringAsync(Cancel)));
        var messages = await client.PostAsync(new Uri("/api/messages", UriKind.Relative), new StringContent("{}"), Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, messages.StatusCode);

        // A real token with the replaced key source reaches decide(): an unknown instance is provisioned.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages")
        {
            Content = new StringContent("{\"recipient\": {\"agenticAppId\": \"agent-identity-1\", \"agenticUserId\": \"margo-user-1\"}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Tokens.Valid(DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(request, Cancel)).StatusCode);
        Assert.Single(factory.Sink.Sent);
    }

    [Fact]
    public async Task The_entry_point_exits_without_the_blueprint()
    {
        await using var factory = new RouterFactory(Complete[2..]);
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        await Task.CompletedTask;
    }

    [Fact]
    public void Queue_messages_are_plain_text_as_python_sent_them()
    {
        Assert.Equal(QueueMessageEncoding.None, new QueueClientOptions().MessageEncoding); // the .NET default
        Assert.Equal(QueueMessageEncoding.None, StorageQueueProvisioningSink.ClientOptions().MessageEncoding);
    }

    [Fact]
    public async Task The_queue_receives_the_message_text_unencoded()
    {
        // A loopback stand-in for Queue storage that records the request body.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Cancel);
            await using var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, Cancel);
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var all = text.ToString();
                var split = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (split >= 0)
                {
                    var length = int.Parse(all.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (Encoding.UTF8.GetByteCount(all[(split + 4)..]) >= length)
                    {
                        const string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><QueueMessagesList><QueueMessage><MessageId>m</MessageId>"
                            + "<InsertionTime>Sat, 07 Nov 2026 09:30:00 GMT</InsertionTime><ExpirationTime>Sat, 14 Nov 2026 09:30:00 GMT</ExpirationTime>"
                            + "<PopReceipt>p</PopReceipt><TimeNextVisible>Sat, 07 Nov 2026 09:30:00 GMT</TimeNextVisible></QueueMessage></QueueMessagesList>";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 201 Created\r\nContent-Type: application/xml\r\nContent-Length: "
                            + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body), Cancel);
                        return all[(split + 4)..];
                    }
                }
            }
        }, Cancel);

        var queue = new QueueClient(new Uri("http://127.0.0.1:" + port + "/devstoreaccount1/margo-provision"), StorageQueueProvisioningSink.ClientOptions());
        var message = new Margo.Cloud.Core.ProvisioningMessage("ensure", "agent-identity-1", "margo-user-1", "first-activity").ToJson();
        await new StorageQueueProvisioningSink(queue).SendAsync(message, Cancel);
        var xml = XDocument.Parse((await received).TrimStart('\ufeff'));
        Assert.Equal(message, xml.Root!.Element("MessageText")!.Value);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
}

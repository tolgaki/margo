using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Margo.Cloud.Engine;
using Margo.Cloud.Host.Authentication;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.WorkIQ;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// The real host (<c>Program</c>) under <see cref="WebApplicationFactory{TEntryPoint}"/>, configured with the variables the
/// instance template and the Dockerfile set, with fakes only at the edges: the token keys, the model, Work IQ, the Blob
/// store, the SDK's MSAL connection and the channel's connector service.
/// </summary>
internal sealed class HostFactory : WebApplicationFactory<Program>
{
    private readonly Channel<(string Kind, string? Status)> _finished = System.Threading.Channels.Channel.CreateUnbounded<(string, string?)>();

    public HostFactory(TempState state, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Environment = Defaults(state);
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            Environment[key] = value;
        }
    }

    /// <summary>The environment variables, by their exact names (<c>A__B</c> is configuration key <c>A:B</c>).</summary>
    public Dictionary<string, string?> Environment { get; }

    public FakeJwksSource Keys { get; } = new();

    public FakeModel Model { get; } = new();

    public FakeWorkIQ WorkIQ { get; } = new();

    public InMemoryLeaseStore Store { get; } = new();

    public List<string> Containers { get; } = [];

    public FakeConnection Connection { get; } = new();

    public FakeChannel Connector { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public static Dictionary<string, string?> Defaults(TempState state) => new(StringComparer.Ordinal)
    {
        // cloud/infra/instance.bicep
        ["MARGO_HOSTING"] = "instance",
        ["MARGO_INSTANCE_KEY"] = "instance-key-1",
        ["MARGO_INSTANCE_AGENT_ID"] = Activities.AgentIdentity,
        ["MARGO_INSTANCE_USER_ID"] = Activities.AgentUser,
        ["MARGO_STATE_BLOB_URL"] = "https://state.example.com/",
        ["MARGO_STATE_CONTAINER"] = "margo-state-instance-1",
        ["MARGO_PUBLIC_BASE_URL"] = "https://margo.example.com",
        ["MARGO_TENANT_ID"] = Tokens.TenantId,
        ["AZURE_CLIENT_ID"] = "fictional-instance-identity",
        ["AZURE_OPENAI_ENDPOINT"] = "https://fictional-ai.openai.azure.com",
        ["MARGO_MODEL_DEPLOYMENT"] = "fictional-deployment",
        ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__AUTHTYPE"] = "FederatedCredentials",
        ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID"] = Tokens.ClientId,
        ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID"] = Tokens.TenantId,
        ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__FEDERATEDCLIENTID"] = "fictional-credential-identity",
        // cloud/Dockerfile
        ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__SCOPES__0"] = "api://fictional-agents-service/.default",
        ["CONNECTIONSMAP__0__SERVICEURL"] = "*",
        ["CONNECTIONSMAP__0__CONNECTION"] = "SERVICE_CONNECTION",
        ["AGENTAPPLICATION__STARTTYPINGTIMER"] = "false",
        ["AGENTAPPLICATION__USERAUTHORIZATION__AUTOSIGNIN"] = "false",
        ["AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__TYPE"] = "AgenticUserAuthorization",
        ["AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__SCOPES"] = "api://fictional-agents-service/.default",
        ["AUTH_HANDLER_NAME"] = "AGENTIC",
        ["MARGO_REPO_ROOT"] = Repository.Root,
        // The test's private state.
        ["MARGO_STATE_DIR"] = state.Combine("state"),
        ["MARGO_PRINCIPAL_ACCESS"] = "delegation",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // the SDK validates its configuration at start-up in Development
        foreach (var (key, value) in Environment)
        {
            builder.UseSetting(key.Replace("__", ":", StringComparison.Ordinal), value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IJwksSource>();
            services.AddSingleton<IJwksSource>(Keys);
            services.RemoveAll<IModelClient>();
            services.AddSingleton<IModelClient>(Model);
            services.RemoveAll<IWorkIQTransport>();
            services.AddSingleton<IWorkIQTransport>(WorkIQ);
            services.RemoveAll<LeaseStoreFactory>();
            services.AddSingleton(new LeaseStoreFactory(container =>
            {
                lock (Containers)
                {
                    Containers.Add(container);
                }

                return Store;
            }));
            services.RemoveAll<IConnections>();
            services.AddSingleton<IConnections>(new ConfigurationConnections(
                new Dictionary<string, IAccessTokenProvider> { ["SERVICE_CONNECTION"] = Connection },
                new List<ConnectionMapItem>
                { new ConnectionMapItem { ServiceUrl = "*", Connection = "SERVICE_CONNECTION" } }));
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(Connector);
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }

    /// <summary>Builds the host and hooks turn completion.</summary>
    public HttpClient Start()
    {
        var client = CreateClient();
        var turns = Services.GetRequiredService<MargoTurns>();
        turns.Finished = (kind, status) => _finished.Writer.TryWrite((kind, status));
        return client;
    }

    /// <summary>Waits for the next handled route (the SDK runs turns in the background after answering 202).</summary>
    public async Task<(string Kind, string? Status)> NextTurnAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await _finished.Reader.ReadAsync(timeout.Token);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject activity, string? token = null, string path = "/api/messages")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Tokens.Valid());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

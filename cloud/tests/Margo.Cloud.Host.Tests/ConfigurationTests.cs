using Margo.Cloud.Orchestrator;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Authentication.Msal.Model;
using Microsoft.Agents.Builder.UserAuth.AgenticAuth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// Configuration from the same environment variables the Python host, <c>autopilot/infra/instance.bicep</c> and the
/// Dockerfile use, and the start-up refusals.
/// </summary>
public sealed class ConfigurationTests
{
    private const string ClientIdKey = "CONNECTIONS:SERVICE_CONNECTION:SETTINGS:CLIENTID";
    private const string TenantIdKey = "CONNECTIONS:SERVICE_CONNECTION:SETTINGS:TENANTID";

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> environment) =>
        new ConfigurationBuilder().AddInMemoryCollection(environment.Select(pair => new KeyValuePair<string, string?>(pair.Key.Replace("__", ":", StringComparison.Ordinal), pair.Value))).Build();

    private static Dictionary<string, string?> Complete(TempState state) => HostFactory.Defaults(state);

    [Fact]
    public void The_host_refuses_to_start_without_the_blueprint_identity()
    {
        using var state = new TempState();
        foreach (var missing in new[] { "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID", "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID" })
        {
            foreach (var value in new string?[] { null, string.Empty })
            {
                var environment = Complete(state);
                environment[missing] = value;
                var error = Assert.Throws<HostStartupException>(() => HostSettings.Load(Configuration(environment)));
                Assert.Equal("the agent blueprint client ID and tenant are required to check activity tokens", error.Message);
            }
        }
    }

    [Fact]
    public void The_program_exits_before_serving_without_the_blueprint()
    {
        using var state = new TempState();
        var host = new HostFactory(state, new Dictionary<string, string?> { ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID"] = string.Empty });
        Assert.ThrowsAny<Exception>(() => host.CreateClient());
        host.Dispose();
    }

    [Fact]
    public void Requirements_are_checked_in_python_s_order()
    {
        using var state = new TempState();
        var environment = Complete(state);
        string Fails(params string[] remove)
        {
            var values = new Dictionary<string, string?>(environment);
            foreach (var key in remove)
            {
                values.Remove(key);
            }

            return Assert.Throws<HostStartupException>(() => HostSettings.Load(Configuration(values))).Message;
        }

        const string Blueprint = "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID";
        Assert.Equal("MARGO_MODEL_DEPLOYMENT is required", Fails("MARGO_MODEL_DEPLOYMENT", "MARGO_STATE_BLOB_URL", Blueprint));
        Assert.Equal("MARGO_STATE_BLOB_URL is required: state must not live only in one session", Fails("MARGO_STATE_BLOB_URL", "AZURE_OPENAI_ENDPOINT", Blueprint));
        Assert.Equal("AZURE_OPENAI_ENDPOINT is required (https://ACCOUNT.openai.azure.com)", Fails("AZURE_OPENAI_ENDPOINT", "MARGO_INSTANCE_AGENT_ID", Blueprint));
        Assert.Equal("MARGO_INSTANCE_AGENT_ID is required", Fails("MARGO_INSTANCE_AGENT_ID", Blueprint));
        Assert.Equal("MARGO_INSTANCE_USER_ID is required", Fails("MARGO_INSTANCE_USER_ID"));
        Assert.Equal("MARGO_STATE_CONTAINER is required", Fails("MARGO_STATE_CONTAINER"));
        // Voice is configured only with a model; then its link needs the instance key and voice needs the tenant.
        environment["VOICE_LIVE_MODEL"] = "fictional-voice-model";
        Assert.Equal("MARGO_INSTANCE_KEY is required", Fails("MARGO_INSTANCE_KEY", Blueprint));
        Assert.Equal("MARGO_TENANT_ID is required", Fails("MARGO_TENANT_ID", Blueprint));
    }

    [Fact]
    public void Settings_follow_the_python_defaults_and_the_instance_template()
    {
        using var state = new TempState();
        var environment = Complete(state);
        var settings = HostSettings.Load(Configuration(environment));
        Assert.True(settings.IsInstance);
        Assert.Equal(new AgentInstance(Activities.AgentIdentity, Activities.AgentUser), settings.Instance);
        Assert.Equal("margo-state-instance-1", settings.StateContainer);
        Assert.Null(settings.VoiceUrl); // no VOICE_LIVE_MODEL
        Assert.False(settings.VoiceEnabled);
        Assert.Equal("AGENTIC", settings.AgenticHandler);
        Assert.Equal(string.Empty, settings.PrincipalHandler);
        Assert.Equal("fictional-instance-identity", settings.ManagedIdentityClientId);
        Assert.Equal(8088, settings.Port);
        Assert.Equal("0.0.0.0", settings.ListenHost);
        Assert.Equal("America/Los_Angeles", settings.TimeZone);
        Assert.Equal(LogLevel.Information, settings.LogLevel);
        Assert.Equal("Voice.Talk", settings.VoiceScope);

        environment["VOICE_LIVE_MODEL"] = "fictional-voice-model";
        environment["MARGO_PUBLIC_BASE_URL"] = "https://margo.example.com///";
        var voice = HostSettings.Load(Configuration(environment));
        Assert.True(voice.VoiceEnabled);
        Assert.Equal("https://margo.example.com/i/instance-key-1/voice/", voice.VoiceUrl);
        Assert.Equal(Tokens.TenantId, voice.VoiceTenantId);

        // A single process (no MARGO_HOSTING) serves whichever instance an activity addresses, without voice.
        environment.Remove("MARGO_HOSTING");
        environment.Remove("MARGO_INSTANCE_AGENT_ID");
        var single = HostSettings.Load(Configuration(environment));
        Assert.False(single.IsInstance);
        Assert.Null(single.Instance);
        Assert.False(single.VoiceEnabled);
        Assert.Null(single.VoiceUrl);
        Assert.Equal(LogLevel.Debug, HostSettings.ParseLogLevel("debug"));
        Assert.Equal(LogLevel.Information, HostSettings.ParseLogLevel("chatty"));
    }

    [Fact]
    public void The_state_root_and_its_parent_are_private()
    {
        using var state = new TempState();
        var root = state.Combine("deep", "state");
        HostSettings.EnsureStateRoot(root);
        var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(owner, File.GetUnixFileMode(root));
        Assert.Equal(owner, File.GetUnixFileMode(state.Combine("deep")));
    }

    /// <summary>
    /// The .NET Agents SDK reads the exact variables the infrastructure sets, through the real environment-variable
    /// provider (each variable here carries a private prefix, so the test touches nothing else in the process).
    /// </summary>
    [Fact]
    public void The_sdk_reads_the_infrastructure_s_variables()
    {
        using var state = new TempState();
        var prefix = "MARGOTEST_" + Guid.NewGuid().ToString("N") + "_";
        var environment = Complete(state);
        try
        {
            foreach (var (key, value) in environment)
            {
                Environment.SetEnvironmentVariable(prefix + key, value);
            }

            var raw = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            Assert.Equal(Tokens.ClientId, raw["Connections:Service_Connection:Settings:ClientId"]); // keys ignore case

            // The connection: MSAL with the blueprint's federated credential.
            var settings = new ConnectionSettings(raw.GetSection("Connections:SERVICE_CONNECTION:Settings"));
            Assert.Equal(AuthTypes.FederatedCredentials, settings.AuthType);
            Assert.Equal(Tokens.ClientId, settings.ClientId);
            Assert.Equal(Tokens.TenantId, settings.TenantId);
            Assert.Equal("fictional-credential-identity", settings.FederatedClientId);
            Assert.Equal(["api://fictional-agents-service/.default"], settings.Scopes);

            var services = new ServiceCollection().AddLogging();
            services.AddDefaultMsalAuth(raw);
            using var provider = services.BuildServiceProvider();
            var connections = new ConfigurationConnections(provider, raw);
            connections.ValidateConfiguration(); // CONNECTIONSMAP__0__CONNECTION names SERVICE_CONNECTION
            var connection = connections.GetConnection("SERVICE_CONNECTION");
            Assert.IsType<MsalAuth>(connection);
            Assert.IsAssignableFrom<IAgenticTokenProvider>(connection);
            Assert.IsAssignableFrom<IOBOExchange>(connection);
            Assert.Equal(Tokens.ClientId, connection.ConnectionSettings.ClientId);

            // The agentic handler: its type and settings; AUTOSIGNIN and STARTTYPINGTIMER as booleans.
            Assert.Equal("AgenticUserAuthorization", raw["AgentApplication:UserAuthorization:Handlers:AGENTIC:Type"]);
            Assert.False(raw.GetValue<bool>("AgentApplication:UserAuthorization:AutoSignIn"));
            Assert.False(raw.GetValue<bool>("AgentApplication:StartTypingTimer"));
            Assert.Equal(["AGENTIC"], raw.GetSection("AgentApplication:UserAuthorization:Handlers").GetChildren().Select(child => child.Key));

            // The one unavoidable difference: SCOPES given as one string does not bind to the SDK's list ...
            var handler = raw.GetSection("AgentApplication:UserAuthorization:Handlers:AGENTIC:Settings");
            Assert.Null(handler.Get<AgenticAuthSettings>()?.Scopes);
            // ... so the host offers it as SCOPES:0 too, without changing the variable.
            var adapted = new ConfigurationBuilder().AddConfiguration(raw).AddInMemoryCollection(MargoHost.SdkConfigurationAdaptations(raw)).Build();
            Assert.Equal(["api://fictional-agents-service/.default"],
                adapted.GetSection("AgentApplication:UserAuthorization:Handlers:AGENTIC:Settings").Get<AgenticAuthSettings>()!.Scopes);
            Assert.Equal("false", adapted["AgentApplication:NormalizeMentions"]);

            // The host's own settings from the same provider.
            var host = HostSettings.Load(raw);
            Assert.Equal(Tokens.ClientId, host.ClientId);
            Assert.Equal(Tokens.TenantId, host.TenantId);
        }
        finally
        {
            foreach (var key in environment.Keys)
            {
                Environment.SetEnvironmentVariable(prefix + key, null);
            }
        }
    }

    [Fact]
    public void A_list_of_scopes_needs_no_adaptation_and_explicit_mention_settings_win()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__SCOPES__0"] = "scope-a",
            ["AGENTAPPLICATION__NORMALIZEMENTIONS"] = "true",
        });
        Assert.Empty(MargoHost.SdkConfigurationAdaptations(configuration));
    }
}

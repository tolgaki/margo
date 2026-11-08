using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Margo.Cloud.Core;
using Margo.Cloud.Engine;
using Margo.Cloud.Guard;
using Margo.Cloud.Host.Authentication;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.Voice;
using Margo.Cloud.WorkIQ;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host;

/// <summary>The leased store for a Blob container name (the orchestrator's <c>store_for</c>), as a service.</summary>
public sealed record LeaseStoreFactory(Func<string, ILeaseStore> StoreFor);

/// <summary>
/// The Margo host (Python: <c>MargoHost</c> and <c>build_orchestrator</c> in <c>host/app.py</c>): the Agents SDK
/// activity endpoint, self-hosted for Agent 365, with the orchestrator, the Azure Blob state store and, per instance,
/// voice.
/// </summary>
/// <remarks>
/// Every external dependency is a service registered with <c>TryAdd</c>, so a test host can replace it: the token
/// keys (<see cref="IJwksSource"/>), the model (<see cref="IModelClient"/>), Work IQ (<see cref="IWorkIQTransport"/>),
/// the state store (<see cref="LeaseStoreFactory"/>), the instance's credential (<see cref="TokenCredential"/>) and the
/// SDK's connections and outbound HTTP.
/// </remarks>
public static class MargoHost
{
    /// <summary>The host's logger (Python: <c>margo.autopilot</c>).</summary>
    public const string LogCategory = "margo.autopilot";

    /// <summary>The voice logger (Python: <c>margo.voice</c>).</summary>
    public const string VoiceLogCategory = "margo.voice";

    /// <summary>The token check's logger (Python: the Agents SDK's own loggers).</summary>
    public const string AuthLogCategory = "margo.autopilot.auth";

    /// <summary>
    /// The largest activity body read (aiohttp's default <c>client_max_size</c>, 1 MiB, which the Python host kept): a
    /// larger one is refused before the SDK reads it.
    /// </summary>
    public const int MaxActivityBytes = 1024 * 1024;

    /// <summary>The variable that keeps GenAI message content out of telemetry (set to <c>false</c> unless already set).</summary>
    public const string ContentCaptureVariable = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    /// <summary>Configure logging, telemetry and services. Python: <c>MargoHost.__init__</c>.</summary>
    public static void Configure(WebApplicationBuilder builder, HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        if (Environment.GetEnvironmentVariable(ContentCaptureVariable) is null)
        {
            Environment.SetEnvironmentVariable(ContentCaptureVariable, "false"); // os.environ.setdefault
        }

        builder.Configuration.AddInMemoryCollection(SdkConfigurationAdaptations(builder.Configuration));
        ConfigureLogging(builder.Logging, settings.LogLevel);
        var telemetry = ConfigureTelemetry(builder, settings);
        AddServices(builder.Services, builder.Configuration, settings, telemetry);
    }

    /// <summary>
    /// What the .NET Agents SDK needs that the Python SDK read differently from the same variables (nothing else is
    /// changed; see PORTING.md, "Configuration"):
    /// <list type="bullet">
    /// <item>A user-authorization handler's <c>SETTINGS__SCOPES</c> given as one string (the Dockerfile's agentic
    /// handler) is also offered as the list item <c>SCOPES:0</c>: the .NET binder does not turn a string into a
    /// list.</item>
    /// <item><c>AgentApplication:NormalizeMentions</c> is <c>false</c> unless configured: Python only removed the
    /// recipient's mention from the text, which <c>RemoveRecipientMention</c> (on by default in both) does.</item>
    /// </list>
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> SdkConfigurationAdaptations(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var adaptations = new List<KeyValuePair<string, string?>>();
        foreach (var handler in configuration.GetSection("AgentApplication:UserAuthorization:Handlers").GetChildren())
        {
            var scopes = handler.GetSection("Settings:Scopes");
            if (!string.IsNullOrEmpty(scopes.Value) && !scopes.GetChildren().Any())
            {
                adaptations.Add(new(scopes.Path + ":0", scopes.Value));
            }
        }

        if (configuration["AgentApplication:NormalizeMentions"] is null)
        {
            adaptations.Add(new("AgentApplication:NormalizeMentions", "false"));
        }

        return adaptations;
    }

    /// <summary>
    /// Python's <c>logging.basicConfig</c> at <c>LOG_LEVEL</c>, with floors that keep request lines (the voice page's
    /// one-time sign-in code is in a query string) and SDK debug output (activity bodies) out of the log.
    /// </summary>
    public static void ConfigureLogging(ILoggingBuilder logging, LogLevel level)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss,fff ";
            options.UseUtcTimestamp = true;
        });
        logging.SetMinimumLevel(level);
        logging.AddFilter(category: null, level);
        var quiet = level > LogLevel.Warning ? level : LogLevel.Warning;
        logging.AddFilter("Microsoft.AspNetCore", quiet);
        logging.AddFilter("System.Net.Http", quiet);
        logging.AddFilter("Microsoft.Agents", quiet);
    }

    /// <summary>
    /// <c>_configure_telemetry()</c>: Application Insights when a connection string is set. Returns what to log once the
    /// host's logger exists.
    /// </summary>
    private static (LogLevel Level, string Message, Exception? Error) ConfigureTelemetry(WebApplicationBuilder builder, HostSettings settings)
    {
        if (settings.ApplicationInsights is null)
        {
            return (LogLevel.Warning, "Application Insights not configured; running without telemetry.", null);
        }

        try
        {
            // Python's configure_azure_monitor refused a bad connection string at once; the .NET exporter would only
            // parse it when the host starts, and stop the host. The message never repeats the connection string.
            if (TelemetryConnection.Problem(settings.ApplicationInsights) is { } problem)
            {
                throw new ArgumentException(problem);
            }

            builder.Services.AddOpenTelemetry().UseAzureMonitor(options => options.ConnectionString = settings.ApplicationInsights);
            return (LogLevel.Information, "Application Insights configured (message content capture is off).", null);
        }
        catch (Exception exc) when (exc is ArgumentException or InvalidOperationException or FormatException)
        {
            return (LogLevel.Error, "Telemetry configuration failed; continuing without it.", exc);
        }
    }

    private static void AddServices(IServiceCollection services, IConfiguration configuration, HostSettings settings, (LogLevel Level, string Message, Exception? Error) telemetry)
    {
        services.AddSingleton(settings);
        services.AddSingleton(new TelemetryOutcome(telemetry.Level, telemetry.Message, telemetry.Error));
        services.AddHttpClient();

        // The inbound token check.
        services.TryAddSingleton<IJwksSource, HttpJwksSource>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new JwksClientManager(provider.GetRequiredService<IJwksSource>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(provider => new AgentTokenValidator(new AgentAuthSettings(settings.ClientId, settings.TenantId),
            provider.GetRequiredService<JwksClientManager>(), provider.GetRequiredService<ILoggerFactory>().CreateLogger(AuthLogCategory),
            provider.GetRequiredService<TimeProvider>()));

        // _credential(): this instance's own managed identity, for its state and the model, nothing else.
        services.TryAddSingleton<TokenCredential>(_ => settings.ManagedIdentityClientId is { } clientId
            ? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId))
            : new DefaultAzureCredential());
        services.TryAddSingleton<IModelClient>(provider => new AzureOpenAIModelClient(AzureOpenAIModelClient.CreateHttpClient(), settings.ModelEndpoint,
            provider.GetRequiredService<TokenCredential>()));
        services.TryAddSingleton<IWorkIQTransport>(_ => new McpHttpTransport());
        services.TryAddSingleton(provider => new LeaseStoreFactory(
            AzureBlobLeaseStore.Factory(new Uri(settings.StateBlobUrl), provider.GetRequiredService<TokenCredential>())));
        services.TryAddSingleton(provider => BuildOrchestrator(provider, settings));

        // The Agents SDK: MSAL connections (MsalConnectionManager), user authorization and the adapter from
        // configuration; one MemoryStorage.
        services.AddDefaultMsalAuth(configuration);
        services.TryAddSingleton<IStorage, MemoryStorage>();
        services.AddAgentApplicationOptions();
        services.AddAgent<MargoAgent, MargoCloudAdapter>();
        services.TryAddSingleton<MargoTurns>();

        if (settings.VoiceEnabled)
        {
            services.TryAddSingleton<IVoiceTokens>(provider => new SdkVoiceTokens(provider.GetRequiredService<Microsoft.Agents.Authentication.IConnections>()));
            services.TryAddSingleton(provider => new VoiceServiceOptions
            {
                Scope = settings.VoiceScope,
                Settings = VoiceSettings.FromEnv(VoiceEnvironment(provider.GetRequiredService<IConfiguration>())),
                Endpoint = provider.GetRequiredService<IConfiguration>()["VOICE_LIVE_ENDPOINT"],
                Logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(VoiceLogCategory),
            });
            services.TryAddSingleton(provider => new VoiceService(provider.GetRequiredService<Margo.Cloud.Orchestrator.Orchestrator>(),
                provider.GetRequiredService<IVoiceTokens>(), settings.Instance!, settings.VoiceTenantId!, settings.ClientId,
                provider.GetRequiredService<VoiceServiceOptions>()));
        }
    }

    /// <summary>The voice variables, read from configuration (the same environment variables).</summary>
    private static Dictionary<string, string> VoiceEnvironment(IConfiguration configuration)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "VOICE_LIVE_MODEL", "MARGO_VOICE_NAME", "MARGO_VOICE_ENDPOINT_ID", "MARGO_VOICE_MAX_SECONDS", "MARGO_VOICE_MAX_TOOL_CALLS" })
        {
            if (configuration[name] is { } value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary><c>build_orchestrator()</c>.</summary>
    private static Margo.Cloud.Orchestrator.Orchestrator BuildOrchestrator(IServiceProvider provider, HostSettings settings)
    {
        var loggers = provider.GetRequiredService<ILoggerFactory>();
        var logger = loggers.CreateLogger(LogCategory);
        var engine = new Margo.Cloud.Engine.Engine(provider.GetRequiredService<IModelClient>(), settings.ModelDeployment, settings.RepoRoot,
                                                   timeZone: settings.TimeZone);
        void Receipt(System.Text.Json.Nodes.JsonObject receipt)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("workiq {Receipt}", PythonJson.Dumps(receipt, sortKeys: true));
            }
        }

        return new Margo.Cloud.Orchestrator.Orchestrator(engine, provider.GetRequiredService<IWorkIQTransport>(),
            HostSettings.EnsureStateRoot(settings.StateRoot), provider.GetRequiredService<LeaseStoreFactory>().StoreFor,
            new OrchestratorOptions
            {
                PrincipalAccess = settings.PrincipalAccess,
                GuardRecorder = receipt => Receipt(receipt.ToJsonObject()),
                StateRecorder = receipt => Receipt(receipt.ToJsonObject()),
                Instance = settings.Instance,
                StateContainer = settings.StateContainer,
                VoiceUrl = settings.VoiceUrl,
                Logger = logger,
            });
    }

    /// <summary>
    /// Build the pipeline (Python: <c>web_app()</c>): the token check on everything but health and voice, the two activity
    /// paths, the health paths and, when enabled, voice. Constructs the orchestrator, the SDK's connections and options
    /// and voice now, so a misconfiguration stops start-up as it did in Python.
    /// </summary>
    public static WebApplication Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var services = app.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
        var telemetry = services.GetRequiredService<TelemetryOutcome>();
        if (telemetry.Error is null)
        {
#pragma warning disable CA2254 // a fixed message chosen above
            logger.Log(telemetry.Level, telemetry.Message);
        }
        else
        {
            logger.Log(telemetry.Level, telemetry.Error, telemetry.Message);
#pragma warning restore CA2254
        }

        services.GetRequiredService<Margo.Cloud.Orchestrator.Orchestrator>();
        services.GetRequiredService<Microsoft.Agents.Authentication.IConnections>();
        services.GetRequiredService<AgentApplicationOptions>();
        var settings = services.GetRequiredService<HostSettings>();
        var voice = settings.VoiceEnabled ? services.GetRequiredService<VoiceService>() : null;

        app.UseMiddleware<InboundAuthentication>();
        foreach (var path in HostSettings.ActivityPaths)
        {
            app.MapPost(path, async (HttpRequest request, HttpResponse response, IAgentHttpAdapter adapter, IAgent agent, CancellationToken cancellationToken) =>
            {
                if (!await BufferActivityAsync(request, cancellationToken))
                {
                    // Python: aiohttp's client_max_size made request.json() raise; the SDK answered this.
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    response.ContentType = "application/json; charset=utf-8";
                    await response.WriteAsync("{\"error\": \"Invalid JSON or unsupported Content-Type\"}", cancellationToken);
                    return;
                }

                await adapter.ProcessAsync(request, response, agent, cancellationToken);
            });
        }

        foreach (var path in HostSettings.HealthPaths)
        {
            app.MapMethods(path, [HttpMethods.Get, HttpMethods.Head], () => Results.Text("{\"status\": \"ok\"}", "application/json; charset=utf-8"));
        }

        if (voice is not null)
        {
            app.UseWebSockets();
            VoiceEndpoints.Map(app, voice);
        }

        return app;
    }

    /// <summary>
    /// Read the activity body into memory, at most <see cref="MaxActivityBytes"/> (aiohttp: <c>body_size &gt;
    /// client_max_size</c> raised). False when it is larger, including a declared length beyond Kestrel's own limit.
    /// </summary>
    private static async Task<bool> BufferActivityAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxActivityBytes)
        {
            return false;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxActivityBytes)
                {
                    await buffer.DisposeAsync();
                    return false;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exc) when (exc.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await buffer.DisposeAsync();
            return false;
        }

        buffer.Position = 0;
        request.Body = buffer;
        request.HttpContext.Response.RegisterForDisposeAsync(buffer);
        return true;
    }
}

/// <summary>What telemetry configuration reported, logged once the host's logger exists.</summary>
public sealed record TelemetryOutcome(LogLevel Level, string Message, Exception? Error);

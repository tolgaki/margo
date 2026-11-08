using AgentNotification;
using Margo.Cloud.Orchestrator;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host;

/// <summary>
/// The Agents SDK application (Python: <c>MargoHost._register</c>): Teams messages and mail become orchestrator events,
/// Agent 365 mail notifications are queued as requests, lifecycle notifications and installation updates are only
/// logged.
/// </summary>
/// <remarks>
/// Route order follows Python, where the message route was registered first and so took mail that arrives as a
/// <c>message</c> on <c>agents:email</c>. The .NET SDK sorts agentic routes (the notification extension's) before the
/// others, so the message route is registered twice: once agentic, ahead of the notification routes, and once for
/// other requests.
/// </remarks>
public sealed class MargoAgent : AgentApplication
{
    public MargoAgent(AgentApplicationOptions options, MargoTurns turns)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(turns);
        string[] handlers = [turns.Settings.AgenticHandler];
        OnActivity(ActivityTypes.Message, (context, state, token) => turns.OnMessageAsync(this, context, token),
                   autoSignInHandlers: handlers, isAgenticOnly: true);
        // Only mail notifications become requests to the office. Lifecycle events and document comments are not
        // messages to Margo and are never queued.
        // The selector of the notification extension's OnAgenticEmailNotification (channel "agents", sub-channel "email",
        // agentic requests), but the handler reads the Outlook reference itself: the extension's wrapper throws on one it
        // cannot read, failing the turn (and the SDK's error handler would answer the mail), where Python's dropped it.
        AddRoute((context, token) => Task.FromResult(IsMailNotification(context.Activity)),
                 (context, state, token) => turns.OnMailNotificationAsync(this, context, ActivityEvents.EmailReference(context.Activity), token),
                 isInvokeRoute: false, autoSignInHandlers: handlers, isAgenticOnly: true);
        // Deployed, the router turns lifecycle events into provisioning and never forwards them; a single process
        // only records them.
        this.OnLifecycleNotification((context, state, notification, token) => turns.OnLifecycleAsync(context));
        OnActivity(ActivityTypes.Message, (context, state, token) => turns.OnMessageAsync(this, context, token), autoSignInHandlers: handlers);
        OnActivity(ActivityTypes.InstallationUpdate, (context, state, token) => turns.OnInstallationAsync(context));
    }

    /// <summary>
    /// The notification extension's email selector: channel <c>agents</c> and sub-channel <c>email</c>, ignoring case,
    /// any activity type (Python's <c>on_email</c> selector likewise).
    /// </summary>
    public static bool IsMailNotification(IActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var channel = activity.ChannelId;
        return channel?.Channel is { } name && name.Equals("agents", StringComparison.OrdinalIgnoreCase)
               && channel.SubChannel is { } sub && sub.Equals("email", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The route handlers' work, shared by every <see cref="MargoAgent"/> (the SDK makes one per request).</summary>
public sealed class MargoTurns
{
    /// <summary>The typing indicator's period (Python: <c>asyncio.sleep(4)</c>).</summary>
    public static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);

    private readonly Margo.Cloud.Orchestrator.Orchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly HashSet<string> _configuredHandlers;

    public MargoTurns(Margo.Cloud.Orchestrator.Orchestrator orchestrator, HostSettings settings, IConfiguration configuration, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggers);
        _orchestrator = orchestrator;
        Settings = settings;
        _logger = loggers.CreateLogger(MargoHost.LogCategory);
        _configuredHandlers = new HashSet<string>(
            configuration.GetSection("AgentApplication:UserAuthorization:Handlers").GetChildren().Select(child => child.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The host's settings.</summary>
    public HostSettings Settings { get; }

    /// <summary>Tests only: called when a turn's orchestrator call has finished (or failed), with its status.</summary>
    internal Action<string, string?>? Finished { get; set; }

    /// <summary><c>on_message</c>.</summary>
    public async Task OnMessageAsync(AgentApplication app, ITurnContext context, CancellationToken cancellationToken)
    {
        var inbound = ActivityEvents.Message(context.Activity, out var email);
        if (inbound is null)
        {
            _logger.LogWarning("message without agent identity or conversation; ignored");
            Finished?.Invoke("message", null);
            return;
        }

        using var typing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var typingTask = TypingAsync(context, typing.Token);
        TurnReply result;
        try
        {
            result = await _orchestrator.HandleAsync(inbound, TokenGetter(app, context), cancellationToken);
        }
        catch (Exception exc) when (exc is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // HandleAsync reports its own failures; this is a last resort.
            _logger.LogError(exc, "turn failed");
            result = new TurnReply(null, TurnStatuses.Error);
        }
        finally
        {
            await typing.CancelAsync();
            await typingTask;
        }

        Info("turn status={Status}", result.Status);
        try
        {
            if (!string.IsNullOrEmpty(result.Reply) && !email) // this version never answers mail
            {
                var reply = new Activity { Type = ActivityTypes.Message, Text = result.Reply, TextFormat = TextFormatTypes.Markdown };
                await context.SendActivityAsync(reply, cancellationToken);
            }
        }
        finally
        {
            Finished?.Invoke("message", result.Status);
        }
    }

    /// <summary><c>on_mail_notification</c>: queued as a request; never answered.</summary>
    public async Task OnMailNotificationAsync(AgentApplication app, ITurnContext context, EmailReference? mail, CancellationToken cancellationToken)
    {
        var inbound = ActivityEvents.MailNotification(context.Activity, mail);
        if (inbound is null)
        {
            Finished?.Invoke("mail", null);
            return;
        }

        string? status = null;
        try
        {
            var result = await _orchestrator.HandleAsync(inbound, TokenGetter(app, context), cancellationToken);
            status = result.Status;
            Info("notification status={Status}", status);
        }
        catch (Exception exc) when (exc is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exc, "notification failed");
        }

        Finished?.Invoke("mail", status);
        // No reply: this version never answers mail.
    }

    /// <summary><c>on_lifecycle</c>: only logged.</summary>
    public Task OnLifecycleAsync(ITurnContext context)
    {
        Info("lifecycle notification: {ValueType}", PythonStr(context.Activity.ValueType));
        Finished?.Invoke("lifecycle", null);
        return Task.CompletedTask;
    }

    /// <summary><c>on_installation</c>: only logged.</summary>
    public Task OnInstallationAsync(ITurnContext context)
    {
        Info("installation update: {Action}", PythonStr(context.Activity.Action));
        Finished?.Invoke("installation", null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>_token_getter(context)</c>: Margo's Work IQ token through the agentic handler, the manager's through the
    /// configured on-behalf-of handler. A failure is logged (never the token) and counts as none.
    /// </summary>
    public Func<string, Task<string?>> TokenGetter(AgentApplication app, ITurnContext context)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(context);
        return async kind =>
        {
            var handler = kind == "margo" ? Settings.AgenticHandler : Settings.PrincipalHandler;
            if (string.IsNullOrEmpty(handler))
            {
                return null;
            }

            try
            {
                if (!_configuredHandlers.Contains(handler))
                {
                    // Python: Authorization.exchange_token raised ValueError for an unknown handler.
                    throw new InvalidOperationException("Auth handler " + handler + " not recognized or not configured.");
                }

                var token = await app.UserAuthorization.ExchangeTurnTokenAsync(context, handler, exchangeScopes: [HostSettings.WorkIQScope]);
                return string.IsNullOrEmpty(token) ? null : token;
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                _logger.LogWarning("token exchange failed for {Kind}", kind);
                return null;
            }
        };
    }

    /// <summary>
    /// <c>_typing</c>: a typing indicator every four seconds until the turn ends. Failures end it quietly.
    /// </summary>
    private static async Task TypingAsync(ITurnContext context, CancellationToken cancellationToken)
    {
        await Task.Yield(); // asyncio.ensure_future: the turn starts first
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await context.SendActivityAsync(new Activity { Type = ActivityTypes.Typing }, cancellationToken);
                await Task.Delay(TypingInterval, cancellationToken);
            }
        }
        catch (Exception)
        {
            // cancelled, or the channel refused: nothing to do
        }
    }

    private void Info(string template, string value)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
#pragma warning disable CA2254 // the templates are the fixed messages above
            _logger.LogInformation(template, value);
#pragma warning restore CA2254
        }
    }

    /// <summary>Python's <c>%s</c> of an optional string.</summary>
    private static string PythonStr(string? value) => value ?? "None";
}

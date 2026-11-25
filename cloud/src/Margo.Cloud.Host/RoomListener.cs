using Margo.Cloud.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host;

/// <summary>
/// Margo in the room: every <see cref="HostSettings.ListenMinutes"/> minutes, reads what was said in the rooms she is a
/// member of since the last pass and lets her tell her manager what matters (<see cref="Orchestrator.Orchestrator.ListenAsync"/>).
/// Runs only in per-instance hosting with the backchannel on; it needs the instance kept running
/// (<c>instanceMinReplicas</c> 1), since a container scaled to zero runs no timer.
/// </summary>
public sealed class RoomListener(Margo.Cloud.Orchestrator.Orchestrator orchestrator, IVoiceTokens tokens, HostSettings settings,
                                 ILoggerFactory loggers, TimeProvider time) : BackgroundService
{
    /// <summary>The longest a pass looks back, after passes that failed.</summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromHours(6);

    private readonly ILogger _logger = loggers.CreateLogger(MargoHost.LogCategory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.Instance is not { } instance || settings.ListenMinutes <= 0 || !settings.Backchannel)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(settings.ListenMinutes);
        var since = time.GetUtcNow() - interval;
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var started = time.GetUtcNow();
            if (started - since > MaxLookback)
            {
                since = started - MaxLookback;
            }

            Task<string?> Token(string kind) => kind == "margo"
                ? tokens.GetAgenticUserTokenAsync(settings.TenantId, instance.AgentIdentityId, instance.AgentUserId, [HostSettings.WorkIQScope], stoppingToken)
                : Task.FromResult<string?>(null);
            var pass = await orchestrator.ListenAsync(Token, since, stoppingToken).ConfigureAwait(false);
            if (pass.Status != "failed")
            {
                since = started; // a failed pass is read again next time
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("listen status={Status} rooms={Rooms} messages={Messages} notes={Notes}", pass.Status, pass.Rooms, pass.Messages, pass.Notes);
            }
        }
    }
}

using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Hosting.AspNetCore.BackgroundQueue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Host;

/// <summary>
/// The SDK's <see cref="CloudAdapter"/> with a logger that never writes activity bodies: the adapter logs the whole
/// activity at Debug (accepted, responses) and an invalid one at Error. Everything else it logs passes through.
/// </summary>
public sealed class MargoCloudAdapter(IChannelServiceClientFactory clients, IActivityTaskQueue queue, ILoggerFactory loggers,
                                      IConfiguration configuration, IOutboundHostValidator hostValidator)
    : CloudAdapter(clients, queue, new BodyFreeLogger<CloudAdapter>(loggers.CreateLogger<CloudAdapter>()), null, null, configuration, hostValidator);

/// <summary>Drops the SDK log events that carry an activity or response body (CloudAdapter event IDs 2, 3, 4, 10).</summary>
public sealed class BodyFreeLogger<T>(ILogger inner) : ILogger<T>
{
    /// <summary>The CloudAdapter events whose message holds a body.</summary>
    public static readonly IReadOnlySet<int> BodyEvents = new HashSet<int> { 2, 3, 4, 10 };

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (BodyEvents.Contains(eventId.Id))
        {
            return;
        }

        inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

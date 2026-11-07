using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>The connection limits Python's aiohttp session had.</summary>
public sealed class InstanceConnectorTests
{
    [Fact]
    public async Task At_most_one_hundred_connections_are_open_at_once()
    {
        using var connector = new InstanceConnector();
        var slots = new List<IDisposable>();
        for (var i = 0; i < InstanceConnector.ConnectionLimit; i++)
        {
            slots.Add(await connector.AcquireAsync(TestContext.Current.CancellationToken));
        }

        var waiting = connector.AcquireAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        slots[0].Dispose();
        slots[0].Dispose(); // releasing twice frees one slot only
        slots.Add(await waiting);
        Assert.False(connector.AcquireAsync(TestContext.Current.CancellationToken).IsCompleted);
        foreach (var slot in slots)
        {
            slot.Dispose();
        }
    }

    [Fact]
    public void Timeouts_and_heartbeats_are_python_s()
    {
        Assert.Equal((100, 10, 300, 20), (InstanceConnector.ConnectionLimit, InstanceConnector.ConnectTimeout.TotalSeconds,
            InstanceConnector.TotalTimeout.TotalSeconds, InstanceConnector.Heartbeat.TotalSeconds));
        Assert.Equal(256 * 1024, VoiceBridge.MaxFrameBytes);
        Assert.Equal((1024 * 1024, 30), (RouterCore.MaxActivityBytes, RouterCore.RetryAfterSeconds));
        Assert.Equal(15, RouteTableCache.Ttl.TotalSeconds);
    }
}

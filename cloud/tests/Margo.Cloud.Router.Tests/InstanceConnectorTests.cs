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
    public void Timeouts_and_heartbeats_are_python_s_except_the_total_and_the_pools()
    {
        // Deliberate deviations (PORTING.md): 120 s in total, not 300; per-instance and voice pools beside the 100.
        Assert.Equal((100, 10, 120, 20), (InstanceConnector.ConnectionLimit, InstanceConnector.ConnectTimeout.TotalSeconds,
            InstanceConnector.TotalTimeout.TotalSeconds, InstanceConnector.Heartbeat.TotalSeconds));
        Assert.Equal((8, 24, 4), (InstanceConnector.PerInstanceLimit, InstanceConnector.VoiceLimit, InstanceConnector.PerInstanceVoiceLimit));
        Assert.Equal(5, RouteTableCache.FailureBackoff.TotalSeconds);
        Assert.Equal(256 * 1024, VoiceBridge.MaxFrameBytes);
        Assert.Equal((1024 * 1024, 30), (RouterCore.MaxActivityBytes, RouterCore.RetryAfterSeconds));
        Assert.Equal(15, RouteTableCache.Ttl.TotalSeconds);
    }

    [Fact]
    public async Task One_instance_cannot_hold_every_slot_and_voice_has_its_own_pool()
    {
        var cancel = TestContext.Current.CancellationToken;
        using var connector = new InstanceConnector(100, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
        var held = new List<IDisposable>();
        try
        {
            // An instance that never answers keeps its own eight slots busy...
            for (var i = 0; i < InstanceConnector.PerInstanceLimit; i++)
            {
                held.Add(await connector.AcquireAsync("stuck-instance", voice: false, cancel));
            }

            await Assert.ThrowsAsync<InstanceTimeoutException>(() => connector.AcquireAsync("stuck-instance", voice: false, cancel));

            // ...and every other instance still gets through.
            using (await connector.AcquireAsync("other-instance", voice: false, cancel))
            {
            }

            // Voice sockets, which need no token at the router, fill only the voice pool: four per instance, 24 in all.
            for (var i = 0; i < InstanceConnector.VoiceLimit; i++)
            {
                held.Add(await connector.AcquireAsync("voice-instance-" + (i / InstanceConnector.PerInstanceVoiceLimit), voice: true, cancel));
            }

            await Assert.ThrowsAsync<InstanceTimeoutException>(() => connector.AcquireAsync("voice-instance-0", voice: true, cancel));
            await Assert.ThrowsAsync<InstanceTimeoutException>(() => connector.AcquireAsync("one-more-instance", voice: true, cancel));
            using (await connector.AcquireAsync("other-instance", voice: false, cancel))
            {
            }

            // A released slot is usable again.
            held[0].Dispose();
            held[0].Dispose();
            held[0] = await connector.AcquireAsync("stuck-instance", voice: false, cancel);
        }
        finally
        {
            foreach (var slot in held)
            {
                slot.Dispose();
            }
        }
    }
}

using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Margo.Cloud.Router;

/// <summary>
/// Bridges the browser's voice WebSocket to the instance's, byte for byte. Python:
/// <c>Router._voice_socket</c>.
/// </summary>
/// <remarks>
/// The browser's socket is accepted first, then the instance's is opened (no headers or query string
/// are passed on: the instance authenticates the person). Text and binary messages are copied each way
/// until either side closes, fails or sends a message of <see cref="MaxFrameBytes"/> bytes or more; that
/// side is closed with 1009 and the other with 1000. A close from either side is answered with 1000,
/// whatever its code, and the other side is closed with 1000. Both sockets send a ping after 20
/// seconds and give up after 10 more without a pong. If the instance cannot be reached the browser's
/// socket is closed with 1000 and a warning is logged.
/// </remarks>
public sealed class VoiceBridge
{
    /// <summary>Python: <c>MAX_VOICE_FRAME</c>. aiohttp refuses a message whose size reaches it.</summary>
    public const int MaxFrameBytes = 256 * 1024;

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly InstanceConnector _instances;
    private readonly ILogger _logger;

    public VoiceBridge(InstanceConnector instances, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(logger);
        _instances = instances;
        _logger = logger;
    }

    private enum Outcome
    {
        PeerClosed,
        TooBig,
        Failed,
    }

    /// <summary>Accept the browser's socket and bridge it to <paramref name="url"/> (the instance's
    /// <c>http(s)://.../voice/...</c> address).</summary>
    public async Task RunAsync(HttpContext context, string url)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(url);
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = InstanceConnector.Heartbeat,
            KeepAliveTimeout = InstanceConnector.Heartbeat / 2,
        });
        try
        {
            using var slot = await _instances.AcquireAsync(CancellationToken.None);
            using var upstream = await _instances.ConnectWebSocketAsync(new Uri("ws" + url["http".Length..]), CancellationToken.None);
            await BridgeAsync(downstream, upstream);
        }
        catch (Exception)
        {
            // Python: except Exception; nothing about the failure or the session is logged.
            _logger.LogWarning("voice connection to the instance failed");
        }
        finally
        {
            await CloseAsync(downstream, WebSocketCloseStatus.NormalClosure);
        }
    }

    private static async Task BridgeAsync(WebSocket downstream, WebSocket upstream)
    {
        var toInstance = PumpAsync(downstream, upstream);
        var toBrowser = PumpAsync(upstream, downstream);
        var first = await Task.WhenAny(toInstance, toBrowser);
        var source = first == toInstance ? downstream : upstream;
        var other = first == toInstance ? toBrowser : toInstance;
        switch (await first)
        {
            case Outcome.TooBig:
                await CloseAsync(source, WebSocketCloseStatus.MessageTooBig);
                break;
            case Outcome.PeerClosed:
                await CloseAsync(source, WebSocketCloseStatus.NormalClosure);
                break;
        }

        // Python cancels both pumps, then closes the instance's socket and the browser's.
        await CloseOutputAsync(upstream, WebSocketCloseStatus.NormalClosure);
        await CloseOutputAsync(downstream, WebSocketCloseStatus.NormalClosure);
        if (await Task.WhenAny(other, Task.Delay(CloseTimeout)) != other)
        {
            upstream.Abort();
            downstream.Abort();
        }

        await other;
    }

    /// <summary>Copy whole messages from <paramref name="source"/> to <paramref name="sink"/>.</summary>
    private static async Task<Outcome> PumpAsync(WebSocket source, WebSocket sink)
    {
        var buffer = new byte[MaxFrameBytes];
        try
        {
            while (true)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length)
                    {
                        return Outcome.TooBig;
                    }

                    result = await source.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return Outcome.PeerClosed;
                    }

                    count += result.Count;
                }
                while (!result.EndOfMessage);

                if (count >= MaxFrameBytes)
                {
                    return Outcome.TooBig;
                }

                await sink.SendAsync(buffer.AsMemory(0, count), result.MessageType, endOfMessage: true, CancellationToken.None);
            }
        }
        catch (Exception exc) when (exc is WebSocketException or InvalidOperationException or OperationCanceledException or IOException)
        {
            return Outcome.Failed;
        }
    }

    /// <summary>Send a close frame and wait up to 10 seconds for the peer's (aiohttp's <c>close()</c>).
    /// Only for a socket nothing else is receiving on.</summary>
    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await socket.CloseAsync(status, null, timeout.Token);
        }
        catch (Exception exc) when (exc is WebSocketException or OperationCanceledException or InvalidOperationException or IOException)
        {
            socket.Abort();
        }
    }

    /// <summary>Send a close frame; the pump still reading this socket receives the peer's reply.</summary>
    private static async Task CloseOutputAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket.CloseOutputAsync(status, null, CancellationToken.None);
        }
        catch (Exception exc) when (exc is WebSocketException or InvalidOperationException or IOException)
        {
            socket.Abort();
        }
    }
}

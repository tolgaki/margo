using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Margo.Cloud.Voice;

/// <summary>What <see cref="MessageSocket.ReceiveAsync"/> returns (aiohttp's <c>WSMsgType</c>, reduced to what the
/// voice server tells apart).</summary>
internal enum SocketMessageKind
{
    /// <summary>A text message.</summary>
    Text,

    /// <summary>A binary message.</summary>
    Binary,

    /// <summary>The socket is closing or closed (aiohttp's CLOSE, CLOSING, CLOSED and ERROR).</summary>
    Closed,
}

/// <summary>One whole message.</summary>
internal sealed record SocketMessage(SocketMessageKind Kind, string? Text = null, byte[]? Data = null)
{
    internal static SocketMessage Closed { get; } = new(SocketMessageKind.Closed);
}

/// <summary>
/// A WebSocket used the way aiohttp's <c>WebSocketResponse</c> and <c>ClientWebSocketResponse</c> are used by the voice
/// server: whole messages, a size limit, one sender at a time, a <see cref="Closed"/> flag, and a graceful close.
/// </summary>
/// <remarks>
/// <para>A reader task receives every message as it arrives and queues it, so a caller can wait for a message with a
/// timeout or stop waiting without disturbing the socket (cancelling a .NET <see cref="WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/>
/// aborts the socket; cancelling an aiohttp <c>receive()</c> does not).</para>
/// <para>As aiohttp: a message whose size reaches the limit closes the socket with 1009 (its reader refuses a frame
/// once the message reaches <c>max_msg_size</c>); a text message that is not UTF-8 closes it with 1007; a close from
/// the peer is answered with 1000; and once closing has started, every receive returns
/// <see cref="SocketMessageKind.Closed"/> and every send fails.</para>
/// </remarks>
internal sealed class MessageSocket : IAsyncDisposable
{
    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly WebSocket _socket;
    private readonly int _maxMessageBytes;
    private readonly Channel<SocketMessage> _messages = Channel.CreateUnbounded<SocketMessage>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Task _reader;
    private int _closed;

    internal MessageSocket(WebSocket socket, int maxMessageBytes)
    {
        _socket = socket;
        _maxMessageBytes = maxMessageBytes;
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>aiohttp's <c>closed</c>: closing has started, from either side.</summary>
    internal bool Closed => Volatile.Read(ref _closed) != 0;

    /// <summary>The next message; <see cref="SocketMessageKind.Closed"/> once the socket is closing.
    /// <paramref name="cancellationToken"/> stops the wait only.</summary>
    internal async Task<SocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Closed)
        {
            return SocketMessage.Closed;
        }

        try
        {
            var message = await _messages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return Closed ? SocketMessage.Closed : message;
        }
        catch (ChannelClosedException)
        {
            return SocketMessage.Closed;
        }
    }

    /// <summary>Send one text message.</summary>
    /// <exception cref="InvalidOperationException">The socket is closing (aiohttp: "Cannot write to closing transport").</exception>
    internal async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed)
            {
                throw new InvalidOperationException("Cannot write to closing transport");
            }

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>aiohttp's <c>close()</c>: send a close frame (once) and wait up to 10 seconds for the peer's.</summary>
    internal async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        await StartClosingAsync(status).ConfigureAwait(false);
        if (await Task.WhenAny(_reader, Task.Delay(CloseTimeout)).ConfigureAwait(false) != _reader)
        {
            _socket.Abort();
        }

        await _reader.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _socket.Dispose();
        _send.Dispose();
    }

    /// <summary>Mark the socket closed and send a close frame if none was sent; the reader receives the reply.</summary>
    private async Task StartClosingAsync(WebSocketCloseStatus status)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await _socket.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exc) when (exc is WebSocketException or OperationCanceledException or InvalidOperationException or IOException
                                    or ObjectDisposedException)
        {
            _socket.Abort();
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[_maxMessageBytes];
        try
        {
            while (true)
            {
                var count = 0;
                ValueWebSocketReceiveResult result = default;
                var tooBig = false;
                do
                {
                    if (count == buffer.Length)
                    {
                        tooBig = true;
                        break;
                    }

                    result = await _socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // The peer closed (or answered our close): answer with 1000 if we have not closed yet.
                        await StartClosingAsync(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                        return;
                    }

                    count += result.Count;
                }
                while (!result.EndOfMessage);

                if (tooBig || count >= _maxMessageBytes)
                {
                    await FailAsync(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                    return;
                }

                if (Closed)
                {
                    continue; // aiohttp's close() reads and drops messages until the peer's close frame.
                }

                SocketMessage message;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string text;
                    try
                    {
                        text = Jwt.Utf8Strict.Encoding.GetString(buffer, 0, count);
                    }
                    catch (DecoderFallbackException)
                    {
                        await FailAsync(WebSocketCloseStatus.InvalidPayloadData).ConfigureAwait(false);
                        return;
                    }

                    message = new SocketMessage(SocketMessageKind.Text, Text: text);
                }
                else
                {
                    message = new SocketMessage(SocketMessageKind.Binary, Data: buffer.AsSpan(0, count).ToArray());
                }

                _messages.Writer.TryWrite(message);
            }
        }
        catch (Exception exc) when (exc is WebSocketException or OperationCanceledException or InvalidOperationException or IOException
                                    or ObjectDisposedException)
        {
            Interlocked.Exchange(ref _closed, 1);
        }
        finally
        {
            _messages.Writer.TryComplete();
        }
    }

    /// <summary>aiohttp's reader error: close with <paramref name="status"/>, then drain until the peer's close frame.</summary>
    private async Task FailAsync(WebSocketCloseStatus status)
    {
        await StartClosingAsync(status).ConfigureAwait(false);
        var drain = new byte[16 * 1024];
        while (_socket.State is WebSocketState.CloseSent or WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(drain.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }
}

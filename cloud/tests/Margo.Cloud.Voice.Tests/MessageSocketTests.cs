using System.IO.Pipelines;
using System.Net.WebSockets;

namespace Margo.Cloud.Voice.Tests;

/// <summary>
/// <see cref="MessageSocket"/> reads as aiohttp's <c>WebSocketDataQueue</c> does: once more than 64 KiB of messages
/// wait unread, it stops reading the socket until the caller takes some, so a peer that sends faster than the session
/// handles its messages is held back by the transport instead of filling the server's memory.
/// </summary>
public sealed class MessageSocketTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unread_messages_hold_the_peer_back_and_arrive_in_order()
    {
        var (server, client, pipes) = Pair();
        var socket = new MessageSocket(server, VoiceService.MaxFrame);
        try
        {
            const int count = 40;
            var sending = Task.Run(async () =>
            {
                for (var index = 0; index < count; index++)
                {
                    var message = new byte[100 * 1024];
                    message[0] = (byte)index;
                    await client.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }, Cancel);

            // Nothing reads the socket's messages yet: the peer must be held back long before 4 MB are queued.
            var finished = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(2), Cancel));
            Assert.NotSame(sending, finished);

            for (var index = 0; index < count; index++)
            {
                var message = await socket.ReceiveAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(30), Cancel);
                Assert.Equal(SocketMessageKind.Binary, message.Kind);
                Assert.Equal(100 * 1024, message.Data!.Length);
                Assert.Equal((byte)index, message.Data[0]);
            }

            await sending.WaitAsync(TimeSpan.FromSeconds(30), Cancel);

            // Closing still works while the peer has sent more than the queue holds.
            var more = Task.Run(async () =>
            {
                for (var index = 0; index < 3; index++)
                {
                    await client.SendAsync(new byte[100 * 1024], WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }, Cancel);
            await Task.WhenAny(more, Task.Delay(TimeSpan.FromMilliseconds(500), Cancel));
            Assert.False(more.IsCompleted); // held back again

            var answering = Task.Run(async () =>
            {
                var buffer = new byte[200 * 1024];
                while (true)
                {
                    var result = await client.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Answer only once the held-back messages are out: a send that passed its state check before
                        // the close frame would otherwise write after it, into a transport nobody reads any more.
                        await more.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                }
            }, Cancel);
            await socket.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5), Cancel);
            await answering.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
            await more.WaitAsync(TimeSpan.FromSeconds(5), Cancel); // once closing, the rest is read and dropped
            Assert.True(socket.Closed);
            Assert.Equal(SocketMessageKind.Closed, (await socket.ReceiveAsync(Cancel)).Kind);
        }
        finally
        {
            // End both directions so no read waits forever, whatever the test's outcome.
            foreach (var pipe in pipes)
            {
                await pipe.Writer.CompleteAsync();
            }

            await socket.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Cancel);
            client.Dispose();
        }
    }

    /// <summary>Two WebSockets over in-memory pipes that hold back a writer once 64 KiB wait unread.</summary>
    private static (WebSocket Server, WebSocket Client, Pipe[] Pipes) Pair()
    {
        var options = new PipeOptions(pauseWriterThreshold: 64 * 1024, resumeWriterThreshold: 32 * 1024);
        var toServer = new Pipe(options);
        var toClient = new Pipe(options);
        var server = WebSocket.CreateFromStream(new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
                                                new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        var client = WebSocket.CreateFromStream(new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
                                                new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
        return (server, client, [toServer, toClient]);
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            input.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            output.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            output.WriteAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

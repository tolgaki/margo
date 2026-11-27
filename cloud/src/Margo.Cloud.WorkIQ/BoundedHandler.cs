using System.Net;

namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Stops reading a response body after <c>maxBytes</c> (after decompression) with
/// <see cref="TransportResponseTooLargeException"/>, so no answer is buffered beyond the bound.
/// </summary>
internal sealed class BoundedHandler(HttpMessageHandler inner, long maxBytes) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            response.Dispose();
            throw new TransportResponseTooLargeException(WorkIQMcpTransport.TooLarge);
        }

        response.Content = new BoundedContent(response.Content, maxBytes);
        return response;
    }

    private sealed class BoundedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _maxBytes;

        public BoundedContent(HttpContent inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new BoundedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _maxBytes);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BoundedStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > maxBytes)
            {
                throw new TransportResponseTooLargeException(WorkIQMcpTransport.TooLarge);
            }

            return read;
        }
    }
}

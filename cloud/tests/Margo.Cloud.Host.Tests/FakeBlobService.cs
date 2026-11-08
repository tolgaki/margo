using System.Globalization;
using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// A small in-memory Azure Blob service behind the real Azure SDK (through <see cref="HttpClientTransport"/>): containers,
/// block blobs, ETags and leases, with the status codes and error codes the service answers. Records each operation in
/// the vocabulary of the Python test's stubs (<c>create</c>, <c>upload</c>, <c>acquire</c>, <c>release</c>, ...).
/// </summary>
internal sealed class FakeBlobService : HttpMessageHandler
{
    public const string Account = "https://state.example.com";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Blob> _blobs = new(StringComparer.Ordinal);
    private readonly List<string> _calls = [];
    private readonly List<HttpRequestMessage> _requests = [];
    private int _etag;

    /// <summary>Answers to give instead of the service's, by operation, in order (status and error code).</summary>
    public Dictionary<string, Queue<(HttpStatusCode Status, string Code)>> Failures { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public BlobServiceClient Client() => new(new Uri(Account), Options());

    public BlobClientOptions Options()
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(this, disposeHandler: false)) };
        options.Retry.MaxRetries = 0;
        return options;
    }

    public void Fail(string operation, HttpStatusCode status, string code)
    {
        lock (_gate)
        {
            if (!Failures.TryGetValue(operation, out var queue))
            {
                queue = new Queue<(HttpStatusCode, string)>();
                Failures[operation] = queue;
            }

            queue.Enqueue((status, code));
        }
    }

    public void RemoveContainer(string container)
    {
        lock (_gate)
        {
            _containers.Remove(container);
            foreach (var key in _blobs.Keys.Where(key => key.StartsWith(container + "/", StringComparison.Ordinal)).ToList())
            {
                _blobs.Remove(key);
            }
        }
    }

    /// <summary>Lease another holder: the blob exists and has a lease this client does not know.</summary>
    public void LeaseElsewhere(string container, string name)
    {
        lock (_gate)
        {
            _containers.Add(container);
            var key = container + "/" + name;
            if (!_blobs.TryGetValue(key, out var blob))
            {
                blob = new Blob([], NextETag());
                _blobs[key] = blob;
            }

            blob.LeaseId = "someone-else";
        }
    }

    public void ReleaseElsewhere(string container, string name)
    {
        lock (_gate)
        {
            _blobs[container + "/" + name].LeaseId = null;
        }
    }

    public byte[]? Content(string container, string name)
    {
        lock (_gate)
        {
            return _blobs.TryGetValue(container + "/" + name, out var blob) ? blob.Data : null;
        }
    }

    public string? LeaseOf(string container, string name)
    {
        lock (_gate)
        {
            return _blobs.TryGetValue(container + "/" + name, out var blob) ? blob.LeaseId : null;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = request.RequestUri.Query;
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        var container = Uri.UnescapeDataString(slash < 0 ? path : path[..slash]);
        var blobName = slash < 0 ? null : Uri.UnescapeDataString(path[(slash + 1)..]);
        var operation = Operation(request, query, blobName);
        lock (_gate)
        {
            _calls.Add(operation);
            _requests.Add(request);
            if (Failures.TryGetValue(operation, out var queue) && queue.Count > 0)
            {
                var (status, code) = queue.Dequeue();
                return Error(status, code);
            }

            return Answer(request, operation, container, blobName, body);
        }
    }

    private static string Operation(HttpRequestMessage request, string query, string? blob)
    {
        if (blob is null)
        {
            return request.Method == HttpMethod.Put ? "create" : "container:" + request.Method.Method;
        }

        if (query.Contains("comp=lease", StringComparison.Ordinal))
        {
            return Header(request, "x-ms-lease-action") ?? "lease";
        }

        return request.Method == HttpMethod.Put ? "upload" : request.Method == HttpMethod.Get ? "download" : request.Method.Method;
    }

    private HttpResponseMessage Answer(HttpRequestMessage request, string operation, string container, string? name, byte[] body)
    {
        if (operation == "create")
        {
            return _containers.Add(container) ? Ok(HttpStatusCode.Created, NextETag()) : Error(HttpStatusCode.Conflict, "ContainerAlreadyExists");
        }

        if (!_containers.Contains(container))
        {
            return Error(HttpStatusCode.NotFound, "ContainerNotFound");
        }

        var key = container + "/" + name;
        _blobs.TryGetValue(key, out var blob);
        var leaseId = Header(request, "x-ms-lease-id");
        switch (operation)
        {
            case "upload":
                if (Header(request, "If-None-Match") == "*" && blob is not null)
                {
                    return blob.LeaseId is not null && leaseId is null
                        ? Error(HttpStatusCode.PreconditionFailed, "LeaseIdMissing")
                        : Error(HttpStatusCode.Conflict, "BlobAlreadyExists");
                }

                if (blob?.LeaseId is not null && blob.LeaseId != leaseId)
                {
                    return Error(HttpStatusCode.PreconditionFailed, leaseId is null ? "LeaseIdMissing" : "LeaseIdMismatchWithBlobOperation");
                }

                var etag = NextETag();
                if (blob is null)
                {
                    _blobs[key] = new Blob(body, etag);
                }
                else
                {
                    blob.Data = body;
                    blob.ETag = etag;
                }

                return Ok(HttpStatusCode.Created, etag);
            case "download":
                if (blob is null)
                {
                    return Error(HttpStatusCode.NotFound, "BlobNotFound");
                }

                if (leaseId is not null && blob.LeaseId != leaseId)
                {
                    return Error(HttpStatusCode.PreconditionFailed, "LeaseIdMismatchWithBlobOperation");
                }

                var response = Ok(HttpStatusCode.OK, blob.ETag);
                response.Content = new ByteArrayContent(blob.Data);
                response.Content.Headers.ContentLength = blob.Data.Length;
                response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
                response.Content.Headers.TryAddWithoutValidation("Last-Modified", "Sun, 08 Nov 2026 09:30:00 GMT");
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                return response;
            case "acquire":
                if (blob is null)
                {
                    return Error(HttpStatusCode.NotFound, "BlobNotFound");
                }

                if (blob.LeaseId is not null)
                {
                    return Error(HttpStatusCode.Conflict, "LeaseAlreadyPresent");
                }

                blob.LeaseId = Header(request, "x-ms-proposed-lease-id") ?? Guid.NewGuid().ToString();
                blob.Duration = Header(request, "x-ms-lease-duration");
                var acquired = Ok(HttpStatusCode.Created, blob.ETag);
                acquired.Headers.TryAddWithoutValidation("x-ms-lease-id", blob.LeaseId);
                return acquired;
            case "renew":
            case "release":
                if (blob?.LeaseId is null || blob.LeaseId != leaseId)
                {
                    return Error(HttpStatusCode.Conflict, "LeaseIdMismatchWithLeaseOperation");
                }

                if (operation == "release")
                {
                    blob.LeaseId = null;
                }

                var leased = Ok(HttpStatusCode.OK, blob.ETag);
                leased.Headers.TryAddWithoutValidation("x-ms-lease-id", leaseId);
                return leased;
            default:
                return Error(HttpStatusCode.BadRequest, "UnsupportedHttpVerb");
        }
    }

    private string NextETag() => "\"0x8DE" + (++_etag).ToString("X6", CultureInfo.InvariantCulture) + "\"";

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpResponseMessage Ok(HttpStatusCode status, string etag)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        response.Content.Headers.TryAddWithoutValidation("Last-Modified", "Sun, 08 Nov 2026 09:30:00 GMT");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.TryAddWithoutValidation("x-ms-version", "2025-01-05");
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>" + code + "</Code><Message>" + code + " (fake)</Message></Error>";
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml)) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/xml");
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString());
        return response;
    }

    private sealed class Blob(byte[] data, string etag)
    {
        public byte[] Data { get; set; } = data;

        public string ETag { get; set; } = etag;

        public string? LeaseId { get; set; }

        public string? Duration { get; set; }
    }
}

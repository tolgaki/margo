using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace Margo.Cloud.Router;

/// <summary>Reads the route table blob. Python: <c>load_routes</c> in <c>router/app.py</c>.</summary>
public interface IRouteSource
{
    /// <summary>The blob text, or an empty string when there is no blob yet.</summary>
    Task<string> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Puts provisioning messages on the queue. Python: <c>send_message</c> in <c>router/app.py</c>.</summary>
public interface IProvisioningSink
{
    /// <summary>Send one message, as text.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken);
}

/// <summary>The route table in Blob storage (<c>MARGO_ROUTES_URL</c>).</summary>
public sealed class BlobRouteSource : IRouteSource
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly BlobClient _blob;

    public BlobRouteSource(BlobClient blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _blob = blob;
    }

    /// <summary>Python: <c>(await (await routes.download_blob()).readall()).decode("utf-8")</c>, with
    /// <c>ResourceNotFoundError</c> (any 404) read as no table.</summary>
    /// <exception cref="DecoderFallbackException">The blob is not UTF-8 (Python: UnicodeDecodeError).</exception>
    public async Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _blob.DownloadContentAsync(cancellationToken);
            return StrictUtf8.GetString(result.Value.Content.ToMemory().Span);
        }
        catch (RequestFailedException exc) when (exc.Status == 404)
        {
            return string.Empty;
        }
    }
}

/// <summary>The provisioning queue in Queue storage (<c>MARGO_PROVISION_QUEUE_URL</c>).</summary>
/// <remarks>
/// Messages are sent as plain text, as the Python <c>QueueClient</c> sent them (its default
/// <c>NoEncodePolicy</c>) and as the Python provisioner reads them. <see cref="CreateClient"/> sets
/// <see cref="QueueMessageEncoding.None"/>, which is also the .NET default.
/// </remarks>
public sealed class StorageQueueProvisioningSink : IProvisioningSink
{
    private readonly QueueClient _queue;

    public StorageQueueProvisioningSink(QueueClient queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    /// <summary>A queue client with the encoding the Python services use.</summary>
    public static QueueClient CreateClient(Uri queueUri, Azure.Core.TokenCredential credential) =>
        new(queueUri, credential, ClientOptions());

    /// <summary>The client options: plain-text messages.</summary>
    public static QueueClientOptions ClientOptions() => new() { MessageEncoding = QueueMessageEncoding.None };

    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await _queue.SendMessageAsync(text, cancellationToken);
    }
}

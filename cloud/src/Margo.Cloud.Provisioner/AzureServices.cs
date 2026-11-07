using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Queues;

namespace Margo.Cloud.Provisioner;

/// <summary>
/// The provisioning queue in Azure Queue Storage. Python: <c>QueueClient.from_queue_url</c> with its
/// default (no) message encoding, so the message text is the JSON itself, as the router writes it.
/// </summary>
public sealed class AzureProvisioningQueueClient : IProvisioningQueueClient
{
    private readonly QueueClient _queue;

    /// <param name="credential">The job's identity.</param>
    /// <param name="queueUrl">The queue's URL (<c>.../margo-provision</c>).</param>
    /// <param name="transport">Replaces the HTTP transport (tests); the default transport otherwise.</param>
    public AzureProvisioningQueueClient(TokenCredential credential, string queueUrl, HttpPipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(queueUrl);
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
        if (transport is not null)
        {
            options.Transport = transport;
        }

        _queue = new QueueClient(new Uri(queueUrl), credential, options);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QueuedMessage>> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        var page = await _queue.ReceiveMessagesAsync(maxMessages, visibilityTimeout, cancellationToken).ConfigureAwait(false);
        return page.Value
            .Select(message => new QueuedMessage(message.MessageId, message.PopReceipt, message.Body?.ToString() ?? string.Empty, message.DequeueCount))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await _queue.SendMessageAsync(text, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The Azure clients for one command, signed in as Python's <c>_credential()</c> did: the managed
/// identity named by <c>AZURE_CLIENT_ID</c> when it is set and not empty, otherwise
/// <see cref="DefaultAzureCredential"/>.
/// </summary>
public sealed class AzureProvisionerServices : IProvisionerServices
{
    private readonly TokenCredential _credential;
    private readonly HttpPipelineTransport? _transport;

    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="transport">Replaces the HTTP transport (tests); the default transport otherwise.</param>
    public AzureProvisionerServices(Func<string, string?> environment, HttpPipelineTransport? transport = null)
        : this(CreateCredential(environment), transport)
    {
    }

    /// <summary>Clients that sign in with <paramref name="credential"/>.</summary>
    public AzureProvisionerServices(TokenCredential credential, HttpPipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _transport = transport;
    }

    /// <summary>Python's <c>_credential()</c>.</summary>
    public static TokenCredential CreateCredential(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var clientId = environment(ProvisionerConfig.ClientIdVariable);
        return string.IsNullOrEmpty(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    /// <inheritdoc/>
    public ICloud CreateCloud(string subscriptionId, string resourceGroup, string routesUrl) =>
        new AzureCloud(_credential, subscriptionId, resourceGroup, routesUrl, _transport);

    /// <inheritdoc/>
    public IProvisioningQueueClient CreateQueue(string queueUrl) => new AzureProvisioningQueueClient(_credential, queueUrl, _transport);
}

using System.Text.Json.Nodes;

namespace Margo.Cloud.Provisioner;

/// <summary>The route table blob as read: its text and the ETag to write it back against.</summary>
/// <param name="Text">The blob's UTF-8 text; empty when the blob does not exist yet.</param>
/// <param name="ETag">The blob's ETag, exactly as the service returned it (quotes included);
/// <see langword="null"/> when the blob does not exist yet.</param>
public sealed record RouteBlob(string Text, string? ETag);

/// <summary>
/// The Azure calls the provisioner makes. Python: the <c>cloud</c> object passed to
/// <c>provision.core.Provisioner</c> (<c>AzureCloud</c> in production, <c>FakeCloud</c> in the tests).
/// </summary>
/// <remarks>
/// Every call either completes or throws; the provisioner records the exception's type name on the
/// route and retries the message later. <see cref="SaveRoutesAsync"/> is the one call that reports
/// a conflict by its result instead.
/// </remarks>
public interface ICloud
{
    /// <summary>Deploy <paramref name="template"/> as an incremental ARM deployment named
    /// <paramref name="name"/> into the provisioner's resource group, and wait for it to finish.</summary>
    Task DeployAsync(string name, JsonNode? template, JsonObject parameters, CancellationToken cancellationToken);

    /// <summary>Delete the instance's Container App; an app that does not exist is not an error.</summary>
    Task DeleteAppAsync(string name, CancellationToken cancellationToken);

    /// <summary>Delete the instance's user-assigned identity, after removing its role assignments in
    /// the resource group, so none is left pointing at nothing. An identity that does not exist is not
    /// an error.</summary>
    Task DeleteIdentityAsync(string name, CancellationToken cancellationToken);

    /// <summary>Read the route table blob, or <c>("", null)</c> when it does not exist yet.</summary>
    Task<RouteBlob> LoadRoutesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Write the route table blob only if it still has <paramref name="etag"/> (or, for a
    /// <see langword="null"/> ETag, only if it still does not exist). Returns <see langword="false"/>
    /// when that condition no longer holds, so concurrent writers never lose an update.
    /// </summary>
    Task<bool> SaveRoutesAsync(string text, string? etag, CancellationToken cancellationToken);
}

/// <summary>One message received from the provisioning queue, hidden from other readers until its
/// visibility timeout expires.</summary>
/// <param name="MessageId">The queue's id for the message.</param>
/// <param name="PopReceipt">The receipt that allows this reader to delete it.</param>
/// <param name="Text">The message text, as it was sent (no Base64).</param>
/// <param name="DequeueCount">How many times the message has been received, this time included.</param>
public sealed record QueuedMessage(string MessageId, string PopReceipt, string Text, long DequeueCount);

/// <summary>The provisioning queue. Python: <c>azure.storage.queue.QueueClient</c> as
/// <c>provision/azure.py</c> uses it.</summary>
public interface IProvisioningQueueClient
{
    /// <summary>Receive up to <paramref name="maxMessages"/> visible messages and hide them for
    /// <paramref name="visibilityTimeout"/>. An empty list means the queue has nothing visible.</summary>
    Task<IReadOnlyList<QueuedMessage>> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken);

    /// <summary>Delete a received message for good.</summary>
    Task DeleteAsync(QueuedMessage message, CancellationToken cancellationToken);

    /// <summary>Put a message on the queue, as text.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Creates the Azure clients for one command. Python: <c>_credential()</c>,
/// <c>AzureCloud(...)</c> and <c>_queue(credential)</c> in <c>provision/azure.py</c>.</summary>
public interface IProvisionerServices
{
    /// <summary>The cloud for one resource group and route table blob.</summary>
    ICloud CreateCloud(string subscriptionId, string resourceGroup, string routesUrl);

    /// <summary>The provisioning queue at <paramref name="queueUrl"/>.</summary>
    IProvisioningQueueClient CreateQueue(string queueUrl);
}

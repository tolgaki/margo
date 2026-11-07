using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Deployments;
using Azure.ResourceManager.Resources.Deployments.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Margo.Cloud.Core;

namespace Margo.Cloud.Provisioner;

/// <summary>
/// The provisioner's Azure calls: one resource group through Azure Resource Manager, and the route
/// table blob. Python: <c>AzureCloud</c> in <c>provision/azure.py</c>.
/// </summary>
/// <remarks>
/// API versions are pinned to what the Python job used: the Container App and identity versions it
/// named, and the default versions of <c>azure-mgmt-resource</c> 23.4 (deployments) and
/// <c>azure-mgmt-authorization</c> 4.0 (role assignments).
/// </remarks>
public sealed class AzureCloud : ICloud
{
    /// <summary>Container Apps API version for deleting an app. Python: <c>APP_API</c>.</summary>
    public const string AppApiVersion = "2024-03-01";

    /// <summary>Managed identity API version for reading and deleting an identity. Python: <c>IDENTITY_API</c>.</summary>
    public const string IdentityApiVersion = "2023-01-31";

    /// <summary>Deployments API version; Python's <c>ResourceManagementClient</c> default.</summary>
    public const string DeploymentApiVersion = "2025-03-01";

    /// <summary>Role assignments API version; Python's <c>AuthorizationManagementClient</c> default.</summary>
    public const string RoleAssignmentApiVersion = "2022-04-01";

    private const string AppProvider = "Microsoft.App/containerApps";
    private const string IdentityProvider = "Microsoft.ManagedIdentity/userAssignedIdentities";
    private const string DeploymentProvider = "Microsoft.Resources/deployments";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ArmClient _arm;
    private readonly HttpPipeline _armPipeline;
    private readonly Uri _armEndpoint;
    private readonly BlobClient _routes;
    private readonly string _subscriptionId;
    private readonly string _resourceGroup;

    /// <param name="credential">The provisioner job's identity.</param>
    /// <param name="subscriptionId">The subscription that holds the resource group.</param>
    /// <param name="resourceGroup">The one resource group the job deploys into.</param>
    /// <param name="routesUrl">The route table blob's URL (<c>.../margo-routes/routes.json</c>).</param>
    /// <param name="transport">Replaces the HTTP transport (tests); the default transport otherwise.</param>
    public AzureCloud(TokenCredential credential, string subscriptionId, string resourceGroup, string routesUrl, HttpPipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(subscriptionId);
        ArgumentNullException.ThrowIfNull(resourceGroup);
        ArgumentNullException.ThrowIfNull(routesUrl);
        var armOptions = new ArmClientOptions();
        armOptions.SetApiVersion(new ResourceType(AppProvider), AppApiVersion);
        armOptions.SetApiVersion(new ResourceType(IdentityProvider), IdentityApiVersion);
        armOptions.SetApiVersion(new ResourceType(DeploymentProvider), DeploymentApiVersion);
        armOptions.SetApiVersion(RoleAssignmentResource.ResourceType, RoleAssignmentApiVersion);
        var blobOptions = new BlobClientOptions();
        if (transport is not null)
        {
            armOptions.Transport = transport;
            blobOptions.Transport = transport;
        }

        _arm = new ArmClient(credential, subscriptionId, armOptions);
        var environment = armOptions.Environment ?? ArmEnvironment.AzurePublicCloud;
        _armEndpoint = environment.Endpoint;
        _armPipeline = HttpPipelineBuilder.Build(armOptions, new BearerTokenAuthenticationPolicy(credential, environment.DefaultScope));
        _routes = new BlobClient(new Uri(routesUrl), credential, blobOptions);
        _subscriptionId = subscriptionId;
        _resourceGroup = resourceGroup;
    }

    private string ResourceGroupId => "/subscriptions/" + _subscriptionId + "/resourceGroups/" + _resourceGroup;

    private ResourceIdentifier Id(string provider, string name) =>
        new(ResourceGroupId + "/providers/" + provider + "/" + name);

    /// <inheritdoc/>
    /// <remarks>The body is <c>{"properties": {"mode": "Incremental", "template": ..., "parameters":
    /// ...}}</c>, as Python sent it; the call returns when the deployment has finished and throws
    /// <see cref="RequestFailedException"/> when it failed.</remarks>
    public async Task DeployAsync(string name, JsonNode? template, JsonObject parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var properties = new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
        {
            Template = BinaryData.FromString(PythonJson.Dumps(template)),
            Parameters = BinaryData.FromString(PythonJson.Dumps(parameters)),
        };
        var resourceGroup = _arm.GetResourceGroupResource(new ResourceIdentifier(ResourceGroupId));
        await resourceGroup.GetArmDeployments()
            .CreateOrUpdateAsync(WaitUntil.Completed, name, new ArmDeploymentContent(properties), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAppAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _arm.GetGenericResource(Id(AppProvider, name)).DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exc) when (exc.Status == 404)
        {
            // Already gone (Python: ResourceNotFoundError).
        }
    }

    /// <inheritdoc/>
    /// <remarks>The role assignments are those ARM lists for the resource group with the filter
    /// <c>principalId eq '...'</c> (at, above or below the resource group), each deleted by its id
    /// as soon as its page is read, then the identity itself is deleted.</remarks>
    public async Task DeleteIdentityAsync(string name, CancellationToken cancellationToken)
    {
        var identityId = Id(IdentityProvider, name);
        string principal;
        try
        {
            var identity = await _arm.GetGenericResource(identityId).GetAsync(cancellationToken).ConfigureAwait(false);
            principal = PrincipalId(identity.Value.Data.Properties);
        }
        catch (RequestFailedException exc) when (exc.Status == 404)
        {
            return;
        }

        var assignments = _arm.GetRoleAssignments(new ResourceIdentifier(ResourceGroupId))
            .GetAllAsync(filter: "principalId eq '" + principal + "'", cancellationToken: cancellationToken);
        await foreach (var assignment in assignments.ConfigureAwait(false))
        {
            await DeleteRoleAssignmentAsync(assignment.Id, cancellationToken).ConfigureAwait(false);
        }

        await _arm.GetGenericResource(identityId).DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Python's <c>authorization.role_assignments.delete_by_id(id)</c>: <c>DELETE {id}?api-version=2022-04-01</c>,
    /// where 200 (deleted) and 204 (already gone) succeed and any other status throws.
    /// </summary>
    /// <remarks><c>RoleAssignmentResource.DeleteAsync</c> is not used: it builds the deleted
    /// assignment from the response body and throws <see cref="NullReferenceException"/> on 204, which
    /// ARM returns for an assignment that is already gone (a retry, or a list that still shows a
    /// just-deleted assignment). The generic resource API cannot take assignments above a subscription
    /// and polls a 202 that Python refused.</remarks>
    private async Task DeleteRoleAssignmentAsync(ResourceIdentifier id, CancellationToken cancellationToken)
    {
        using var message = _armPipeline.CreateMessage();
        var request = message.Request;
        request.Method = RequestMethod.Delete;
        var uri = new RequestUriBuilder();
        uri.Reset(_armEndpoint);
        uri.AppendPath(id.ToString(), escape: false);
        uri.AppendQuery("api-version", RoleAssignmentApiVersion);
        request.Uri = uri;
        request.Headers.Add("Accept", "application/json");
        await _armPipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (message.Response.Status is not (200 or 204))
        {
            throw new RequestFailedException(message.Response);
        }
    }

    /// <summary>Python's <c>.properties["principalId"]</c>.</summary>
    private static string PrincipalId(BinaryData? properties)
    {
        var node = properties is null ? null : PythonJson.Loads(properties.ToString());
        if (node is not JsonObject values)
        {
            throw new InvalidOperationException("identity has no properties"); // Python: TypeError
        }

        if (!values.TryGetPropertyValue("principalId", out var principal))
        {
            throw new KeyNotFoundException("principalId"); // Python: KeyError
        }

        return principal is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new InvalidOperationException("identity principal id is not a string");
    }

    /// <inheritdoc/>
    public async Task<RouteBlob> LoadRoutesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var download = await _routes.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return new RouteBlob(StrictUtf8.GetString(download.Value.Content.ToMemory().Span), download.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException exc) when (exc.Status == 404)
        {
            return new RouteBlob(string.Empty, null);
        }
    }

    /// <inheritdoc/>
    /// <remarks>A <see langword="null"/> ETag writes only if the blob does not exist
    /// (<c>If-None-Match: *</c>); otherwise only if it still has that ETag (<c>If-Match</c>). Python
    /// treated <c>ResourceExistsError</c> (any 409) and <c>ResourceModifiedError</c> (412 with
    /// <c>ConditionNotMet</c> or <c>BlobOverwritten</c>) as a conflict; so does this.</remarks>
    public async Task<bool> SaveRoutesAsync(string text, string? etag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var conditions = etag is null
            ? new BlobRequestConditions { IfNoneMatch = ETag.All }
            : new BlobRequestConditions { IfMatch = new ETag(etag) };
        try
        {
            await _routes.UploadAsync(BinaryData.FromBytes(StrictUtf8.GetBytes(text)), new BlobUploadOptions { Conditions = conditions }, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exc) when (IsConflict(exc))
        {
            return false;
        }
    }

    private static bool IsConflict(RequestFailedException exc) =>
        exc.Status == 409 || (exc.Status == 412 && exc.ErrorCode is "ConditionNotMet" or "BlobOverwritten");
}

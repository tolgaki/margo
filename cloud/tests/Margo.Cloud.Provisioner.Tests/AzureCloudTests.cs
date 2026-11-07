using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Xunit;
using static Margo.Cloud.Provisioner.Tests.Fixtures;

namespace Margo.Cloud.Provisioner.Tests;

/// <summary>
/// The Azure SDK calls <see cref="AzureCloud"/> and <see cref="AzureProvisioningQueueClient"/> make,
/// checked at the HTTP level against canned ARM, Blob and Queue responses (no network). They pin the
/// requests provision/azure.py sent: the URLs, API versions, conditional headers and the order of
/// deletions.
/// </summary>
public sealed class AzureCloudTests : IDisposable
{
    private const string Group = "rg-margo-fictional";
    private const string RoutesUrl = "https://fictional.blob.example.com/margo-routes/routes.json";
    private const string QueueUrl = "https://fictional.queue.example.com/margo-provision";
    private const string Arm = "https://management.azure.com";

    /// <summary>The all-zero subscription: the SDK parses subscription ids as GUIDs, and no tenant
    /// identifier may appear in the repository.</summary>
    private static readonly string Subscription = Guid.Empty.ToString();
    private static readonly string GroupPath = "/subscriptions/" + Subscription + "/resourceGroups/" + Group;

    private readonly RecordingHandler _http = new();

    private AzureCloud Cloud() => new(new FixtureCredential(), Subscription, Group, RoutesUrl, new HttpClientTransport(new HttpClient(_http)));

    private AzureProvisioningQueueClient Queue() => new(new FixtureCredential(), QueueUrl, new HttpClientTransport(new HttpClient(_http)));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Deploy_is_an_incremental_deployment_of_the_template_that_waits_for_completion()
    {
        _http.Respond = request => Json(HttpStatusCode.OK,
            "{\"id\": \"" + GroupPath + "/providers/Microsoft.Resources/deployments/margo-instance-" + Key + "\", \"name\": \"margo-instance-" + Key + "\","
            + " \"properties\": {\"provisioningState\": \"Succeeded\", \"mode\": \"Incremental\"}}");
        // The real template and parameters, with the list and object values main.bicep passes.
        var template = ProvisionerConfig.LoadTemplate(_ => null);
        var config = Config();
        config["extraEnv"] = new JsonArray(new JsonObject { ["name"] = "MARGO_TIMEZONE", ["value"] = "Europe/Zurich" });
        config["voiceName"] = "fictional-voice-é";
        var parameters = Margo.Cloud.Core.Instances.DeploymentParameters(Key, new Margo.Cloud.Core.ProvisioningMessage("ensure", Identity, User), config);
        await Cloud().DeployAsync("margo-instance-" + Key, template, parameters, Token);

        var put = Assert.Single(_http.Requests);
        Assert.Equal("PUT", put.Method);
        // 2025-03-01 is pinned (Python's default); the .NET package would send 2025-04-01.
        Assert.Equal(Arm + GroupPath + "/providers/Microsoft.Resources/deployments/margo-instance-" + Key + "?api-version=2025-03-01", put.Url, ignoreCase: true);
        var body = JsonNode.Parse(put.Body!)!.AsObject();
        Assert.Equal(["properties"], body.Select(pair => pair.Key));
        var properties = body["properties"]!.AsObject();
        Assert.Equal(["mode", "parameters", "template"], properties.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("Incremental", (string?)properties["mode"]);
        Assert.True(JsonNode.DeepEquals(template, properties["template"]));
        Assert.True(JsonNode.DeepEquals(parameters, properties["parameters"]));
        Assert.Equal("Europe/Zurich", (string?)properties["parameters"]!["extraEnv"]!["value"]![0]!["value"]);
        Assert.Equal("Bearer fixture-access", put.Authorization);
    }

    [Theory]
    [InlineData("Succeeded", false)]
    [InlineData("Failed", true)]
    public async Task Deploy_polls_a_long_running_deployment_until_it_ends(string status, bool throws)
    {
        var deployment = GroupPath + "/providers/Microsoft.Resources/deployments/margo-instance-" + Key;
        var operation = Arm + deployment + "/operationStatuses/op-1?api-version=2025-03-01";
        _http.Respond = request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                var accepted = Json(HttpStatusCode.Created, "{\"id\": \"" + deployment + "\", \"properties\": {\"provisioningState\": \"Accepted\"}}");
                accepted.Headers.TryAddWithoutValidation("Azure-AsyncOperation", operation);
                accepted.Headers.TryAddWithoutValidation("Retry-After", "0");
                return accepted;
            }

            return request.RequestUri!.AbsolutePath.Contains("operationStatuses", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"status\": \"" + status + "\"" + (throws ? ", \"error\": {\"code\": \"DeploymentFailed\", \"message\": \"fictional\"}" : string.Empty) + "}")
                : Json(HttpStatusCode.OK, "{\"id\": \"" + deployment + "\", \"properties\": {\"provisioningState\": \"" + status + "\"}}");
        };
        var deploy = Cloud().DeployAsync("margo-instance-" + Key, new JsonObject(), new JsonObject(), Token);
        if (throws)
        {
            await Assert.ThrowsAsync<RequestFailedException>(() => deploy);
        }
        else
        {
            await deploy;
        }

        Assert.Equal("PUT", _http.Requests[0].Method);
        Assert.Equal(operation, _http.Requests[1].Url, ignoreCase: true); // it waited for the outcome
        Assert.All(_http.Requests.Skip(1), request => Assert.Equal("GET", request.Method));
    }

    [Fact]
    public async Task A_failed_deployment_throws()
    {
        _http.Respond = _ => Json(HttpStatusCode.BadRequest, "{\"error\": {\"code\": \"InvalidTemplate\", \"message\": \"fictional\"}}");
        var error = await Assert.ThrowsAsync<RequestFailedException>(() => Cloud().DeployAsync("margo-instance-" + Key, new JsonObject(), new JsonObject(), Token));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task Delete_app_uses_the_container_apps_api_and_ignores_a_missing_app()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        await Cloud().DeleteAppAsync("margo-" + Key, Token);
        _http.Respond = _ => Json(HttpStatusCode.NotFound, "{\"error\": {\"code\": \"ResourceNotFound\", \"message\": \"fictional\"}}");
        await Cloud().DeleteAppAsync("margo-" + Key, Token);
        Assert.Equal(2, _http.Requests.Count);
        Assert.All(_http.Requests, request =>
        {
            Assert.Equal("DELETE", request.Method);
            Assert.Equal(Arm + GroupPath + "/providers/Microsoft.App/containerApps/margo-" + Key + "?api-version=2024-03-01", request.Url, ignoreCase: true);
        });
        _http.Respond = _ => Json(HttpStatusCode.Forbidden, "{\"error\": {\"code\": \"AuthorizationFailed\", \"message\": \"fictional\"}}");
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(() => Cloud().DeleteAppAsync("margo-" + Key, Token))).Status);
    }

    [Fact]
    public async Task Delete_identity_removes_its_role_assignments_first()
    {
        var principal = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11).ToString(); // fictional; the SDK parses principal ids as GUIDs
        var identityPath = GroupPath + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-margo-" + Key;
        var assignment1 = GroupPath + "/providers/Microsoft.Storage/storageAccounts/fictional/providers/Microsoft.Authorization/roleAssignments/a1";
        var assignment2 = GroupPath + "/providers/Microsoft.Authorization/roleAssignments/a2";
        _http.Respond = request => request.Method.Method switch
        {
            "GET" when request.RequestUri!.AbsolutePath.EndsWith("roleAssignments", StringComparison.OrdinalIgnoreCase) => Json(HttpStatusCode.OK,
                "{\"value\": [" + Assignment(assignment1, principal) + ", " + Assignment(assignment2, principal) + "]}"),
            "GET" => Json(HttpStatusCode.OK,
                "{\"id\": \"" + identityPath + "\", \"name\": \"id-margo-" + Key + "\", \"type\": \"Microsoft.ManagedIdentity/userAssignedIdentities\","
                + " \"location\": \"westus\", \"properties\": {\"principalId\": \"" + principal + "\", \"clientId\": \"fictional\"}}"),
            "DELETE" when request.RequestUri!.AbsolutePath.Contains("roleAssignments", StringComparison.OrdinalIgnoreCase) => Json(HttpStatusCode.OK,
                Assignment(request.RequestUri.AbsolutePath, principal)),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        };
        await Cloud().DeleteIdentityAsync("id-margo-" + Key, Token);
        string[] expected =
            [
                "GET " + Arm + identityPath + "?api-version=2023-01-31",
                "GET " + Arm + GroupPath + "/providers/Microsoft.Authorization/roleAssignments?$filter=principalId eq '" + principal + "'&api-version=2022-04-01",
                "DELETE " + Arm + assignment1 + "?api-version=2022-04-01",
                "DELETE " + Arm + assignment2 + "?api-version=2022-04-01",
                "DELETE " + Arm + identityPath + "?api-version=2023-01-31",
            ];
        Assert.Equal(expected.Length, _http.Requests.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], _http.Requests[i].Method + " " + Uri.UnescapeDataString(_http.Requests[i].Url), ignoreCase: true);
        }
    }

    [Fact]
    public async Task A_missing_identity_is_not_an_error_and_deletes_nothing()
    {
        _http.Respond = _ => Json(HttpStatusCode.NotFound, "{\"error\": {\"code\": \"ResourceNotFound\", \"message\": \"fictional\"}}");
        await Cloud().DeleteIdentityAsync("id-margo-" + Key, Token);
        Assert.Equal("GET", Assert.Single(_http.Requests).Method);
    }

    [Fact]
    public async Task An_identity_without_a_principal_fails_before_deleting_anything()
    {
        _http.Respond = _ => Json(HttpStatusCode.OK,
            "{\"id\": \"" + GroupPath + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-margo-" + Key + "\", \"properties\": {}}");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Cloud().DeleteIdentityAsync("id-margo-" + Key, Token)); // Python: KeyError
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Routes_are_read_with_their_etag_or_as_empty_when_missing()
    {
        _http.Respond = _ => Blob(HttpStatusCode.OK, "{\"schema_version\": 1, \"routes\": {}}", "\"0x8DEFICTIONAL\"");
        var blob = await Cloud().LoadRoutesAsync(Token);
        Assert.Equal(new RouteBlob("{\"schema_version\": 1, \"routes\": {}}", "\"0x8DEFICTIONAL\""), blob);
        var get = Assert.Single(_http.Requests);
        Assert.Equal(("GET", RoutesUrl), (get.Method, get.Url));

        _http.Respond = _ => StorageError(HttpStatusCode.NotFound, "BlobNotFound");
        Assert.Equal(new RouteBlob(string.Empty, null), await Cloud().LoadRoutesAsync(Token));
        _http.Respond = _ => StorageError(HttpStatusCode.NotFound, "ContainerNotFound");
        Assert.Equal(new RouteBlob(string.Empty, null), await Cloud().LoadRoutesAsync(Token));
        _http.Respond = _ => StorageError(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch");
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(() => Cloud().LoadRoutesAsync(Token))).Status);
    }

    [Fact]
    public async Task Routes_that_are_not_utf8_fail_to_load()
    {
        _http.Respond = _ => Blob(HttpStatusCode.OK, null, "\"0x1\"", [0x7b, 0xff, 0x7d]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Cloud().LoadRoutesAsync(Token)); // Python: UnicodeDecodeError
    }

    [Fact]
    public async Task A_byte_order_mark_is_kept_and_the_table_is_refused()
    {
        // Python: bytes.decode("utf-8") keeps U+FEFF and json.loads refuses it ("Unexpected UTF-8 BOM").
        _http.Respond = _ => Blob(HttpStatusCode.OK, null, "\"0x1\"", [0xef, 0xbb, 0xbf, .. "{\"schema_version\": 1, \"routes\": {}}"u8.ToArray()]);
        var blob = await Cloud().LoadRoutesAsync(Token);
        Assert.Equal('﻿', blob.Text[0]);
        Assert.Equal("route table is not JSON", Assert.Throws<Margo.Cloud.Core.RouteException>(() => Margo.Cloud.Core.RouteTable.Loads(blob.Text)).Message);
    }

    [Fact]
    public async Task Routes_are_written_only_if_unchanged()
    {
        _http.Respond = _ => Created();
        Assert.True(await Cloud().SaveRoutesAsync("{\"routes\": {}}", "\"0x8DEFICTIONAL\"", Token));
        Assert.True(await Cloud().SaveRoutesAsync("{}", null, Token));
        var update = _http.Requests[0];
        Assert.Equal(("PUT", RoutesUrl, "{\"routes\": {}}"), (update.Method, update.Url, update.Body));
        Assert.Equal("\"0x8DEFICTIONAL\"", update.Header("If-Match"));
        Assert.Null(update.Header("If-None-Match"));
        Assert.Equal("BlockBlob", update.Header("x-ms-blob-type"));
        var create = _http.Requests[1];
        Assert.Equal("*", create.Header("If-None-Match"));
        Assert.Null(create.Header("If-Match"));
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "ConditionNotMet", false)]
    [InlineData(HttpStatusCode.PreconditionFailed, "BlobOverwritten", false)]
    [InlineData(HttpStatusCode.Conflict, "BlobAlreadyExists", false)]
    [InlineData(HttpStatusCode.Conflict, "LeaseIdMissing", false)] // any 409 is ResourceExistsError in Python
    [InlineData(HttpStatusCode.PreconditionFailed, "LeaseIdMissing", true)] // a plain HttpResponseError in Python
    [InlineData(HttpStatusCode.NotFound, "ContainerNotFound", true)]
    public async Task Conflicting_writes_return_false_and_other_errors_throw(HttpStatusCode status, string code, bool throws)
    {
        _http.Respond = _ => StorageError(status, code);
        if (throws)
        {
            Assert.Equal((int)status, (await Assert.ThrowsAsync<RequestFailedException>(() => Cloud().SaveRoutesAsync("{}", "\"0x1\"", Token))).Status);
        }
        else
        {
            Assert.False(await Cloud().SaveRoutesAsync("{}", "\"0x1\"", Token));
            Assert.False(await Cloud().SaveRoutesAsync("{}", null, Token));
        }
    }

    [Fact]
    public async Task The_provisioner_and_the_blob_agree_on_etags_end_to_end()
    {
        var stored = string.Empty;
        string? etag = null;
        var version = 0;
        _http.Respond = request =>
        {
            if (request.RequestUri!.Host == "management.azure.com")
            {
                return Json(HttpStatusCode.OK, "{\"id\": \"" + GroupPath + "/providers/Microsoft.Resources/deployments/d\", \"properties\": {\"provisioningState\": \"Succeeded\"}}");
            }

            if (request.Method == HttpMethod.Get)
            {
                return etag is null ? StorageError(HttpStatusCode.NotFound, "BlobNotFound") : Blob(HttpStatusCode.OK, stored, etag);
            }

            var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out values) ? values.Single() : null;
            if (etag is null ? ifNoneMatch != "*" : ifMatch != etag)
            {
                return etag is null ? StorageError(HttpStatusCode.NotFound, "BlobNotFound") : StorageError(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            }

            stored = _http.Requests[^1].Body!;
            etag = "\"0x" + ++version + "\"";
            return Created();
        };
        var log = new List<string>();
        var provisioner = new Provisioner(Cloud(), Config(), new JsonObject(), log.Add, new FakeClock(Now));
        Assert.Equal(HandleOutcome.Done, await provisioner.HandleAsync(Message("ensure"), 1, Token));
        Assert.Equal(2, version); // provisioning, then ready
        Assert.Equal("ready", Margo.Cloud.Core.RouteTable.Loads(stored).Get(Key)!.State);
        Assert.Equal(["instance 8f6b44ff6f8dd73b5b3a: ensure -> ['route_provisioning', 'deploy', 'route_ready']"], log);
    }

    [Fact]
    public async Task The_queue_is_received_deleted_and_written_as_plain_text()
    {
        const string text = "{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-1\", \"event\": \"agentic<&>\"}";
        _http.Respond = request => request.Method.Method switch
        {
            "GET" => Xml(HttpStatusCode.OK, new XElement("QueueMessagesList",
                new XElement("QueueMessage",
                    new XElement("MessageId", "id-1"),
                    new XElement("InsertionTime", "Sat, 07 Nov 2026 09:30:00 GMT"),
                    new XElement("ExpirationTime", "Sat, 14 Nov 2026 09:30:00 GMT"),
                    new XElement("PopReceipt", "receipt-1"),
                    new XElement("TimeNextVisible", "Sat, 07 Nov 2026 09:45:00 GMT"),
                    new XElement("DequeueCount", "3"),
                    new XElement("MessageText", text)))),
            "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => Xml(HttpStatusCode.Created, new XElement("QueueMessagesList",
                new XElement("QueueMessage",
                    new XElement("MessageId", "id-2"),
                    new XElement("InsertionTime", "Sat, 07 Nov 2026 09:30:00 GMT"),
                    new XElement("ExpirationTime", "Sat, 14 Nov 2026 09:30:00 GMT"),
                    new XElement("PopReceipt", "receipt-2"),
                    new XElement("TimeNextVisible", "Sat, 07 Nov 2026 09:30:00 GMT")))),
        };
        var queue = Queue();
        var message = Assert.Single(await queue.ReceiveAsync(16, TimeSpan.FromSeconds(900), Token));
        Assert.Equal(new QueuedMessage("id-1", "receipt-1", text, 3), message);
        await queue.DeleteAsync(message, Token);
        await queue.SendAsync(text, Token);

        var receive = _http.Requests[0];
        Assert.Equal("GET", receive.Method);
        Assert.StartsWith(QueueUrl + "/messages?", receive.Url, StringComparison.Ordinal);
        Assert.Contains("numofmessages=16", receive.Url, StringComparison.Ordinal);
        Assert.Contains("visibilitytimeout=900", receive.Url, StringComparison.Ordinal);
        Assert.Equal("DELETE " + QueueUrl + "/messages/id-1?popreceipt=receipt-1", _http.Requests[1].Method + " " + _http.Requests[1].Url);
        var send = _http.Requests[2];
        Assert.Equal(("POST", QueueUrl + "/messages"), (send.Method, send.Url));
        Assert.Equal(text, XElement.Parse(send.Body!).Element("MessageText")!.Value); // no Base64, as in Python
    }

    [Fact]
    public async Task An_empty_queue_returns_an_empty_page()
    {
        _http.Respond = _ => Xml(HttpStatusCode.OK, new XElement("QueueMessagesList"));
        Assert.Empty(await Queue().ReceiveAsync(16, TimeSpan.FromSeconds(900), Token));
    }

    [Fact]
    public void The_credential_follows_azure_client_id()
    {
        Assert.IsType<Azure.Identity.ManagedIdentityCredential>(AzureProvisionerServices.CreateCredential(name => name == "AZURE_CLIENT_ID" ? "fictional-client" : null));
        Assert.IsType<Azure.Identity.DefaultAzureCredential>(AzureProvisionerServices.CreateCredential(name => name == "AZURE_CLIENT_ID" ? string.Empty : null));
        Assert.IsType<Azure.Identity.DefaultAzureCredential>(AzureProvisionerServices.CreateCredential(_ => null));
        var services = new AzureProvisionerServices(new FixtureCredential());
        Assert.IsType<AzureCloud>(services.CreateCloud(Subscription, Group, RoutesUrl));
        Assert.IsType<AzureProvisioningQueueClient>(services.CreateQueue(QueueUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-subscription")]
    public async Task A_malformed_subscription_fails_the_step_not_the_job(string subscription)
    {
        // Python sent the request and ARM refused it; the .NET SDK refuses to parse the resource id.
        // Either way the step fails and is recorded, and the job keeps draining the queue.
        _http.Respond = _ => Json(HttpStatusCode.BadRequest, "{\"error\": {\"code\": \"InvalidSubscriptionId\", \"message\": \"fictional\"}}");
        var cloud = new AzureCloud(new FixtureCredential(), subscription, Group, RoutesUrl, new HttpClientTransport(new HttpClient(_http)));
        await Assert.ThrowsAnyAsync<Exception>(() => cloud.DeleteAppAsync("margo-" + Key, Token));
        await Assert.ThrowsAnyAsync<Exception>(() => cloud.DeployAsync("margo-instance-" + Key, new JsonObject(), new JsonObject(), Token));
    }

    private static string Assignment(string id, string principal) =>
        "{\"id\": \"" + id + "\", \"name\": \"" + id[(id.LastIndexOf('/') + 1)..] + "\", \"type\": \"Microsoft.Authorization/roleAssignments\","
        + " \"properties\": {\"principalId\": \"" + principal + "\", \"roleDefinitionId\": \"/providers/Microsoft.Authorization/roleDefinitions/fictional\", \"scope\": \"" + GroupPath + "\"}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Xml(HttpStatusCode status, XElement body) =>
        new(status) { Content = new StringContent(body.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Blob(HttpStatusCode status, string? text, string etag, byte[]? bytes = null)
    {
        var content = new ByteArrayContent(bytes ?? Encoding.UTF8.GetBytes(text!));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        content.Headers.TryAddWithoutValidation("Last-Modified", "Sat, 07 Nov 2026 09:30:00 GMT");
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        return response;
    }

    private static HttpResponseMessage Created()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", "\"0x8DEWRITTEN\"");
        response.Content.Headers.TryAddWithoutValidation("Last-Modified", "Sat, 07 Nov 2026 09:30:00 GMT");
        return response;
    }

    private static HttpResponseMessage StorageError(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>" + code + "</Code><Message>fictional</Message></Error>", Encoding.UTF8, "application/xml"),
        };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }

    /// <summary>A credential that hands out a fixed, fictional token; nothing signs in.</summary>
    private sealed class FixtureCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fixture-access", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

/// <summary>Records every request and answers with <see cref="Respond"/>; nothing reaches the network.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method.Method, Uri.UnescapeDataString(request.RequestUri!.GetLeftPart(UriPartial.Path)) + request.RequestUri.Query, body, headers));
        var response = Respond(request);
        response.RequestMessage = request;
        return response;
    }
}

internal sealed record RecordedRequest(string Method, string Url, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Authorization => Header("Authorization");

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

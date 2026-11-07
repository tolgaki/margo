using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine.Tests;

/// <summary>
/// The Responses API client over HttpClient: what <c>host/app.py</c> did with <c>openai.AsyncOpenAI</c> on the Azure AI
/// Services v1 endpoint with an Entra bearer token provider. A fake HTTP handler and a fake credential; no network.
/// </summary>
public sealed class ModelClientTests
{
    private const string Endpoint = "https://fictional-account.openai.azure.example.com/";

    [Fact]
    public void Base_url_is_the_openai_v1_api_of_the_endpoint()
    {
        Assert.Equal("https://a.example.com/openai/v1/", AzureOpenAIModelClient.ModelBaseUrl("https://a.example.com///"));
        Assert.Equal("https://a.example.com/openai/v1/", AzureOpenAIModelClient.ModelBaseUrl("https://a.example.com"));
        var error = Assert.Throws<InvalidOperationException>(() => AzureOpenAIModelClient.ModelBaseUrl(""));
        Assert.Equal("AZURE_OPENAI_ENDPOINT is required (https://ACCOUNT.openai.azure.com)", error.Message);
        Assert.Throws<InvalidOperationException>(() => AzureOpenAIModelClient.ModelBaseUrl(null));
        Assert.Equal("https://cognitiveservices.azure.com/.default", AzureOpenAIModelClient.Scope);
    }

    [Fact]
    public void The_host_credential_is_the_instance_identity_and_needs_no_key()
    {
        Assert.IsType<Azure.Identity.ManagedIdentityCredential>(AzureOpenAIModelClient.InstanceCredential("fictional-client-id"));
        Assert.IsType<Azure.Identity.DefaultAzureCredential>(AzureOpenAIModelClient.InstanceCredential(""));
        var saved = Environment.GetEnvironmentVariable(AzureOpenAIModelClient.EndpointVariable);
        try
        {
            using var http = new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("no network in this test")));
            Environment.SetEnvironmentVariable(AzureOpenAIModelClient.EndpointVariable, null);
            Assert.Throws<InvalidOperationException>(() => AzureOpenAIModelClient.FromEnvironment(http, new FakeCredential()));
            Environment.SetEnvironmentVariable(AzureOpenAIModelClient.EndpointVariable, "https://fictional.openai.azure.example.com");
            using var client = AzureOpenAIModelClient.FromEnvironment(http, new FakeCredential());
            Assert.Equal("https://fictional.openai.azure.example.com/openai/v1/", client.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureOpenAIModelClient.EndpointVariable, saved);
        }
    }

    [Fact]
    public async Task A_request_is_posted_with_an_entra_token_and_the_answer_returned()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"id": "r1", "output": []}"""));
        var credential = new FakeCredential();
        using var http = new HttpClient(handler);
        using var client = new AzureOpenAIModelClient(http, Endpoint, credential);
        var request = new JsonObject { ["model"] = "fictional-model", ["instructions"] = "café", ["input"] = "Hi", ["previous_response_id"] = null };
        var response = await client.CreateResponseAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("""{"id": "r1", "output": []}""", Fixtures.Dumps(response));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://fictional-account.openai.azure.example.com/openai/v1/responses", sent.Uri);
        Assert.Equal("Bearer fictional-token-1", sent.Authorization);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("{\"model\":\"fictional-model\",\"instructions\":\"café\",\"input\":\"Hi\",\"previous_response_id\":null}", sent.Body);
        Assert.StartsWith("margo-cloud-retry-", sent.IdempotencyKey, StringComparison.Ordinal);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credential.Scopes.Single());

        await client.CreateResponseAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(credential.Scopes); // a token valid for more than five minutes is reused
        Assert.NotEqual(handler.Requests[0].IdempotencyKey, handler.Requests[1].IdempotencyKey);
    }

    [Fact]
    public async Task A_token_close_to_expiry_is_refreshed()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var credential = new FakeCredential(lifetime: TimeSpan.FromMinutes(4));
        using var http = new HttpClient(handler);
        using var client = new AzureOpenAIModelClient(http, Endpoint, credential);
        await client.CreateResponseAsync([], TestContext.Current.CancellationToken);
        await client.CreateResponseAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(2, credential.Scopes.Count);
        Assert.Equal(["Bearer fictional-token-1", "Bearer fictional-token-2"], handler.Requests.Select(r => r.Authorization).ToArray());
    }

    [Fact]
    public async Task Retryable_answers_are_retried_twice_with_the_same_idempotency_key()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.OK]);
        var handler = new FakeHandler((_, _) => Json(statuses.Dequeue(), """{"id": "r2"}"""));
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(handler);
        using var client = new AzureOpenAIModelClient(http, Endpoint, new FakeCredential(), Options(delays));
        var response = await client.CreateResponseAsync([], TestContext.Current.CancellationToken);
        Assert.Equal("r2", PyNode.AsString(response!["id"]));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(r => r.IdempotencyKey).Distinct());
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.0)], delays); // jitter 0 in the test
    }

    [Fact]
    public async Task Errors_after_the_retries_carry_the_status_and_python_message()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.ServiceUnavailable, """{"error": {"code": "busy", "message": "fictional"}}"""));
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(handler);
        using var client = new AzureOpenAIModelClient(http, Endpoint, new FakeCredential(), Options(delays));
        var error = await Assert.ThrowsAsync<ModelApiException>(() => client.CreateResponseAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("Error code: 503 - {'error': {'code': 'busy', 'message': 'fictional'}}", error.Message);
        Assert.Equal(3, handler.Requests.Count);

        var bad = new FakeHandler((_, _) => Json(HttpStatusCode.BadRequest, "not json"));
        using var badHttp = new HttpClient(bad);
        using var badClient = new AzureOpenAIModelClient(badHttp, Endpoint, new FakeCredential(), Options(delays));
        var rejected = await Assert.ThrowsAsync<ModelApiException>(() => badClient.CreateResponseAsync([], TestContext.Current.CancellationToken));
        Assert.Equal("not json", rejected.Message);
        Assert.Single(bad.Requests); // 400 is not retried

        var empty = new FakeHandler((_, _) => Json(HttpStatusCode.Unauthorized, ""));
        using var emptyHttp = new HttpClient(empty);
        using var emptyClient = new AzureOpenAIModelClient(emptyHttp, Endpoint, new FakeCredential(), Options(delays));
        Assert.Equal("Error code: 401", (await Assert.ThrowsAsync<ModelApiException>(() => emptyClient.CreateResponseAsync([], TestContext.Current.CancellationToken))).Message);
    }

    [Fact]
    public async Task Retry_headers_are_honoured_as_the_sdk_does()
    {
        var answers = new Queue<Func<HttpResponseMessage>>([
            () => WithHeader(Json(HttpStatusCode.TooManyRequests, "{}"), "retry-after-ms", "1500"),
            () => WithHeader(Json(HttpStatusCode.Conflict, "{}"), "retry-after", "2"),
            () => Json(HttpStatusCode.OK, "{}"),
        ]);
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(new FakeHandler((_, _) => answers.Dequeue()()));
        using var client = new AzureOpenAIModelClient(http, Endpoint, new FakeCredential(), Options(delays));
        await client.CreateResponseAsync([], TestContext.Current.CancellationToken);
        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2)], delays);

        var never = new FakeHandler((_, _) => WithHeader(Json(HttpStatusCode.TooManyRequests, "{}"), "retry-after", "3600"));
        using var neverHttp = new HttpClient(never);
        using var neverClient = new AzureOpenAIModelClient(neverHttp, Endpoint, new FakeCredential(), Options(delays));
        await Assert.ThrowsAsync<ModelApiException>(() => neverClient.CreateResponseAsync([], TestContext.Current.CancellationToken));
        Assert.Single(never.Requests); // a retry-after above 60 seconds is not waited for

        var told = new FakeHandler((_, _) => WithHeader(Json(HttpStatusCode.BadRequest, "{}"), "x-should-retry", "true"));
        using var toldHttp = new HttpClient(told);
        using var toldClient = new AzureOpenAIModelClient(toldHttp, Endpoint, new FakeCredential(), Options(delays));
        await Assert.ThrowsAsync<ModelApiException>(() => toldClient.CreateResponseAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(3, told.Requests.Count);
    }

    [Fact]
    public async Task Connection_failures_and_timeouts_are_retried_then_typed()
    {
        var delays = new List<TimeSpan>();
        var unreachable = new FakeHandler((_, _) => throw new HttpRequestException("fictional connection refused"));
        using var http = new HttpClient(unreachable);
        using var client = new AzureOpenAIModelClient(http, Endpoint, new FakeCredential(), Options(delays));
        var error = await Assert.ThrowsAsync<ModelConnectionException>(() => client.CreateResponseAsync([], TestContext.Current.CancellationToken));
        Assert.Equal("Connection error.", error.Message);
        Assert.Equal(3, unreachable.Requests.Count);

        var slow = new FakeHandler((_, token) =>
        {
            Task.Delay(Timeout.Infinite, token).GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, "{}");
        });
        using var slowHttp = new HttpClient(slow);
        using var slowClient = new AzureOpenAIModelClient(slowHttp, Endpoint, new FakeCredential(),
                                                          Options(delays) with { Timeout = TimeSpan.FromMilliseconds(50), MaxRetries = 1 });
        Assert.Equal("Request timed out.", (await Assert.ThrowsAsync<ModelTimeoutException>(() => slowClient.CreateResponseAsync([], TestContext.Current.CancellationToken))).Message);
        Assert.Equal(2, slow.Requests.Count);

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slowClient.CreateResponseAsync([], cancel.Token));
    }

    [Fact]
    public async Task The_engine_runs_on_the_http_client()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK,
            """{"id": "r-final", "output": [{"type": "message", "content": [{"type": "output_text", "text": "Hello from a fictional model."}]}]}"""));
        using var http = new HttpClient(handler);
        using var client = new AzureOpenAIModelClient(http, Endpoint, new FakeCredential());
        var engine = new Engine(client, "fictional-model", Fixtures.Root);
        var guard = new Guard.WorkIQGuard(Fixtures.Binding, new FakeTokens(), new RecordingTransport());
        var result = await engine.RunAsync("Hi", Guard.RunModes.Attended, guard, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("Hello from a fictional model.", "r-final"), (result.Text, result.ResponseId));
        var body = (JsonObject)PyJson.Loads(handler.Requests[0].Body)!;
        Assert.Equal(["model", "instructions", "input", "tools"], body.Select(pair => pair.Key).ToArray());
    }

    private static ModelClientOptions Options(List<TimeSpan> delays) => new()
    {
        Delay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        },
        Random = () => 0,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage WithHeader(HttpResponseMessage response, string name, string value)
    {
        response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private sealed record SentRequest(HttpMethod Method, string Uri, string? Authorization, string? ContentType, string Body, string? IdempotencyKey);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                                         request.Content?.Headers.ContentType?.MediaType, body,
                                         request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null));
            return await Task.Run(() => answer(request, cancellationToken), cancellationToken);
        }
    }

    private sealed class FakeCredential(TimeSpan? lifetime = null) : TokenCredential
    {
        public List<string[]> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.Add(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken("fictional-token-" + Scopes.Count, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(1))));
        }
    }
}

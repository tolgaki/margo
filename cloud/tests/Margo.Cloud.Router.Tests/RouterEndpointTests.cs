using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;
using Margo.Cloud.Router.Tests.TestSupport;
using Xunit;

namespace Margo.Cloud.Router.Tests;

/// <summary>
/// The router's HTTP endpoints. Expected statuses, headers and bodies were observed by driving the
/// Python router (<c>build_app</c>) with raw HTTP requests, a fake instance and a local key set,
/// except where a comment says the port deliberately differs (see PORTING.md).
/// </summary>
public sealed class RouterEndpointTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static byte[] Activity(string user = RouterUnderTest.User, Action<JsonObject>? change = null)
    {
        var value = new JsonObject
        {
            ["type"] = "message",
            ["recipient"] = new JsonObject { ["agenticAppId"] = RouterUnderTest.Identity, ["agenticUserId"] = user },
            ["from"] = new JsonObject { ["aadObjectId"] = "principal-1" },
            ["text"] = "Brief me",
        };
        change?.Invoke(value);
        return Encoding.UTF8.GetBytes(value.ToJsonString());
    }

    private static HttpRequestMessage Post(string path, byte[] body, string? token, string contentType = "application/json; charset=utf-8")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", token);
        }

        return request;
    }

    private static async Task<(int Status, string? ContentType, string Body, HttpResponseMessage Response)> Send(RouterUnderTest router, HttpRequestMessage request)
    {
        var response = await router.Client.SendAsync(request, Cancel);
        var body = await response.Content.ReadAsStringAsync(Cancel);
        return ((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), body, response);
    }

    [Fact]
    public async Task Activities_need_a_valid_token_on_both_paths_and_any_method()
    {
        await using var router = await RouterUnderTest.StartAsync();
        var missing = await Send(router, Post("/api/messages", Activity(), null));
        Assert.Equal((401, "application/json; charset=utf-8", "{\"error\": \"Authorization header not found\"}"), (missing.Status, missing.ContentType, missing.Body));
        var bad = await Send(router, Post("/activity/messages", Activity(), "Bearer not-a-jwt"));
        Assert.Equal((401, "{\"error\": \"Invalid token or authentication failed.\"}"), (bad.Status, bad.Body));

        // aiohttp ran the SDK middleware before the method check.
        var get = await Send(router, new HttpRequestMessage(HttpMethod.Get, "/api/messages"));
        Assert.Equal(401, get.Status);
        var getWithToken = new HttpRequestMessage(HttpMethod.Get, "/api/messages");
        getWithToken.Headers.TryAddWithoutValidation("Authorization", "Bearer " + router.Token());
        var notAllowed = await Send(router, getWithToken);
        Assert.Equal((405, "text/plain; charset=utf-8", "405: Method Not Allowed", "POST"),
            (notAllowed.Status, notAllowed.ContentType, notAllowed.Body, string.Join(",", notAllowed.Response.Content.Headers.Allow)));
        Assert.Empty(router.Sink.Sent);
        Assert.Equal(0, router.Routes.Loads);
    }

    [Fact]
    public async Task Ready_instances_get_the_activity_with_its_token_and_content_type_only()
    {
        await using var instance = await FakeInstance.StartAsync();
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table((RouterUnderTest.User, RouteStates.Ready, instance.Url)));
        var token = router.Token();
        var request = Post("/api/messages", Activity(), "Bearer " + token);
        request.Headers.TryAddWithoutValidation("X-Never-Forwarded", "1");
        request.Headers.TryAddWithoutValidation("Cookie", "c=d");
        var result = await Send(router, request);

        // aiohttp passed on the media type only: no charset.
        Assert.Equal((200, "application/json", "{\"ok\": true}"), (result.Status, result.ContentType, result.Body));
        var seen = Assert.Single(instance.Seen);
        Assert.Equal(("POST", "/activity/messages", "Bearer " + token, "application/json; charset=utf-8", Activity().Length),
            (seen.Method, seen.PathAndQuery, seen.Authorization, seen.ContentType, seen.BodyLength));
        Assert.DoesNotContain("Cookie", seen.HeaderNames);
        Assert.DoesNotContain("X-Never-Forwarded", seen.HeaderNames);
        Assert.Contains("Authorization", seen.HeaderNames);
        Assert.Empty(router.Sink.Sent);
    }

    [Fact]
    public async Task Instance_responses_keep_status_and_body_but_no_other_headers()
    {
        await using var instance = await FakeInstance.StartAsync();
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table((RouterUnderTest.User, RouteStates.Ready, instance.Url)));
        var token = "Bearer " + router.Token();
        var empty = await Send(router, Post("/api/messages", Activity(change: a => a["text"] = "empty"), token));
        Assert.Equal((202, null, string.Empty), (empty.Status, empty.ContentType, empty.Body));
        var noContent = await Send(router, Post("/api/messages", Activity(change: a => a["text"] = "nocontent"), token)); // aiohttp: no body for 204
        Assert.Equal((204, null, string.Empty), (noContent.Status, noContent.ContentType, noContent.Body));
        var plain = await Send(router, Post("/api/messages", Activity(change: a => a["text"] = "plain"), token));
        Assert.Equal((201, "text/plain", "hi"), (plain.Status, plain.ContentType, plain.Body));
        Assert.False(plain.Response.Headers.Contains("X-Extra"));
        Assert.Null(plain.Response.Headers.RetryAfter);
        var untyped = await Send(router, Post("/api/messages", Activity(change: a => a["text"] = "untyped"), token));
        Assert.Equal((200, "application/octet-stream", "raw"), (untyped.Status, untyped.ContentType, untyped.Body));
        var gzip = await Send(router, Post("/api/messages", Activity(change: a => a["text"] = "gzip"), token));
        Assert.Equal((200, "application/json", "{\"compressed\": true}"), (gzip.Status, gzip.ContentType, gzip.Body));
        Assert.Empty(gzip.Response.Content.Headers.ContentEncoding);

        // Without a Content-Type from the caller, the bytes went as application/octet-stream.
        var noType = new HttpRequestMessage(HttpMethod.Post, "/api/messages") { Content = new ByteArrayContent(Activity()) };
        noType.Headers.TryAddWithoutValidation("Authorization", token);
        Assert.Equal(200, (await Send(router, noType)).Status);
        Assert.Equal("application/octet-stream", instance.Seen.Last().ContentType);
    }

    [Fact]
    public async Task Unknown_failed_waiting_and_stopped_instances()
    {
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table(
            ("margo-user-p", RouteStates.Provisioning, null), ("margo-user-s", RouteStates.Suspended, null), ("margo-user-f", RouteStates.Failed, null)));
        var token = "Bearer " + router.Token();

        var unknown = await Send(router, Post("/api/messages", Activity("margo-user-new"), token));
        Assert.Equal((503, "30", string.Empty, null), (unknown.Status, unknown.Response.Headers.RetryAfter?.ToString(), unknown.Body, unknown.ContentType));
        Assert.Equal(["{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-new\", \"event\": \"first-activity\"}"],
            router.Sink.Sent);

        router.Sink.Sent.Clear();
        var failed = await Send(router, Post("/api/messages", Activity("margo-user-f"), token));
        Assert.Equal((503, "30"), (failed.Status, failed.Response.Headers.RetryAfter?.ToString()));
        Assert.Equal(["{\"action\": \"ensure\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-f\", \"event\": \"retry-after-failure\"}"],
            router.Sink.Sent);

        router.Sink.Sent.Clear();
        var waiting = await Send(router, Post("/api/messages", Activity("margo-user-p"), token));
        Assert.Equal((503, "30"), (waiting.Status, waiting.Response.Headers.RetryAfter?.ToString()));
        var stopped = await Send(router, Post("/api/messages", Activity("margo-user-s"), token));
        Assert.Equal((200, string.Empty), (stopped.Status, stopped.Body));
        Assert.Null(stopped.Response.Headers.RetryAfter);
        Assert.Empty(router.Sink.Sent);
    }

    [Fact]
    public async Task Lifecycle_notifications_become_provisioning_messages()
    {
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table((RouterUnderTest.User, RouteStates.Ready, "https://margo-x.internal.example.com")));
        var result = await Send(router, Post("/api/messages", Activity(change: a =>
        {
            a["type"] = "event";
            a["name"] = "AgentLifecycle";
            a["valueType"] = "agenticUserDisabled";
        }), "Bearer " + router.Token()));
        Assert.Equal((200, string.Empty), (result.Status, result.Body));
        Assert.Equal(["{\"action\": \"suspend\", \"agent_identity_id\": \"agent-identity-1\", \"agent_user_id\": \"margo-user-1\", \"event\": \"agenticuserdisabled\"}"],
            router.Sink.Sent);
    }

    [Fact]
    public async Task Malformed_bodies()
    {
        await using var router = await RouterUnderTest.StartAsync();
        var token = "Bearer " + router.Token();
        foreach (var body in (string[])["{nope", "[1]", "{\"x\": NaN, \"recipient\": {}}", ""])
        {
            Assert.Equal(400, (await Send(router, Post("/api/messages", Encoding.UTF8.GetBytes(body), token))).Status);
        }

        Assert.Equal(400, (await Send(router, Post("/api/messages", Activity(change: a => a["recipient"] = new JsonObject()), token))).Status);
        Assert.Equal(400, (await Send(router, Post("/api/messages", Activity(change: a =>
            a["recipient"] = new JsonObject { ["agenticAppId"] = "x", ["agenticUserId"] = "x" }), token))).Status);
        Assert.Empty(router.Sink.Sent);
    }

    [Fact]
    public async Task Bodies_are_decoded_as_python_json_loads_decodes_bytes()
    {
        await using var router = await RouterUnderTest.StartAsync();
        var token = "Bearer " + router.Token();
        var activity = Activity("margo-user-new");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(activity).ToArray();
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(activity))).ToArray();
        Assert.Equal(503, (await Send(router, Post("/api/messages", bom, token))).Status);
        Assert.Equal(503, (await Send(router, Post("/api/messages", utf16, token))).Status);
        Assert.Equal(2, router.Sink.Sent.Count);
    }

    [Fact]
    public async Task A_lone_surrogate_in_the_text_is_routed_and_one_in_the_recipient_fails_as_in_python()
    {
        await using var router = await RouterUnderTest.StartAsync();
        var token = "Bearer " + router.Token();
        var text = Encoding.UTF8.GetString(Activity("margo-user-new")).Replace("Brief me", "Brief me \\ud83d", StringComparison.Ordinal);
        Assert.Equal(503, (await Send(router, Post("/api/messages", Encoding.UTF8.GetBytes(text), token))).Status);
        var recipient = Encoding.UTF8.GetString(Activity("margo-user-new")).Replace("margo-user-new", "margo-user-\\udc00", StringComparison.Ordinal);
        var failure = await Send(router, Post("/api/messages", Encoding.UTF8.GetBytes(recipient), token));
        Assert.Equal((500, "500 Internal Server Error\n\nServer got itself in trouble"), (failure.Status, failure.Body));
        Assert.Single(router.Sink.Sent);
    }

    [Fact]
    public async Task The_size_limit_is_one_mebibyte()
    {
        await using var router = await RouterUnderTest.StartAsync();
        var token = "Bearer " + router.Token();
        var activity = Activity("margo-user-new");
        var padded = new byte[RouterCore.MaxActivityBytes];
        Array.Fill(padded, (byte)' ');
        activity.CopyTo(padded, 0);
        // Python read the body with StreamReader.read(limit + 1), which returns what is buffered, so a
        // large body arrived cut short and was answered 400; this port reads up to the limit (PORTING.md).
        Assert.Equal(503, (await Send(router, Post("/api/messages", padded, token))).Status); // exactly the limit: read and routed
        var tooBig = new byte[RouterCore.MaxActivityBytes + 1];
        padded.CopyTo(tooBig, 0);
        tooBig[^1] = (byte)' ';
        var result = await Send(router, Post("/api/messages", tooBig, token));
        Assert.Equal((413, string.Empty), (result.Status, result.Body));
        Assert.Single(router.Sink.Sent);

        // Without a valid token the size does not matter.
        Assert.Equal(401, (await Send(router, Post("/api/messages", tooBig, "Bearer x"))).Status);
    }

    [Fact]
    public async Task Health_and_unknown_paths()
    {
        await using var router = await RouterUnderTest.StartAsync();
        foreach (var path in (string[])["/", "/liveness", "/readiness"])
        {
            var health = await Send(router, new HttpRequestMessage(HttpMethod.Get, path));
            Assert.Equal((200, "application/json; charset=utf-8", "{\"status\": \"ok\"}"), (health.Status, health.ContentType, health.Body));
        }

        var head = await Send(router, new HttpRequestMessage(HttpMethod.Head, "/liveness"));
        Assert.Equal((200, string.Empty, 16L), (head.Status, head.Body, head.Response.Content.Headers.ContentLength));
        var post = await Send(router, new HttpRequestMessage(HttpMethod.Post, "/liveness"));
        Assert.Equal((405, "GET,HEAD"), (post.Status, string.Join(",", post.Response.Content.Headers.Allow)));
        foreach (var path in (string[])["/nope", "/liveness/", "/api/messages/", "/i/zz/voice/"])
        {
            var missing = await Send(router, new HttpRequestMessage(HttpMethod.Get, path));
            Assert.Equal((404, "text/plain; charset=utf-8", "404: Not Found"), (missing.Status, missing.ContentType, missing.Body));
        }

        Assert.Equal(0, router.Routes.Loads);
    }

    [Fact]
    public async Task The_route_table_is_cached_for_fifteen_seconds_and_the_last_good_one_is_kept()
    {
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table(("margo-user-s", RouteStates.Suspended, null)));
        var token = "Bearer " + router.Token();
        async Task<int> Status() => (await Send(router, Post("/api/messages", Activity("margo-user-s"), token))).Status;

        Assert.Equal(200, await Status());
        Assert.Equal(1, router.Routes.Loads);
        router.Routes.Text = string.Empty; // no table any more
        router.Time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(200, await Status()); // 15 s is still fresh
        Assert.Equal(1, router.Routes.Loads);

        router.Routes.Failure = new InvalidOperationException("storage is unreachable");
        router.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(200, await Status()); // the load failed: the last good table is used
        Assert.Equal(200, await Status()); // and every request tries again
        Assert.Equal(3, router.Routes.Loads);
        Assert.Contains(router.Logs.Lines, line => line.Contains("route table could not be loaded", StringComparison.Ordinal));

        router.Routes.Failure = null;
        Assert.Equal(503, await Status()); // the empty table: provision
        Assert.Equal(4, router.Routes.Loads);
        router.Routes.Text = "not json";
        router.Time.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(503, await Status()); // a malformed table is a failed load too
        Assert.Equal(5, router.Routes.Loads);
    }

    [Fact]
    public async Task Unreachable_instances_fail_with_500()
    {
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table((RouterUnderTest.User, RouteStates.Ready, "http://127.0.0.1:9")));
        var result = await Send(router, Post("/api/messages", Activity(), "Bearer " + router.Token()));
        Assert.Equal((500, "text/plain; charset=utf-8", "500 Internal Server Error\n\nServer got itself in trouble"), (result.Status, result.ContentType, result.Body));
    }

    [Fact]
    public async Task Bodies_and_tokens_are_never_logged()
    {
        await using var instance = await FakeInstance.StartAsync();
        await using var router = await RouterUnderTest.StartAsync(RouterUnderTest.Table((RouterUnderTest.User, RouteStates.Ready, instance.Url)));
        var token = router.Token();
        Assert.Equal(200, (await Send(router, Post("/api/messages", Activity(change: a => a["text"] = "fictional secret words"), "Bearer " + token))).Status);
        Assert.Equal(401, (await Send(router, Post("/api/messages", Activity(), "Bearer " + token + "x"))).Status);
        Assert.Contains(router.Logs.Lines, line => line.EndsWith("activity kind=forward key=" + RouterUnderTest.Key(), StringComparison.Ordinal));
        Assert.All(router.Logs.Lines, line =>
        {
            Assert.DoesNotContain("fictional secret words", line, StringComparison.Ordinal);
            Assert.DoesNotContain(token[..20], line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_rejected_activity_is_logged_with_key_none()
    {
        await using var router = await RouterUnderTest.StartAsync();
        Assert.Equal(400, (await Send(router, Post("/api/messages", Activity(change: a => a["recipient"] = new JsonObject()), "Bearer " + router.Token()))).Status);
        Assert.Contains(router.Logs.Lines, line => line.EndsWith("activity kind=reject key=None", StringComparison.Ordinal));
    }

    [Fact]
    public void Media_types_are_read_as_aiohttp_reads_them()
    {
        // aiohttp.helpers.parse_content_type on Python 3.13.
        Assert.Equal("application/json", RouterService.MediaType("application/json; charset=utf-8"));
        Assert.Equal("text/html", RouterService.MediaType("Text/HTML"));
        Assert.Equal("application/octet-stream", RouterService.MediaType("garbage"));
        Assert.Equal("application/octet-stream", RouterService.MediaType("a/b/c"));
        Assert.Equal("application/octet-stream", RouterService.MediaType(string.Empty));
        Assert.Equal("application/json", RouterService.MediaType("  application/json  ;x=1"));
        Assert.Equal("x/y z", RouterService.MediaType("x/y z"));
        Assert.Equal("application/octet-stream", RouterService.MediaType(null));
    }
}

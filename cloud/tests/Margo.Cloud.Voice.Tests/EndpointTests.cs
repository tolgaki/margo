using System.Net;
using Margo.Cloud.Orchestrator;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Voice.Tests;

/// <summary><c>mount(app, service)</c>, <c>page</c> and <c>config</c> of <c>voice/server.py</c>, and the constructor's checks.</summary>
public sealed class EndpointTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static string Static(string file) => Path.Combine(Fixtures.Root, "autopilot", "src", "margo_autopilot", "voice", "static", file);

    [Fact]
    public async Task The_page_files_are_served_unchanged_with_their_headers()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);
        using var client = host.Client();
        foreach (var (name, (file, contentType)) in VoiceEndpoints.Files)
        {
            using var response = await client.GetAsync("/voice/" + name, Cancel);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, response.Content.Headers.ContentType!.ToString());
            Assert.Equal(VoiceEndpoints.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal(await File.ReadAllBytesAsync(Static(file), Cancel), await response.Content.ReadAsByteArrayAsync(Cancel));
        }

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/voice/voice.js"), Cancel);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(new FileInfo(Static("voice.js")).Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Cancel));
    }

    [Fact]
    public void The_python_page_lists_the_same_files_and_policy()
    {
        var python = File.ReadAllText(Path.Combine(Fixtures.Root, "autopilot", "src", "margo_autopilot", "voice", "server.py"));
        foreach (var (name, (file, contentType)) in VoiceEndpoints.Files)
        {
            Assert.Contains($"\"{name}\": (\"{file}\", \"{contentType}\")", python, StringComparison.Ordinal);
        }

        Assert.Equal(VoiceEndpoints.Files.Count, python.Split("\": (\"").Length - 1);
        var csp = python[(python.IndexOf("CSP = (", StringComparison.Ordinal) + 7)..];
        csp = csp[..csp.IndexOf(")\n", StringComparison.Ordinal)];
        Assert.Equal(VoiceEndpoints.ContentSecurityPolicy, string.Concat(csp.Split('"').Where((_, index) => index % 2 == 1)));
        Assert.Contains("MAX_FRAME = 256 * 1024", python, StringComparison.Ordinal);
        Assert.Contains("VOICE_CALL_BUDGET, VOICE_STATE_BUDGET = 120, 60", python, StringComparison.Ordinal);
        Assert.Contains("HELLO_SECONDS = 15", python, StringComparison.Ordinal);
        Assert.Contains("heartbeat=20", python, StringComparison.Ordinal);
        Assert.Contains("\"" + VoiceService.SignInRefused + "\"", python, StringComparison.Ordinal);
        Assert.Contains("\"" + VoiceService.SessionFailed + "\"", python, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_config_and_refusals()
    {
        using var state = new TempState();
        await using var host = await VoiceHost.StartAsync(state);
        using var client = host.Client();

        using var redirect = await client.GetAsync("/voice", Cancel);
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("voice/", redirect.Headers.Location!.OriginalString);

        using var config = await client.GetAsync("/voice/config", Cancel);
        Assert.Equal("application/json; charset=utf-8", config.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", config.Headers.CacheControl!.ToString());
        Assert.Equal("""{"tenantId": "fictional-tenant", "clientId": "fictional-blueprint-app", "scope": "api://fictional-blueprint-app/Voice.Talk"}""",
                     await config.Content.ReadAsStringAsync(Cancel));

        using var unknown = await client.GetAsync("/voice/README.md", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(await unknown.Content.ReadAsByteArrayAsync(Cancel));
        foreach (var path in new[] { "/voice/pcm-worklets.test.mjs", "/voice/..", "/voice/%2E%2E" })
        {
            using var response = await client.GetAsync(path, Cancel);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var nested = await client.GetAsync("/voice/static/index.html", Cancel);
        Assert.Equal(HttpStatusCode.NotFound, nested.StatusCode);
        Assert.Equal("404: Not Found", await nested.Content.ReadAsStringAsync(Cancel));

        using var post = await client.PostAsync("/voice/config", new StringContent("{}"), Cancel);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal("GET,HEAD", string.Join(",", post.Content.Headers.Allow));

        using var plain = await client.GetAsync("/voice/ws", Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal("No WebSocket UPGRADE hdr: None\n Can \"Upgrade\" only to \"WebSocket\".", await plain.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public void The_endpoint_and_settings_are_required_at_start_up()
    {
        using var state = new TempState();
        var engine = new Margo.Cloud.Engine.Engine(new UnusedModel(), "fictional-model", Fixtures.Root);
        var orchestrator = new Margo.Cloud.Orchestrator.Orchestrator(engine, new DirectoryTransport(), state.Path, _ => new InMemoryLeaseStore());
        var error = Assert.Throws<InvalidOperationException>(() =>
            new VoiceService(orchestrator, new FakeTokens(), Fixtures.Instance, Fixtures.Tenant, Fixtures.Client,
                             new VoiceServiceOptions { Settings = new VoiceSettings("m"), Endpoint = "" }));
        Assert.Equal("VOICE_LIVE_ENDPOINT is required for voice", error.Message);

        using (PyRuntime.Override(new Dictionary<string, string> { ["VOICE_LIVE_ENDPOINT"] = "https://voice.example.com/" }))
        {
            Assert.Equal("VOICE_LIVE_MODEL is required for voice", Assert.Throws<PyValueError>(() =>
                new VoiceService(orchestrator, new FakeTokens(), Fixtures.Instance, Fixtures.Tenant, Fixtures.Client)).Message);
        }

        using (PyRuntime.Override(new Dictionary<string, string>
        {
            ["VOICE_LIVE_ENDPOINT"] = "https://voice.example.com/",
            ["VOICE_LIVE_MODEL"] = "fictional-realtime",
            ["MARGO_VOICE_MAX_TOOL_CALLS"] = "5",
        }))
        {
            using var service = new VoiceService(orchestrator, new FakeTokens(), Fixtures.Instance, Fixtures.Tenant, Fixtures.Client,
                                                 new VoiceServiceOptions { Scope = "Voice.Custom" });
            Assert.Equal("https://voice.example.com/", service.Endpoint);
            Assert.Equal(new VoiceSettings("fictional-realtime", MaxToolCalls: 5), service.Settings);
            Assert.Equal("api://fictional-blueprint-app/Voice.Custom", PyNode.AsString(service.Config()["scope"]));
        }
    }

    [Fact]
    public void The_embedded_files_are_the_static_folder_s()
    {
        foreach (var (file, _) in VoiceEndpoints.Files.Values)
        {
            Assert.Equal(File.ReadAllBytes(Static(file)), VoiceEndpoints.FileContents(file));
        }

    }
}

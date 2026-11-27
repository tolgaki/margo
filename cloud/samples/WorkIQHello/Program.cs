// Work IQ from a custom agent, in one file: sign in as a user, load Work IQ as an MCP server, list its tools, read
// today's calendar (fetch), and search with citations (retrieve). Run: see README.md.
using System.Globalization;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

const string Endpoint = "https://workiq.svc.cloud.microsoft/mcp";
const string Scope = "api://workiq.svc.cloud.microsoft/.default";

var clientId = Environment.GetEnvironmentVariable("WORKIQ_CLIENT_ID");
var tenantId = Environment.GetEnvironmentVariable("WORKIQ_TENANT_ID");
if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId))
{
    Console.Error.WriteLine("Set WORKIQ_CLIENT_ID (an app registration with Work IQ's delegated permissions) and WORKIQ_TENANT_ID.");
    return 2;
}

// 1. A delegated token for Work IQ, as the signed-in user. Work IQ answers as the token's subject.
var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
{
    ClientId = clientId,
    TenantId = tenantId,
    DeviceCodeCallback = (code, _) => { Console.WriteLine(code.Message); return Task.CompletedTask; },
});
var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), default);

// 2. Load Work IQ as an MCP server (streamable HTTP), one session for this identity.
await using var client = await McpClient.CreateAsync(
    new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(Endpoint),
        TransportMode = HttpTransportMode.StreamableHttp,
        AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.Token },
    }),
    new McpClientOptions { ProtocolVersion = "2025-06-18" });

// 3. The server describes its own tools. A custom agent hands these descriptions and schemas to its model.
var tools = await client.ListToolsAsync();
Console.WriteLine($"Work IQ offers {tools.Count} tools:");
foreach (var tool in tools)
{
    var summary = (tool.Description ?? "").Split('\n')[0];
    Console.WriteLine($"  {tool.Name,-16} {(summary.Length > 90 ? summary[..90] + "..." : summary)}");
}

// 4. A literal, bounded read: today's meetings. $select and $top keep the payload small; calendarView needs a window.
var today = DateTimeOffset.Now.Date;
var path = "/me/calendarView?startDateTime=" + today.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
           + "&endDateTime=" + today.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
           + "&$select=subject,start,end,webLink&$orderby=start/dateTime&$top=10";
Print("Today's meetings (fetch)", await client.CallToolAsync("fetch", new Dictionary<string, object?> { ["entityUrls"] = new[] { path } }));

// 5. A semantic search with citations: retrieve returns ranked hits with links (and sensitivity labels).
Print("Recent project updates (retrieve)", await client.CallToolAsync("retrieve", new Dictionary<string, object?> { ["query"] = new[] { "recent project updates" } }));
return 0;

// Tool results are content blocks; Work IQ puts its JSON in text blocks. isError marks a failed call.
static void Print(string title, CallToolResult result)
{
    Console.WriteLine();
    Console.WriteLine(title + (result.IsError == true ? " -> failed" : ""));
    foreach (var block in result.Content.OfType<TextContentBlock>())
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(JsonDocument.Parse(block.Text).RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException)
        {
            Console.WriteLine(block.Text);
        }
    }
}

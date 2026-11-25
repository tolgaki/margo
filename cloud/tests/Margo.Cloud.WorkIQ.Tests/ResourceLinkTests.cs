using System.Text.Json.Nodes;
using Xunit;

namespace Margo.Cloud.WorkIQ.Tests;

/// <summary>Tools that create something answer with a <c>resource_link</c> item, which the result keeps (Python dropped it).</summary>
public sealed class ResourceLinkTests
{
    [Fact]
    public void Resource_links_are_kept_beside_the_payload()
    {
        var result = JsonNode.Parse("""
            {"content": [
              {"type": "text", "text": "{\"statusCode\": 200}"},
              {"type": "resource_link", "uri": "https://contoso.example.com/personal/margo/Documents/Q3%20review.docx", "name": "Q3 review.docx",
               "mimeType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "extra": 1},
              {"type": "resource_link", "uri": ""},
              {"type": "resource_link"}
            ]}
            """);
        var payload = McpMessages.Normalise(result);
        Assert.Equal(200, (int)payload["statusCode"]!);
        var link = Assert.Single(payload["resource_links"]!.AsArray())!;
        Assert.Equal("https://contoso.example.com/personal/margo/Documents/Q3%20review.docx", (string?)link["uri"]);
        Assert.Equal("Q3 review.docx", (string?)link["name"]);
        Assert.Null(link["extra"]);
    }

    [Fact]
    public void Without_resource_links_the_payload_is_unchanged()
    {
        var payload = McpMessages.Normalise(JsonNode.Parse("""{"content": [{"type": "text", "text": "{\"statusCode\": 200}"}]}"""));
        Assert.False(payload.ContainsKey("resource_links"));
        Assert.Throws<TransportException>(() => McpMessages.Normalise(JsonNode.Parse(
            """{"isError": true, "content": [{"type": "resource_link", "uri": "https://contoso.example.com/x"}]}""")));
    }
}

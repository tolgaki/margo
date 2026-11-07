using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers;
using Margo.Cloud.State.Helpers.Python;
using Xunit;

namespace Margo.Cloud.State.Tests.Helpers;

/// <summary>teams_feedback.py: cleaning, thread status, partial results and its Python type errors (fictional data).</summary>
public sealed class TeamsFeedbackTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("margo-teams-test-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Message(string id, string created, string author, string content = "", string? replyTo = null) =>
        "{\"id\": \"" + id + "\", \"messageType\": \"message\", \"createdDateTime\": \"" + created + "\", "
        + "\"from\": {\"user\": {\"displayName\": \"" + author + "\"}}, \"body\": {\"content\": \"" + content + "\"}"
        + (replyTo is null ? "" : ", \"replyToId\": \"" + replyTo + "\"") + "}";

    private static string Page(params string[] messages) => "{\"results\": [{\"statusCode\": 200, \"data\": {\"value\": [" + string.Join(", ", messages) + "]}}]}";

    [Theory]
    [InlineData("<p>Hello <b>world</b></p>", "Hello world")]
    [InlineData("<img src='a.png'>x", "[image] x")]
    [InlineData("<<b>>", ">")]
    [InlineData("<>", "<>")]
    [InlineData("a < b", "a < b")]
    [InlineData("\u3000x\u001Cy\u0085z\u200B", "x y z\u200B")]
    [InlineData("&nbsp;&lt;b&gt;", "<b>")]
    [InlineData("<img alt='>'>after", "[image] '>after")]
    public void Clean_matches_cpython(string body, string expected) => Assert.Equal(expected, TeamsFeedback.Clean(body));

    [Fact]
    public void Clean_of_falsy_and_non_string_bodies()
    {
        Assert.Equal("", TeamsFeedback.Clean(null));
        Assert.Equal("", TeamsFeedback.Clean(0L));
        Assert.Throws<PyTypeError>(() => TeamsFeedback.Clean(5L));
    }

    [Fact]
    public void Who_spoke_last_decides_the_status()
    {
        var roots = Write("roots.json", Page(
            Message("1", "2026-03-01T09:00:00Z", "Avery Example", "<p>question</p>"),
            Message("2", "2026-03-02T09:00:00Z", "Blake Sample", "another"),
            Message("3", "2026-03-03T09:00:00Z", "Casey Demo", "third"),
            Message("r1", "2026-03-01T10:00:00Z", "Blake Sample", "answer", replyTo: "1")));
        var replies = Write("replies.json", Page(
            Message("r2", "2026-03-02T10:00:00Z", "Avery Example", "help", replyTo: "2"),
            Message("r3", "2026-03-02T11:00:00Z", "Blake Sample", "still blocked", replyTo: "2")));
        var problems = new List<string>();
        var rows = TeamsFeedback.Build([roots], [replies], problems);
        Assert.Empty(problems);
        Assert.Equal(["team-last", "asker-last", "no-reply"], rows.Select(row => row.Status));
        Assert.Equal(["Blake Sample"], rows[0].Responders.Select(PyObj.Str));
        Assert.Equal(["Avery Example"], rows[1].Responders.Select(PyObj.Str));
        Assert.True(rows[0].Answered);
        Assert.False(rows[1].Answered);
        Assert.Equal("question", rows[0].Text);
    }

    [Fact]
    public void Pages_without_data_are_reported_and_the_exit_status_is_one()
    {
        var pages = Write("pages.json", "{\"results\": [{\"statusCode\": 500, \"error\": \"Internal\"}, {\"data\": {}}]}");
        var missing = Path.Combine(_root.FullName, "missing.json");
        var problems = new List<string>();
        TeamsFeedback.Build([pages, missing, _root.FullName], [], problems);
        Assert.Equal([pages + " page 1: HTTP 500 Internal", pages + " page 2: no value array",
                      missing + ": unreadable (FileNotFoundError)", _root.FullName + ": unreadable (IsADirectoryError)"], problems);
        var (code, stdout, _) = ScriptRunner.Run(new TeamsFeedbackScript(), [pages]);
        Assert.Equal(1, code);
        Assert.StartsWith("WARNING: 2 page(s) yielded no data. COUNTS BELOW ARE PARTIAL.\n", stdout, StringComparison.Ordinal);
        Assert.Contains("0 posts, 0 with no reply, 0 awaiting a follow-up (asker spoke last) (PARTIAL)\n", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Untrusted_types_raise_pythons_exceptions()
    {
        Assert.Throws<PyAttributeError>(() => TeamsFeedback.Build([Write("list.json", "[1]")], [], []));
        Assert.Throws<PyValueError>(() => TeamsFeedback.Build([Write("status.json", "{\"results\": [{\"statusCode\": \"abc\"}]}")], [], []));
        Assert.Throws<PyTypeError>(() => TeamsFeedback.Build([Write("id.json", Page("{\"id\": [1], \"messageType\": \"message\"}"))], [], []));
        var (code, _, stderr) = ScriptRunner.Run(new TeamsFeedbackScript(), [Write("value.json", "{\"results\": [{\"data\": {\"value\": null}}]}")]);
        Assert.Equal(1, code);
        Assert.StartsWith("Traceback (most recent call last):", stderr, StringComparison.Ordinal);
        Assert.Contains("TypeError: 'NoneType' object is not iterable", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Equal_ids_across_json_types_are_one_message()
    {
        var path = Write("ids.json", Page(
            "{\"id\": 1, \"messageType\": \"message\", \"createdDateTime\": \"2026-03-01\", \"lastModifiedDateTime\": \"a\"}",
            "{\"id\": 1.0, \"messageType\": \"message\", \"createdDateTime\": \"2026-03-02\", \"lastModifiedDateTime\": \"b\"}",
            "{\"id\": true, \"messageType\": \"message\", \"createdDateTime\": \"2026-03-03\", \"lastModifiedDateTime\": \"0\"}"));
        var rows = TeamsFeedback.Build([path], [], []);
        var row = Assert.Single(rows);
        Assert.Equal("1", PyJson.Dumps((System.Text.Json.Nodes.JsonNode?)row.Id)); // the first key object is kept
        Assert.Equal("2026-03-02", row.Created);                                   // the newest copy's value
    }

    [Fact]
    public void Json_view_stops_at_the_recursion_limit_after_partial_output()
    {
        var url = new string('[', 994) + "\"x\"" + new string(']', 994);
        var path = Write("deep.json", Page("{\"id\": \"1\", \"messageType\": \"message\", \"webUrl\": " + url + "}"));
        var (code, stdout, stderr) = ScriptRunner.Run(new TeamsFeedbackScript(), [path, "--format", "json"]);
        Assert.Equal(1, code);
        Assert.Contains("RecursionError", stderr, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"partial\": false,", stdout, StringComparison.Ordinal);
        Assert.EndsWith("[\n" + new string(' ', 2 * 996), stdout, StringComparison.Ordinal);
    }
}

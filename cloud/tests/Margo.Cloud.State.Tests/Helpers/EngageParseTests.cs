using System.Numerics;
using Margo.Cloud.State.Core;
using Margo.Cloud.State.Helpers;
using Margo.Cloud.State.Helpers.Python;
using Xunit;

namespace Margo.Cloud.State.Tests.Helpers;

/// <summary>engage_parse.py: the regular expressions, salvage, join keys and rows (fictional data).</summary>
public sealed class EngageParseTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("margo-engage-test-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Footnotes_follow_pythons_regex()
    {
        const string text = "[^h1]:\n \"Spans\"\n\u2014\n url1 trailing\n[^h\u0662]: \"A \"quoted\" title\" - https://example.com/threads/x\\.\n[^h3]: \"x\" - ";
        Assert.Equal([new EngageFootnote("h1", "Spans", "url1"), new EngageFootnote("h\u0662", "A \"quoted\" title", "https://example.com/threads/x\\.")],
                     EngageParse.Footnotes(text));
    }

    [Fact]
    public void Source_blobs_run_to_the_end_of_the_line()
    {
        Assert.Equal(["{\"a\": 1} tail", "{b"], EngageParse.SourceBlobs("x **sourceJson:** \t{\"a\": 1} tail\n**sourceJson:****sourceJson:**{b\n**sourceJson:** none"));
    }

    [Fact]
    public void Salvage_recovers_fields_with_unicode_digits_and_words()
    {
        var salvaged = EngageParse.Salvage("{\"ScopeType\":\"YAMMER_GROUP\",\"PostBody\":\"a\", 5, \"b\" ,\"k\u00E9y\u00B2\":1,\"PostUpvoteCount\":\u0663\u0664");
        Assert.Equal("{\"PostUpvoteCount\": 34, \"PostBody\": \"a\\\", 5, \\\"b\", \"_salvaged\": true, \"ScopeType\": \"YAMMER_GROUP\"}",
                     PyJson.Dumps(salvaged));
    }

    [Theory]
    [InlineData("2026-02-28", "2026-03-01", 1)]
    [InlineData("\u0662\u0660\u0662\u0666-01-01", "2026-01-31", 30)]
    [InlineData("2026-02-30", "2026-03-01", 0)]
    [InlineData(" 2026 - 1 - 1 ", "+2026-01-0_2", 1)]
    [InlineData("2026-01", "2026-01-01", 0)]
    public void Day_gap_uses_int_and_date_rules(string created, string updated, int expected) =>
        Assert.Equal(new BigInteger(expected), EngageParse.DayGap(created, updated));

    [Fact]
    public void Encoded_key_ignores_padding_and_raises_for_other_types()
    {
        Assert.Equal("eyJpZCI6IjEifQ", EngageParse.EncodedKey("eyJpZCI6IjEifQ%3D%3D"));
        Assert.Equal("", EngageParse.EncodedKey("==="));
        Assert.Equal("", EngageParse.EncodedKey(null));
        Assert.Throws<PyTypeError>(() => EngageParse.EncodedKey(5L));
        Assert.Throws<PyAttributeError>(() => EngageParse.EncodedKey(PyJson.Loads("[\"a\"]")));
        Assert.Throws<PyTypeError>(() => EngageParse.EncodedKey(PyJson.Loads("[\"%\"]")));
        Assert.Equal("YXJ0aWNsZQ==/threads/x", EngageParse.ThreadId("https://example.com/g/articles/YXJ0aWNsZQ==/threads/x"));
        Assert.Null(EngageParse.ThreadId("https://example.com/threads/"));
    }

    [Fact]
    public void Rows_join_titles_by_thread_id_and_flag_degraded_rows()
    {
        var path = Path.Combine(_root.FullName, "readout.md");
        File.WriteAllText(path, string.Join("\n",
            "**sourceJson:** {\"ScopeType\":\"YAMMER_GROUP\",\"GroupName\":\"Work IQ\",\"Type\":\"QUESTION\",\"EncodedId\":\"aWQx==\",\"PostBody\":\"Body one\",\"CreatedAt\":\"2026-03-01T00:00:00Z\",\"UpdatedAt\":\"2026-03-05T00:00:00Z\"}",
            "**sourceJson:** {\"ScopeType\":\"SHAREPOINT\",\"EncodedId\":\"aWQy\"}",
            "**sourceJson:** {\"ScopeType\":\"YAMMER_GROUP\",\"PostBody\":\"He said \"hi\"\",\"Type\":\"DISCUSSION\"",
            "[^h1]: \"Linked title\" \u2014 https://engage.example.com/threads/aWQx%3D%3D."));
        var rows = EngageParse.LoadHits(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Linked title", "https://engage.example.com/threads/aWQx%3D%3D", true, false, new BigInteger(4)),
                     (rows[0].Title, rows[0].Url, rows[0].Linked, rows[0].Salvaged, rows[0].ActiveDays));
        Assert.Equal(("He said \"hi\"", "", false, true), (rows[1].Title, rows[1].Url, rows[1].Linked, rows[1].Salvaged));

        var (code, stdout, _) = ScriptRunner.Run(new EngageParseScript(), [path]);
        Assert.Equal(0, code);
        Assert.StartsWith("2 posts \u2014 1 linked, 1 unlinked, 1 salvaged (body may be partial)\n", stdout, StringComparison.Ordinal);
        Assert.Contains("ASK   [QUES] act:4    2026-03-01 ?                    Linked title\n", stdout, StringComparison.Ordinal);
        Assert.Contains("  .*^ [DISC] act:0     ?                    He said \"hi\"\n", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_files_are_uncaught_errors()
    {
        var (code, stdout, stderr) = ScriptRunner.Run(new EngageParseScript(), [Path.Combine(_root.FullName, "missing.md")]);
        Assert.Equal((1, ""), (code, stdout));
        Assert.Contains("FileNotFoundError: [Errno 2] No such file or directory", stderr, StringComparison.Ordinal);
    }
}

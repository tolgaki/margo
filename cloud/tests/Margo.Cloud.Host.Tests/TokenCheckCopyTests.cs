namespace Margo.Cloud.Host.Tests;

/// <summary>
/// The host's inbound token check is a copy of the router's (Margo.Cloud.Host/PORTING.md, "The token check"): both
/// answer the same platform tokens for the same blueprint. Security review: a fix to one copy that does not reach the
/// other would let the two hops accept different tokens, so this test fails as soon as the copies differ in anything
/// but the copy note and the namespace.
/// </summary>
public sealed class TokenCheckCopyTests
{
    private const string Note =
        "// Copied from Margo.Cloud.Router (namespace changed only): the Agents SDK token check the Python host also ran.\n"
        + "// Keep the copies in step; see Margo.Cloud.Host/PORTING.md, \"Requests to other components\".\n";

    public static TheoryData<string, string> Copies => new()
    {
        { "Authentication/AgentTokenValidator.cs", "Authentication/AgentTokenValidator.cs" },
        { "Authentication/CompactJws.cs", "Authentication/CompactJws.cs" },
        { "Authentication/EntraIssuers.cs", "Authentication/EntraIssuers.cs" },
        { "Authentication/SigningKeys.cs", "Authentication/SigningKeys.cs" },
        { "PythonBase64.cs", "Authentication/PythonBase64.cs" },
        { "PythonCompat.cs", "Authentication/PythonCompat.cs" },
        { "PythonJsonBytes.cs", "Authentication/PythonJsonBytes.cs" },
    };

    [Theory]
    [MemberData(nameof(Copies))]
    public void The_host_copy_matches_the_router_source(string router, string host)
    {
        var source = Path.Combine(Root(), "cloud", "src");
        var original = Normalise(File.ReadAllText(Path.Combine(source, "Margo.Cloud.Router", router)));
        var copy = Normalise(File.ReadAllText(Path.Combine(source, "Margo.Cloud.Host", host)));
        Assert.StartsWith(Note, copy, StringComparison.Ordinal);
        var expected = original
            .Replace("namespace Margo.Cloud.Router.Authentication;", "namespace Margo.Cloud.Host.Authentication;", StringComparison.Ordinal)
            .Replace("namespace Margo.Cloud.Router;", "namespace Margo.Cloud.Host.Authentication;", StringComparison.Ordinal);
        Assert.Equal(expected, copy[Note.Length..]);
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "cloud", "src", "Margo.Cloud.Router", "Authentication", "AgentTokenValidator.cs")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

namespace Margo.Cloud.Host.Tests;

/// <summary>
/// The host's inbound token check is the router's (Margo.Cloud.Host/PORTING.md, "The token check"): both answer the
/// same platform tokens for the same blueprint. Security review: a fix that reaches one hop and not the other would
/// let the two accept different tokens. There is one implementation, in Margo.Cloud.Auth, and these tests fail as soon
/// as either service's source tree holds a file of its own with one of these names again.
/// </summary>
public sealed class TokenCheckCopyTests
{
    public static TheoryData<string> Shared => new()
    {
        "AgentTokenValidator.cs",
        "CompactJws.cs",
        "EntraIssuers.cs",
        "SigningKeys.cs",
        "PythonBase64.cs",
        "PythonCompat.cs",
        "PythonJsonBytes.cs",
    };

    [Theory]
    [MemberData(nameof(Shared))]
    public void The_token_check_has_one_source(string file)
    {
        var source = Path.Combine(Root(), "cloud", "src");
        Assert.True(File.Exists(Path.Combine(source, "Margo.Cloud.Auth", file)), file + " is missing from Margo.Cloud.Auth");
        foreach (var service in new[] { "Margo.Cloud.Host", "Margo.Cloud.Router" })
        {
            var copies = Directory.EnumerateFiles(Path.Combine(source, service), file, SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(source, path).Replace('\\', '/'))
                .Where(path => !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal));
            Assert.Empty(copies);
        }
    }

    [Fact]
    public void The_host_and_the_router_run_the_same_validator()
    {
        var shared = typeof(Margo.Cloud.Auth.AgentTokenValidator);
        Assert.Equal("Margo.Cloud.Auth", shared.Assembly.GetName().Name);
        // The host takes the validator in its composition; no type of that name is left in the host itself.
        Assert.Null(typeof(MargoHost).Assembly.GetType("Margo.Cloud.Host.Authentication.AgentTokenValidator"));
        var root = Path.Combine(Root(), "cloud", "src");
        foreach (var project in new[] { "Margo.Cloud.Host/Margo.Cloud.Host.csproj", "Margo.Cloud.Router/Margo.Cloud.Router.csproj" })
        {
            Assert.Contains("../Margo.Cloud.Auth/Margo.Cloud.Auth.csproj", File.ReadAllText(Path.Combine(root, project)), StringComparison.Ordinal);
        }
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "cloud", "src", "Margo.Cloud.Auth", "AgentTokenValidator.cs")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

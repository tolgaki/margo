using System.Text.Json.Nodes;

namespace Margo.Cloud.Core.Tests;

/// <summary>Fictional identities and configuration shared by the tests (tests/test_autopilot_instances.py).</summary>
internal static class Fixtures
{
    public const string Identity = "agent-identity-1";
    public const string User = "margo-user-1";
    public const string Domain = "calmforest-1a2b3c.example.com";

    /// <summary>instance_key(IDENTITY, USER) as computed by the Python implementation.</summary>
    public const string Key = "8f6b44ff6f8dd73b5b3a";

    public static readonly DateTimeOffset Now = new(2026, 11, 7, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A code point as a string, so sources stay ASCII.</summary>
    public static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    /// <summary>A lone UTF-16 surrogate, which Python's UTF-8 encoder refuses.</summary>
    public static string LoneSurrogate => ((char)0xd800).ToString();

    /// <summary>CONFIG from the Python tests: every required name, a domain and one replica setting.</summary>
    public static JsonObject Config()
    {
        var config = new JsonObject();
        foreach (var name in Instances.RequiredConfig)
        {
            config[name] = "fictional-" + name.ToLowerInvariant();
        }

        config["environmentDomain"] = Domain;
        config["minReplicas"] = 0;
        return config;
    }

    public static JsonObject Activity(Action<JsonObject>? change = null)
    {
        var activity = new JsonObject
        {
            ["type"] = "message",
            ["recipient"] = new JsonObject { ["agenticAppId"] = Identity, ["agenticUserId"] = User },
            ["from"] = new JsonObject { ["aadObjectId"] = "principal-1" },
            ["text"] = "Brief me",
        };
        change?.Invoke(activity);
        return activity;
    }

    public static string Lf(string text) => text.ReplaceLineEndings("\n");
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class RepoRoot
{
    /// <summary>The repository root: the first directory above the test binaries that holds the instance template.</summary>
    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "autopilot", "infra", "instance.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

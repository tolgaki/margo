using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>
/// What desktop Margo can do that hosted Margo cannot do yet, and why (<c>parity.py</c>). One list, used twice: the
/// engine tells the model this list, so she reports a missing capability instead of treating it as an empty result,
/// and the parity tests fail when a skill uses something that is neither hosted nor listed here.
/// </summary>
public static class NotHosted
{
    private const string WriteReason = "outward actions arrive with approval cards; until then Margo drafts and the manager sends";

    /// <summary>
    /// <c>NOT_HOSTED_PROGRAMS</c>: programs the procedures run in a desktop shell. Hosted Margo has no shell; each
    /// becomes a narrow tool once its credential is decided.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Programs { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gh"] = "GitHub review load needs a GitHub credential for hosted Margo; not decided yet",
        ["az"] = "the Azure CLI signs in as the desktop user; Azure DevOps work items need their own credential for "
                 + "hosted Margo, not decided yet",
        ["curl"] = "raw HTTP from a shell; the procedures use it for Azure DevOps work items, which need a credential first",
        ["jq"] = "shell JSON filtering for Azure DevOps responses; arrives with the Azure DevOps tool",
        ["git"] = "the team decision registry needs repository access for hosted Margo; not decided yet",
    };

    /// <summary><c>NOT_HOSTED_SCRIPTS</c>: scripts that are about the desktop installation, or need their own sign-in.</summary>
    public static IReadOnlyDictionary<string, string> Scripts { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["margo_doctor.py"] = "checks the desktop installation; hosted health comes from the service's logs",
        ["margo_store.py"] = "the hosting service sets up the account and private state",
        ["m365_files.py"] = "the large-file bridge has its own browser sign-in; a hosted bridge through the guard "
                            + "is not built yet",
        ["memory_encoder.py"] = "optional local embeddings are not installed in the hosted image",
    };

    /// <summary><c>NOT_HOSTED_WORKIQ</c>: every Work IQ write tool, in sorted order, with the same reason.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> WorkIQ { get; } =
        PyText.Sorted(GuardPolicy.WriteTools).Select(tool => KeyValuePair.Create(tool, WriteReason)).ToArray();

    /// <summary><c>summary()</c>: Markdown lines for the model: what is missing, and what to say instead.</summary>
    public static string Summary()
    {
        var lines = new List<string>
        {
            "- Work IQ writes (" + string.Join(", ", WorkIQ.Select(item => "`workiq-" + item.Key + "`")) + "): " + WorkIQ[0].Value + ".",
        };
        foreach (var name in PyText.Sorted(Programs.Keys))
        {
            lines.Add("- `" + name + "`: " + Programs[name] + ".");
        }

        foreach (var name in PyText.Sorted(Scripts.Keys))
        {
            lines.Add("- `" + name + "`: " + Scripts[name] + ".");
        }

        foreach (var ((script, command), reason) in StateCommands.NotHostedCommands
                     .OrderBy(item => item.Key.Script, PyRepr.StringComparer)
                     .ThenBy(item => item.Key.Command, PyRepr.StringComparer))
        {
            lines.Add("- `" + script + " " + command + "`: " + reason + ".");
        }

        return string.Join("\n", lines);
    }
}

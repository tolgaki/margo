using Margo.Cloud.Guard;
using Xunit;

namespace Margo.Cloud.Engine.Tests;

/// <summary>In delegation mode Margo is told to read mail and calendar directly and to search what is shared with her.</summary>
public sealed class DelegationInstructionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Delegation_adds_the_direct_read_and_shared_search_guidance()
    {
        var instructions = EngineTools.LoadInstructions(Fixtures.Root, now: Now, principalAccess: PrincipalAccessModes.Delegation);
        // The addendum ends the stable instructions; only the current time comes after it (so it never breaks the cached prefix).
        var stable = EngineTools.StableInstructions(Fixtures.Root, principalAccess: PrincipalAccessModes.Delegation);
        Assert.EndsWith(EngineTools.DelegationAddendum, stable, StringComparison.Ordinal);
        Assert.Equal(stable + "\n# Current time\n\nCurrent time: Friday 20 November 2026, 04:00 (America/Los_Angeles).\n", instructions);
        Assert.Contains("`/me/calendarView`", instructions, StringComparison.Ordinal);
        Assert.Contains("`on_data_of: \"self\"`, including `workiq-retrieve` and\n  `workiq-ask`", EngineTools.DelegationAddendum.Replace("    ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void On_behalf_of_and_unspecified_modes_keep_the_original_instructions()
    {
        var original = EngineTools.LoadInstructions(Fixtures.Root, now: Now);
        Assert.Equal(original, EngineTools.LoadInstructions(Fixtures.Root, now: Now, principalAccess: PrincipalAccessModes.Obo));
        Assert.DoesNotContain("delegation", original.Split("# Available skills")[0], StringComparison.Ordinal);
    }
}

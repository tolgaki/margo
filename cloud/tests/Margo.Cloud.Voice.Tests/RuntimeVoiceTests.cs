using Margo.Cloud.Orchestrator;

namespace Margo.Cloud.Voice.Tests;

/// <summary>The voice test of <c>tests/test_autopilot_runtime.py</c>: the manager's <c>/voice</c> command in Teams answers
/// with the voice page's link, or says voice is not set up; nobody else gets the link. (The same scenario is also in
/// <c>Margo.Cloud.Orchestrator.Tests</c>, which owns the command.)</summary>
public sealed class RuntimeVoiceTests
{
    private static Margo.Cloud.Orchestrator.Orchestrator Create(TempState state, string? voiceUrl = null) =>
        new(new Margo.Cloud.Engine.Engine(new UnusedModel(), "fictional-model", Fixtures.Root), new DirectoryTransport(), state.Path,
            _ => new InMemoryLeaseStore(), new OrchestratorOptions { VoiceUrl = voiceUrl, StateCliCommand = Fixtures.StateCli });

    private static InboundEvent Event(string sender = Fixtures.Manager, string conversation = "conv-manager") => new()
    {
        AgentIdentityId = Fixtures.Instance.AgentIdentityId,
        AgentUserId = Fixtures.Instance.AgentUserId,
        SenderId = sender,
        SenderName = "Dana",
        ConversationId = conversation,
        ConversationType = "personal",
        ActivityId = "a-1",
        Channel = "teams",
        Text = "/voice",
    };

    private static Task<TurnReply> Handle(Margo.Cloud.Orchestrator.Orchestrator orchestrator, InboundEvent inbound) =>
        orchestrator.HandleAsync(inbound, kind => Task.FromResult<string?>(kind + "-token"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Voice_command_replies_with_the_link_or_says_it_is_not_set_up()
    {
        using var state = new TempState();
        const string url = "https://router.example.com/i/0123456789abcdef0123/voice/";
        var linked = await Handle(Create(state, url), Event());
        Assert.Equal(new TurnReply("Talk to me here: " + url, "voice_link"), linked);
        Assert.Equal(Margo.Cloud.Orchestrator.Orchestrator.VoiceUnavailable, (await Handle(Create(state), Event())).Reply);
        var colleague = await Handle(Create(state, url), Event(sender: "colleague-1", conversation: "conv-rafa"));
        Assert.Null(colleague.Reply);
    }
}

using System.Text.Json.Nodes;
using Margo.Cloud.Guard;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator.Tests;

/// <summary>
/// The orchestrator's helpers: <c>TurnTokens</c>, <c>_manager_from</c>, the command patterns and <c>set_reply_mode</c>.
/// Expected values were produced by the Python functions on Python 3.13.
/// </summary>
public sealed class TurnSupportTests
{
    [Fact]
    public void Turn_tokens_have_no_fallback()
    {
        var tokens = new TurnTokens(new Dictionary<string, string?> { ["margo"] = "margo-token", ["principal"] = null, ["other"] = "" });
        Assert.Equal("margo-token", tokens.TokenFor("margo"));
        Assert.Equal("principal token not available for this turn", Assert.Throws<TokenUnavailableException>(() => tokens.TokenFor("principal")).Message);
        Assert.Equal("other token not available for this turn", Assert.Throws<TokenUnavailableException>(() => tokens.TokenFor("other")).Message);
        Assert.Equal("self token not available for this turn", Assert.Throws<TokenUnavailableException>(() => tokens.TokenFor("self")).Message);
    }

    [Theory]
    [InlineData("ok", """{"results": [{"statusCode": 200, "data": {"id": "p", "displayName": ""}}]}""", "p", "your manager")]
    [InlineData("ok", """{"results": [{"statusCode": 200, "data": {"id": "p", "displayName": 5}}]}""", "p", null)]
    [InlineData("ok", """{"id": "top", "displayName": "Top"}""", "top", "Top")]
    [InlineData("ok", """{"data": {"id": "nested", "displayName": "N"}}""", "nested", "N")]
    [InlineData("ok", """{"results": [{"data": null}, {"data": {"id": ""}}, {"data": {"id": 7}}]}""", null, null)]
    [InlineData("ok", """{"results": [{"data": {"id": "first"}}], "data": {"id": "second"}}""", "first", "your manager")]
    [InlineData("error", """{"results": [{"statusCode": 404}, {"statusCode": 404}]}""", null, null)]
    [InlineData("error", """{"statusCode": 404}""", null, null)]
    [InlineData("ok", null, null, null)]
    public void Manager_is_read_from_the_directory_answer(string status, string? data, string? id, string? name)
    {
        var result = new GuardResult(status, "margo", "fetch", Data: data is null ? null : Fixtures.Obj(data));
        Assert.Equal(id is null ? null : new Manager(id, name), ManagerDirectory.From(result));
    }

    [Theory]
    [InlineData("error", """{"results": [{"statusCode": 404}, {"statusCode": 500}]}""", "error")]
    [InlineData("error", "{}", "error")]
    [InlineData("partial", """{"results": [{"statusCode": 404}]}""", "partial")]
    [InlineData("unknown", null, "unknown")]
    [InlineData("blocked", null, "blocked")]
    [InlineData("", null, "no status")]
    public void A_failed_lookup_is_never_no_manager(string status, string? data, string message)
    {
        var result = new GuardResult(status, "margo", "fetch", Data: data is null ? null : Fixtures.Obj(data));
        Assert.Equal(message, Assert.Throws<ManagerLookupFailedException>(() => ManagerDirectory.From(result)).Message);
    }

    [Theory]
    [InlineData("/voice", true, null)]
    [InlineData(" /VOICE \n", true, null)]
    [InlineData("/voice now", false, null)]
    [InlineData("/vo\u0131ce", true, null)]
    [InlineData("/vo\u0130ce", true, null)]
    [InlineData("\u001c/voice\u001f", true, null)]
    [InlineData("\u3000/voice\u2028", true, null)]
    [InlineData("/voice\n\n", true, null)]
    [InlineData("/replies none", false, "none")]
    [InlineData("/REPLIES  Acknowledge ", false, "acknowledge")]
    [InlineData("/replies\tapprove\n", false, "approve")]
    [InlineData("/replies", false, null)]
    [InlineData("/repliesnone", false, null)]
    [InlineData("/replie\u017f none", false, "none")]
    [InlineData("/replies ac\u212anowledge", false, "acknowledge")]
    [InlineData("/replies  none x", false, null)]
    [InlineData("/replies autonomous", false, null)]
    [InlineData("x /replies none", false, null)]
    [InlineData("/replies\u00a0none", false, "none")]
    [InlineData("/replies\u200bnone", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    [InlineData("/voice\r", true, null)]
    [InlineData("/voice\u0085", true, null)]
    public void Commands_match_as_python_re_does(string? text, bool voice, string? mode)
    {
        Assert.Equal(voice, ManagerCommands.IsVoice(text));
        Assert.Equal(mode, ManagerCommands.ReplyMode(text));
    }

    [Fact]
    public void Set_reply_mode_keeps_the_rest_of_the_preferences()
    {
        using var state = new TempState();
        var preferences = state.Combine("private", "preferences.md");
        ReplySetting.SetReplyMode(preferences, "acknowledge");
        Assert.Equal("- **Replies to other people:** acknowledge\n", File.ReadAllText(preferences));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(state.Combine("private")));

        File.WriteAllText(preferences, "\n\n# Preferences\r\n\r\n- **Tone:** brief\r\n  * **Replies to other people:** none please\r\n- **Other:** x\n\n\n");
        ReplySetting.SetReplyMode(preferences, "approve");
        Assert.Equal("\n\n# Preferences\n\n- **Tone:** brief\n- **Replies to other people:** approve\n- **Other:** x\n\n\n", File.ReadAllText(preferences));

        File.WriteAllText(preferences, "\n\n# Preferences\n- **Tone:** brief\n\n\n");
        ReplySetting.SetReplyMode(preferences, "none");
        Assert.Equal("# Preferences\n- **Tone:** brief\n- **Replies to other people:** none\n", File.ReadAllText(preferences));

        File.WriteAllText(preferences, "- **Replies to other people:** none\n- **Replies to other people:** approve\n");
        ReplySetting.SetReplyMode(preferences, "acknowledge");
        Assert.Equal("- **Replies to other people:** acknowledge\n- **Replies to other people:** acknowledge\n", File.ReadAllText(preferences));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(preferences));

        File.WriteAllBytes(preferences, [0xff, 0x0a]);
        Assert.Throws<PyUnicodeError>(() => ReplySetting.SetReplyMode(preferences, "none"));
        Assert.Throws<PyUnicodeError>(() => ReplySetting.Read(preferences));
        Assert.Null(ReplySetting.Read(state.Combine("missing.md")));
    }

    [Fact]
    public void Every_fixed_text_and_limit_matches_the_python_modules()
    {
        // Printed by the Python orchestrator, office, replies and statesync modules before the cutover removed them.
        var python = (JsonObject)PyJson.Loads(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "orchestrator_constants.json")))!;
        var csharp = new JsonObject
        {
            ["TURN_CALL_BUDGET"] = Orchestrator.TurnCallBudget,
            ["STATE_CALL_BUDGET"] = Orchestrator.StateCallBudget,
            ["PERSONAL"] = Orchestrator.Personal,
            ["VOICE_UNAVAILABLE"] = Orchestrator.VoiceUnavailable,
            ["NO_MANAGER"] = Orchestrator.NoManager,
            ["MANAGER_UNKNOWN"] = Orchestrator.ManagerUnknown,
            ["GROUP_REFUSED"] = Orchestrator.GroupRefused,
            ["STATE_UNSAFE"] = Orchestrator.StateUnsafe,
            ["TURN_FAILED"] = Orchestrator.TurnFailed,
            ["OFFICE_FAMILY"] = OfficeQueue.OfficeFamily,
            ["CONVERSATION_FAMILY"] = OfficeQueue.ConversationFamily,
            ["CONVERSATION_SCOPE"] = OfficeQueue.ConversationScope,
            ["OFFICE_NOTE"] = OfficeQueue.OfficeNote,
            ["EXCERPT_LIMIT"] = OfficeQueue.ExcerptLimit,
            ["MODES"] = PyNode.StrList(ReplyModes.All),
            ["SETTING_LABEL"] = ReplyPolicy.SettingLabel,
            ["ACKNOWLEDGEMENT"] = ReplyPolicy.Acknowledgement,
            ["BUNDLE"] = StateSync.Bundle,
            ["MARKER"] = StateSync.Marker,
        };
        Assert.Equal(PyJson.Dumps(python), PyJson.Dumps(csharp));
    }

    [Fact]
    public void The_fixed_texts_are_python_s()
    {
        Assert.Equal("I won't reply to anyone else; their messages wait for you.", ReplySetting.ChangedNote("none"));
        Assert.Equal("I'll send one short 'passed this on' note per conversation, nothing more.", ReplySetting.ChangedNote("acknowledge"));
        Assert.Equal("Noted, but approval cards aren't in this version yet, so until they are I'll treat this as 'none'.", ReplySetting.ChangedNote("approve"));
        Assert.Equal(40, Orchestrator.TurnCallBudget);
        Assert.Equal(40, Orchestrator.StateCallBudget);
        Assert.Equal(new JsonObject { ["reply"] = null, ["status"] = "queued" }.ToJsonString(), new TurnReply(null, "queued").ToJsonObject().ToJsonString());
    }
}

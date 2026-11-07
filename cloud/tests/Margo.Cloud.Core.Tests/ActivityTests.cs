using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>tests/test_autopilot_runtime.py EmailClassificationTests and StableIdTests, plus the adapters.</summary>
public sealed class ActivityTests
{
    // The Python test's Activity(channel_id, entities) and Channel(channel, sub_channel) shapes.
    private static EmailFacts Text(string? channelId, params JsonNode[] entities) =>
        new() { ChannelIdText = channelId, Entities = entities.Select(ActivityEntity.FromJson).ToList() };

    private static EmailFacts Channel(string channel, string subChannel = "") =>
        new() { Channel = channel, SubChannel = subChannel, ChannelIdText = subChannel.Length == 0 ? channel : channel + ":" + subChannel };

    private static JsonObject Entity(string json) => (JsonObject)PythonJson.Loads(json)!;

    [Fact]
    public void Email_shapes_are_recognised()
    {
        foreach (var facts in new[]
        {
            Text("email"), Text("agents:email"), Channel("agents", "email"), Channel("Email"),
            Text("msteams", Entity("""{"type": "productInfo", "id": "email"}""")),
        })
        {
            Assert.True(ActivityClassifier.IsEmail(facts));
        }

        foreach (var facts in new[] { Text("msteams"), Channel("agents", "teams"), Text(null), Text("msteams", Entity("""{"type": "mention"}""")) })
        {
            Assert.False(ActivityClassifier.IsEmail(facts));
        }
    }

    [Fact]
    public void Structured_channel_wins_over_its_text_unless_empty()
    {
        Assert.False(ActivityClassifier.IsEmail(new EmailFacts { Channel = "msteams", ChannelIdText = "email" }));
        Assert.True(ActivityClassifier.IsEmail(new EmailFacts { Channel = string.Empty, SubChannel = "teams", ChannelIdText = "AGENTS:EMAIL" }));
        Assert.False(ActivityClassifier.IsEmail(new EmailFacts { Channel = "agents", SubChannel = null, ChannelIdText = "agents:email" }));
        Assert.False(ActivityClassifier.IsEmail(new EmailFacts { ChannelIdText = "agents:email:x" })); // partition keeps "email:x"
        Assert.True(ActivityClassifier.IsEmail(new EmailFacts { ChannelIdText = "email:anything" }));
    }

    [Fact]
    public void Product_info_from_the_sdk_counts()
    {
        Assert.True(ActivityClassifier.IsEmail(new EmailFacts { ChannelIdText = "msteams", ProductInfoId = "Email" }));
        Assert.False(ActivityClassifier.IsEmail(new EmailFacts { ChannelIdText = "msteams", ProductInfoId = "teams" }));
        Assert.True(ActivityClassifier.IsEmail(new EmailFacts { Entities = [new ActivityEntity("ProductInfo", "EMAIL")] }));
    }

    [Fact]
    public void Entity_fields_are_read_like_python_value()
    {
        // Expected results from activity.is_email in Python 3.13.
        Assert.True(ActivityClassifier.IsEmail(Text("msteams", Entity("""{"type": "", "TYPE": "productInfo", "id": "email"}"""))));
        Assert.False(ActivityClassifier.IsEmail(Text("msteams", Entity("""{"TYPE": "productInfo", "type": "", "id": "email"}"""))));
        Assert.True(ActivityClassifier.IsEmail(Text("msteams", Entity("""{"Type": "PRODUCTINFO", "Id": "EMAIL"}"""))));
        Assert.False(ActivityClassifier.IsEmail(Text("msteams", Entity("""{"type": 5, "Type": "productInfo", "id": "email"}"""))));
        var kelvin = new JsonObject { ["type"] = "productInfo", ["id"] = U(0x212a) + "mail" };
        Assert.False(ActivityClassifier.IsEmail(Text("msteams", kelvin)));
        Assert.Equal(new ActivityEntity(null, null), ActivityEntity.FromJson(new JsonArray()));
        Assert.Equal(new ActivityEntity("5", "true"), ActivityEntity.FromJson(Entity("""{"type": 5, "id": true}""")));
    }

    [Fact]
    public void Raw_activity_json_is_classified_too()
    {
        Assert.True(ActivityClassifier.IsEmail(PythonJson.Loads("""{"type": "message", "channelId": "agents:email"}""")));
        Assert.True(ActivityClassifier.IsEmail(PythonJson.Loads("""{"channelId": "msteams", "entities": [{"type": "productInfo", "id": "email"}]}""")));
        Assert.False(ActivityClassifier.IsEmail(PythonJson.Loads("""{"channelId": "msteams", "entities": {"type": "productInfo"}}""")));
        Assert.False(ActivityClassifier.IsEmail(PythonJson.Loads("""{"channelId": 5}""")));
        Assert.False(ActivityClassifier.IsEmail(Activity()));
        Assert.False(ActivityClassifier.IsEmail((JsonNode?)null));
    }

    [Fact]
    public void Missing_ids_are_derived_from_content_never_shared()
    {
        var first = ActivityClassifier.StableIds(null, null, "colleague-1", "first");
        Assert.NotEqual(first, ActivityClassifier.StableIds(null, null, "colleague-1", "second"));
        Assert.NotEqual(first.ExternalId, ActivityClassifier.StableIds(null, null, "colleague-2", "first").ExternalId);
        Assert.Equal(first, ActivityClassifier.StableIds(string.Empty, string.Empty, "colleague-1", "first"));
        Assert.Equal(new MessageIds("message-1", "conv-1"), ActivityClassifier.StableIds("message-1", "conv-1", "x", "y"));
        Assert.Equal(new MessageIds("message-1", "message-message-1"), ActivityClassifier.StableIds("message-1", null, "x", "y"));
    }

    [Fact]
    public void Derived_ids_match_the_python_implementation()
    {
        // Golden values from margo_autopilot.activity.stable_ids.
        const string first = "sha256-845a9552e35b96d2f4fbdb4ac70272606d897847883a5041f0c11c075c630d0f";
        Assert.Equal(new MessageIds(first, "message-" + first), ActivityClassifier.StableIds(null, null, "colleague-1", "first"));
        var text = "line\nbreak " + U(0x2028) + " " + U(0x1f600) + " " + U(0x7f) + " " + U(0x01) + " \"q\" \\";
        Assert.Equal(
            new MessageIds("sha256-a5f1c02a21cc8bb13091f71a512016c0d3e952fcc023d2f56a25f5a8065e14d7", "conv-" + U(0xe9)),
            ActivityClassifier.StableIds(null, "conv-" + U(0xe9), null, text));
        const string empty = "sha256-52957740ed8b51caf1c22b61f89b4801a7184bff1a24807613bd808789f977d4";
        Assert.Equal(new MessageIds(empty, "message-" + empty), ActivityClassifier.StableIds(string.Empty, null, string.Empty, null));
    }

    [Fact]
    public void A_lone_surrogate_cannot_be_hashed_as_in_python()
    {
        Assert.Throws<EncoderFallbackException>(() => ActivityClassifier.StableIds(null, null, "colleague-1", "x" + LoneSurrogate));
        Assert.Equal(new MessageIds("m", "c" + LoneSurrogate), ActivityClassifier.StableIds("m", "c" + LoneSurrogate, null, null));
    }
}

using System.Text.Json.Nodes;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Margo.Cloud.Host.Tests;

/// <summary>On-behalf-of mode asks only the manager to sign in, only in their 1:1 chat with Margo.</summary>
public sealed class PrincipalSignInTests
{
    private static readonly Dictionary<string, string?> Obo = new()
    {
        ["MARGO_PRINCIPAL_ACCESS"] = "obo",
        ["PRINCIPAL_AUTH_HANDLER"] = "PRINCIPAL",
        ["AgentApplication:UserAuthorization:Handlers:PRINCIPAL:Type"] = "AzureBotUserAuthorization",
        ["AgentApplication:UserAuthorization:Handlers:PRINCIPAL:Settings:AzureBotOAuthConnectionName"] = "manager-sign-in",
    };

    private static IChannelAdapter? _adapter;

    private static TurnContext Context(JsonObject activity) =>
        new(_adapter!, ProtocolJsonSerializer.ToObject<Activity>(activity.ToJsonString()));

    private static async Task<MargoTurns> AfterAManagerTurn(HostFactory host)
    {
        host.Model.Add(FakeModel.Final("Nothing urgent."));
        var client = host.Start();
        _adapter = (IChannelAdapter)host.Services.GetRequiredService<Microsoft.Agents.Hosting.AspNetCore.IAgentHttpAdapter>();
        await HostFactory.PostAsync(client, Activities.Message("What is on today?"));
        await host.NextTurnAsync(); // the turn looked the manager up
        return host.Services.GetRequiredService<MargoTurns>();
    }

    [Fact]
    public async Task Only_the_managers_own_one_to_one_message_signs_them_in()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state, Obo);
        var turns = await AfterAManagerTurn(host);
        Assert.True(turns.PrincipalSignInConfigured);
        Assert.Equal(["AGENTIC", "PRINCIPAL"], turns.SignInHandlers(Context(Activities.Message("Brief me"))));
        Assert.Equal(["AGENTIC"], turns.SignInHandlers(Context(Activities.Message("Hi Margo", sender: "colleague-1"))));
        Assert.Equal(["AGENTIC"], turns.SignInHandlers(Context(Activities.Message("Hi", conversation: "group-1", conversationType: "groupChat"))));
        Assert.False(turns.SignsInPrincipal(Context(Activities.Mail("Please review", sender: Activities.Manager))));
    }

    [Fact]
    public async Task Delegation_mode_never_asks()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state, new Dictionary<string, string?>(Obo) { ["MARGO_PRINCIPAL_ACCESS"] = "delegation" });
        var turns = await AfterAManagerTurn(host);
        Assert.False(turns.PrincipalSignInConfigured);
        Assert.Equal(["AGENTIC"], turns.SignInHandlers(Context(Activities.Message("Brief me"))));
    }

    [Fact]
    public async Task Without_the_handler_configured_nobody_is_asked()
    {
        using var state = new TempState();
        await using var host = new HostFactory(state, new Dictionary<string, string?> { ["MARGO_PRINCIPAL_ACCESS"] = "obo", ["PRINCIPAL_AUTH_HANDLER"] = "PRINCIPAL" });
        var turns = await AfterAManagerTurn(host);
        Assert.False(turns.SignsInPrincipal(Context(Activities.Message("Brief me"))));
    }
}

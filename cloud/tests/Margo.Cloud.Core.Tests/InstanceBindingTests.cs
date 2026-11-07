using Xunit;

namespace Margo.Cloud.Core.Tests;

/// <summary>tests/test_autopilot_guard.py InstanceBindingTests and the binding used across
/// tests/test_autopilot_runtime.py.</summary>
public sealed class InstanceBindingTests
{
    private static readonly InstanceBinding Binding = new("agent-identity-1", "margo-user-1", "principal-1", "chat-principal-1");

    [Fact]
    public void Binding_rejects_ambiguous_identity()
    {
        Assert.Throws<BindingException>(() => new InstanceBinding("agent-identity-1", "same-1", "same-1", "chat-1"));
        Assert.Throws<BindingException>(() => new InstanceBinding("agent identity", "margo-user-1", "principal-1", "chat-1"));
        Assert.Throws<BindingException>(() => new InstanceBinding("agent-identity-1", "margo-user-1", string.Empty, "chat-1"));
    }

    [Fact]
    public void Account_key_and_principal_check()
    {
        Assert.Equal("autopilot:agent-identity-1:principal-1", Binding.Account);
        var other = new InstanceBinding("agent-identity-2", "margo-user-2", "principal-1", "chat-2");
        Assert.NotEqual(Binding.Account, other.Account);
        Assert.True(Binding.IsPrincipal("principal-1"));
        Assert.False(Binding.IsPrincipal("Principal One"));
        Assert.False(Binding.IsPrincipal(null));
        Assert.False(Binding.IsPrincipal("PRINCIPAL-1"));
    }

    [Fact]
    public void Messages_name_the_python_field()
    {
        Assert.Equal("agent_identity_id must be an explicit opaque identifier",
            Assert.Throws<BindingException>(() => new InstanceBinding(null!, "u", "p")).Message);
        Assert.Equal("agent_user_id must be an explicit opaque identifier",
            Assert.Throws<BindingException>(() => new InstanceBinding("a", "u#1", "p")).Message);
        Assert.Equal("principal_id must be an explicit opaque identifier",
            Assert.Throws<BindingException>(() => new InstanceBinding("a", "u", "p?")).Message);
        Assert.Equal("principal_chat_id must be an explicit opaque identifier",
            Assert.Throws<BindingException>(() => new InstanceBinding("a", "u", "p", string.Empty)).Message);
        Assert.Equal("the principal cannot be the agent's own user account",
            Assert.Throws<BindingException>(() => new InstanceBinding("a", "u", "u")).Message);
    }

    [Fact]
    public void Fields_are_checked_in_python_order()
    {
        // Every field is bad: the first one in declaration order is reported.
        Assert.StartsWith("agent_identity_id", Assert.Throws<BindingException>(() => new InstanceBinding("", "", "", "")).Message, StringComparison.Ordinal);
        Assert.StartsWith("principal_chat_id", Assert.Throws<BindingException>(() => new InstanceBinding("a", "u", "u", "")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Principal_chat_is_optional_and_bindings_compare_by_value()
    {
        var unbound = new InstanceBinding("agent-identity-1", "margo-user-1", "principal-1");
        Assert.Null(unbound.PrincipalChatId);
        Assert.Equal(Binding.Account, unbound.Account);
        Assert.NotEqual(Binding, unbound);
        Assert.Equal(Binding, new InstanceBinding("agent-identity-1", "margo-user-1", "principal-1", "chat-principal-1"));
        Assert.Equal(Binding, Binding with { }); // a copy keeps the validated values; properties cannot be re-set
    }

    [Fact]
    public void The_agent_identity_may_equal_the_principal()
    {
        // Python checks only principal against agent user, and identity against user in instance_key.
        Assert.Equal("autopilot:same:same", new InstanceBinding("same", "u", "same").Account);
    }
}

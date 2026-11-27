using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using static Margo.Cloud.Core.Tests.Fixtures;

namespace Margo.Cloud.Core.Tests;

/// <summary>The former Python instance tests' NamesAndRoutes.test_keys_are_stable_opaque_and_valid_azure_names, and more.</summary>
public sealed class NamesTests
{
    [Fact]
    public void Keys_are_stable_opaque_and_valid_azure_names()
    {
        var key = Instances.InstanceKey(Identity, User);
        Assert.Equal(key, Instances.InstanceKey(Identity, User));
        Assert.NotEqual(key, Instances.InstanceKey(Identity, "margo-user-2"));
        Assert.Matches("^[0-9a-f]{20}$", key);
        var names = Instances.ResourceNames(key);
        Assert.Matches("^[a-z][a-z0-9-]{1,30}[a-z0-9]$", names.App);
        Assert.True(names.App.Length <= 32);
        Assert.Matches("^[a-z0-9][a-z0-9-]{2,62}$", names.Container);
        Assert.Throws<RouteException>(() => Instances.ResourceNames("../escape"));
    }

    [Theory]
    [InlineData("same", "same")]
    [InlineData("", User)]
    [InlineData("a b", User)]
    [InlineData(null, User)]
    [InlineData(Identity, null)]
    public void Bad_identifiers_are_value_errors(string? identity, string? user)
    {
        var error = Assert.Throws<BindingException>(() => Instances.InstanceKey(identity, user));
        Assert.IsAssignableFrom<ArgumentException>(error); // Python: BindingError is a ValueError
    }

    [Fact]
    public void Key_messages_name_the_field_like_python()
    {
        Assert.Equal("agent_identity_id must be an explicit opaque identifier",
            Assert.Throws<BindingException>(() => Instances.InstanceKey("", User)).Message);
        Assert.Equal("agent_user_id must be an explicit opaque identifier",
            Assert.Throws<BindingException>(() => Instances.InstanceKey(Identity, "a/b")).Message);
        Assert.Equal("agent identity and agent user must differ",
            Assert.Throws<BindingException>(() => Instances.InstanceKey("same", "same")).Message);
    }

    [Fact]
    public void Keys_match_the_python_implementation()
    {
        // Golden values from the Python instances.py instance_key.
        Assert.Equal(Key, Instances.InstanceKey(Identity, User));
        Assert.Equal("a0f90081efd683453c1a", Instances.InstanceKey(Identity, "margo-user-2"));
        Assert.Equal("7688250577852fdae674", Instances.InstanceKey("agent-" + U(0xe9) + "-1", "user-" + U(0x1f600)));
    }

    [Fact]
    public void A_lone_surrogate_fails_encoding_not_binding_as_in_python()
    {
        // Python: UnicodeEncodeError (a ValueError, not a BindingError), so resolve() lets it through.
        Assert.Throws<EncoderFallbackException>(() => Instances.InstanceKey("agent" + LoneSurrogate, User));
        Assert.Throws<EncoderFallbackException>(() => new RouteTable().Resolve("agent" + LoneSurrogate, User));
    }

    public static TheoryData<string, bool> IdentifierCases() => new()
    {
        // Expected results from instance._check in Python 3.13.
        { "a", true },
        { new string('x', 256), true },
        { new string('x', 257), false },
        { string.Concat(Enumerable.Repeat(U(0x1f600), 256)), true }, // 256 code points, 512 UTF-16 units
        { string.Concat(Enumerable.Repeat(U(0x1f600), 257)), false },
        { "a" + U(0x200b) + "b", true },  // zero-width space is not whitespace to Python
        { "a" + U(0xa0) + "b", false },
        { "a" + U(0x2028) + "b", false },
        { "a" + U(0x1c) + "b", false },
        { "a" + U(0x7f) + "b", false },
        { "a" + U(0x85) + "b", false },
        { "a" + U(0x3000) + "b", false },
        { "a" + U(0x180e) + "b", true },
        { "a" + U(0xfeff) + "b", true },
        { "a{b", false },
        { "a}b", false },
        { "a/b", false },
        { "a?b", false },
        { "a#b", false },
        { "a\tb", false },
        { "caf" + U(0xe9), true },
        { "a:b@example.com", true },
    };

    [Theory]
    [MemberData(nameof(IdentifierCases))]
    public void Opaque_identifiers_follow_the_python_pattern(string value, bool valid)
    {
        Assert.Equal(valid, OpaqueIdentifier.IsValid(value));
    }

    [Fact]
    public void Internal_urls_need_a_plain_domain_and_a_key()
    {
        Assert.Equal("https://margo-" + Key + ".internal." + Domain, Instances.InternalUrl(Key, Domain));
        foreach (var domain in new[] { null, "", "ab", "Example.com", "a b.example.com", "x_y.example.com", new string('a', 254) })
        {
            Assert.Equal("environment domain is required", Assert.Throws<RouteException>(() => Instances.InternalUrl(Key, domain)).Message);
        }

        Assert.Equal("not an instance key", Assert.Throws<RouteException>(() => Instances.InternalUrl("zz", Domain)).Message);
        Assert.Equal("https://margo-" + Key + ".internal." + new string('a', 253), Instances.InternalUrl(Key, new string('a', 253)));
    }

    [Fact]
    public void Instance_keys_are_twenty_lower_case_hex_digits_only()
    {
        Assert.True(Instances.IsInstanceKey(Key));
        Assert.False(Instances.IsInstanceKey(Key.ToUpperInvariant()));
        Assert.False(Instances.IsInstanceKey(Key + "\n")); // Python fullmatch, not a $ anchor
        Assert.False(Instances.IsInstanceKey(Key[..19]));
        Assert.False(Instances.IsInstanceKey(null));
        Assert.Equal(new InstanceResourceNames("margo-" + Key, "id-margo-" + Key, "margo-i-" + Key), Instances.ResourceNames(Key));
    }

    [Fact]
    public void Error_types_are_sealed_value_errors()
    {
        // cloud/README.md: sealed by default. Python's BindingError and RouteError are unrelated ValueErrors.
        Assert.True(typeof(BindingException).IsSealed);
        Assert.True(typeof(RouteException).IsSealed);
        Assert.Equal(typeof(ArgumentException), typeof(BindingException).BaseType);
        Assert.Equal(typeof(ArgumentException), typeof(RouteException).BaseType);
    }

    [Fact]
    public void Resource_names_fit_azure_limits_for_any_key()
    {
        var names = Instances.ResourceNames(Key);
        Assert.Matches(new Regex("^[a-z0-9][a-z0-9-]{2,62}$"), names.Identity);
        Assert.True(names.Container.Length <= 63);
    }
}

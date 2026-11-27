namespace Margo.Cloud.Core;

/// <summary>Opaque platform identifiers: never empty, never more than 256 code points, no whitespace,
/// control characters or URL delimiters. Python: <c>instance._ID</c> and <c>instance._check</c>.</summary>
public static class OpaqueIdentifier
{
    /// <summary>The longest identifier accepted, in code points.</summary>
    public const int MaxLength = 256;

    /// <summary>Whether <paramref name="value"/> matches <c>[^\s\x00-\x1f\x7f{}/?#]{1,256}</c> in full.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || PythonText.CodePointCount(value) > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c < ' ' || c == '\u007f' || c is '{' or '}' or '/' or '?' or '#' || PythonText.IsSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns <paramref name="value"/>, or throws <see cref="BindingException"/> naming the field.</summary>
    /// <param name="name">The Python field name used in the message, such as <c>agent_user_id</c>.</param>
    /// <param name="value">The identifier to check.</param>
    public static string Check(string name, string? value)
    {
        if (!IsValid(value))
        {
            throw new BindingException(name + " must be an explicit opaque identifier");
        }

        return value!;
    }
}

/// <summary>
/// Identity binding for one hired instance. Recorded at hire time from authenticated platform data,
/// never from message content. Python: <c>instance.InstanceBinding</c>.
/// </summary>
/// <remarks>
/// <para>An instance serves exactly one principal (its manager). Its private state reuses the
/// account-isolated store: <see cref="Account"/> combines the instance's agent identity and the
/// principal's directory object ID, so two instances, or one instance re-bound to another person,
/// can never open each other's ledger or memory.</para>
/// <para><see cref="PrincipalChatId"/> is the Graph chat ID of the manager's one-to-one chat with
/// Margo, recorded at hire. It is not the activity's conversation ID, which Teams formats
/// differently. <see langword="null"/> means no chat was recorded, so the unattended principal-chat
/// post is unavailable.</para>
/// <para>The properties have no <c>init</c> accessors, so a <c>with</c> expression cannot produce an
/// unvalidated copy.</para>
/// </remarks>
public sealed record InstanceBinding
{
    /// <exception cref="BindingException">An identifier is missing or malformed, or the principal is the agent's own user.</exception>
    public InstanceBinding(string agentIdentityId, string agentUserId, string principalId, string? principalChatId = null)
    {
        AgentIdentityId = OpaqueIdentifier.Check("agent_identity_id", agentIdentityId);
        AgentUserId = OpaqueIdentifier.Check("agent_user_id", agentUserId);
        PrincipalId = OpaqueIdentifier.Check("principal_id", principalId);
        if (principalChatId is not null)
        {
            OpaqueIdentifier.Check("principal_chat_id", principalChatId);
        }

        PrincipalChatId = principalChatId;
        if (string.Equals(PrincipalId, AgentUserId, StringComparison.Ordinal))
        {
            throw new BindingException("the principal cannot be the agent's own user account");
        }
    }

    public string AgentIdentityId { get; }

    public string AgentUserId { get; }

    public string PrincipalId { get; }

    public string? PrincipalChatId { get; }

    /// <summary>Private-state account key; deliberately not an email address or display name.</summary>
    public string Account => "autopilot:" + AgentIdentityId + ":" + PrincipalId;

    /// <summary>Authority comes from the authenticated activity's object ID, never a name or header.</summary>
    public bool IsPrincipal(string? senderId) => senderId is not null && string.Equals(senderId, PrincipalId, StringComparison.Ordinal);
}

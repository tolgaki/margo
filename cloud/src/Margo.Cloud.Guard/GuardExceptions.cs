using Margo.Cloud.State.Core;

namespace Margo.Cloud.Guard;

/// <summary>
/// The guard was wired to something other than the one permitted endpoint, or to an unknown principal access
/// mode (<c>guard.py</c> <c>GuardConfigError</c>, a <c>ValueError</c>).
/// </summary>
public sealed class GuardConfigException : ArgumentException
{
    public GuardConfigException()
    {
    }

    public GuardConfigException(string message)
        : base(message)
    {
    }

    public GuardConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The requested subject's token cannot be produced: revoked, expired or refused by policy
/// (<c>guard.py</c> <c>TokenUnavailable</c>). Its message is shown to the model in the <c>blocked</c> reason,
/// so it must never contain a token.
/// </summary>
public sealed class TokenUnavailableException : Exception
{
    /// <summary>No detail; the message is empty, as Python's <c>str(TokenUnavailable())</c> is.</summary>
    public TokenUnavailableException()
        : base(string.Empty)
    {
    }

    public TokenUnavailableException(string message)
        : base(message)
    {
    }

    public TokenUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The execution claim is missing, settled, mismatched or otherwise unusable (<c>claims.py</c> <c>ClaimError</c>,
/// a <c>ValueError</c>).
/// </summary>
/// <remarks><see cref="Exception.InnerException"/> is the exception it was raised from (<c>raise ... from exc</c>).</remarks>
public sealed class ClaimException : PyValueError
{
    public ClaimException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public override string PythonType => "ClaimError";
}

/// <summary>The call is refused by policy (<c>guard.py</c> <c>_Refused</c>); its message is the result's reason.</summary>
internal sealed class RefusedException : Exception
{
    public RefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A principal source this access mode cannot reach; reported as unavailable, not empty
/// (<c>guard.py</c> <c>_Unavailable</c>).
/// </summary>
internal sealed class UnavailableException : Exception
{
    public UnavailableException(string message)
        : base(message)
    {
    }
}

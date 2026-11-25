namespace Margo.Cloud.Guard;

/// <summary>
/// The tokens one turn may use (the object the Python guard calls <c>tokens.token_for(subject)</c> on, for
/// example the orchestrator's <c>TurnTokens</c>). The guard chooses the subject; the model never sees a token.
/// </summary>
/// <remarks>
/// Synchronous, as in Python: hosts acquire the turn's tokens before the turn and hand them over. A missing
/// token must throw <see cref="TokenUnavailableException"/> (its message reaches the model, so it must not hold
/// a token); there is never a fallback to the other subject. Any other exception is reported as a "token
/// provider error" without its message.
/// </remarks>
public interface ITokenProvider
{
    /// <summary>The bearer token for <paramref name="subject"/>: <see cref="Subjects.Principal"/> or <see cref="Subjects.Margo"/>.</summary>
    /// <exception cref="TokenUnavailableException">The token cannot be produced.</exception>
    string TokenFor(string subject);
}

/// <summary>
/// A token provider that can fetch a fresh token for a subject when Work IQ answers 401 (the token expired during a
/// long turn). The guard asks once per read; a write is never resent.
/// </summary>
public interface IRefreshingTokenProvider : ITokenProvider
{
    /// <summary>A fresh token for <paramref name="subject"/>, or null when none can be had.</summary>
    Task<string?> RefreshAsync(string subject, CancellationToken cancellationToken = default);
}

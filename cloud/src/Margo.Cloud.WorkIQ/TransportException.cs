namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Work IQ answered with an error or could not be reached (<c>guard.py</c> <c>TransportError</c>).
/// </summary>
/// <remarks>
/// The guard treats a write that failed with a <see cref="StatusCode"/> in its rejected-before-acting set
/// as a no-effect failure, and any other write failure as an unknown outcome.
/// </remarks>
public class TransportException : Exception
{
    /// <summary>Create a transport error without a status code.</summary>
    public TransportException()
    {
    }

    /// <summary>Create a transport error without a status code.</summary>
    public TransportException(string message)
        : base(message)
    {
    }

    /// <summary>Create a transport error without a status code.</summary>
    public TransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Create a transport error carrying the HTTP or tool status code, when there was one.</summary>
    public TransportException(string message, int? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status, or the tool result's integer <c>statusCode</c>; <c>null</c> when there was none.</summary>
    public int? StatusCode { get; }
}

/// <summary>No response arrived in time; the effect of a write is unknown (<c>guard.py</c> <c>TransportTimeout</c>).</summary>
public sealed class TransportTimeoutException : TransportException
{
    /// <summary>Create a timeout.</summary>
    public TransportTimeoutException()
    {
    }

    /// <summary>Create a timeout.</summary>
    public TransportTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Create a timeout.</summary>
    public TransportTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The server no longer recognises the MCP session, so the request was not processed
/// (<c>transport.py</c> <c>_UnknownSession</c>).
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="TransportException"/>: when it escapes the transport (the
/// <c>notifications/initialized</c> post of a session, or the one permitted resend, is answered 404),
/// Python's guard sees a plain exception and records an unknown transport failure.
/// </remarks>
internal sealed class McpUnknownSessionException : Exception
{
    public McpUnknownSessionException()
        : base("Work IQ no longer recognises the MCP session")
    {
    }

    public McpUnknownSessionException(string message)
        : base(message)
    {
    }

    public McpUnknownSessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A failure that Python's transport raises as something other than <c>TransportError</c>
/// (<c>TypeError</c>, <c>AttributeError</c>, <c>ValueError</c> from header checks, <c>RecursionError</c>).
/// </summary>
/// <remarks>
/// Kept distinct from <see cref="TransportException"/> so the guard classifies it exactly as Python does:
/// an unknown transport failure, never a known error.
/// </remarks>
internal sealed class TransportFaultException : Exception
{
    public TransportFaultException()
    {
    }

    public TransportFaultException(string message)
        : base(message)
    {
    }

    public TransportFaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

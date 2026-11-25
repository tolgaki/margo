namespace Margo.Cloud.WorkIQ;

/// <summary>
/// Work IQ answered with an error or could not be reached .
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

    /// <summary>
    /// The server's own error text (a JSON-RPC error message), clipped. It may echo the request, so it goes back to the
    /// model and never into a receipt or a log.
    /// </summary>
    public string? ServerMessage { get; init; }
}

/// <summary>No response arrived in time; the effect of a write is unknown.</summary>
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
/// Work IQ's answer was larger than <see cref="McpTransportOptions.MaxResponseBytes"/> and was not read to the end. The request was processed: a read can be asked again for less,
/// and a write's outcome is unknown, as for any <see cref="TransportException"/> without a status code.
/// </summary>
public sealed class TransportResponseTooLargeException : TransportException
{
    public TransportResponseTooLargeException()
    {
    }

    public TransportResponseTooLargeException(string message)
        : base(message)
    {
    }

    public TransportResponseTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

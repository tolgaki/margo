namespace Margo.Cloud.Core;

/// <summary>
/// The instance binding is incomplete or ambiguous; refuse to run. Python: <c>instance.BindingError</c>,
/// a <c>ValueError</c>, hence an <see cref="ArgumentException"/> here.
/// </summary>
public sealed class BindingException : ArgumentException
{
    public BindingException()
    {
    }

    public BindingException(string message)
        : base(message)
    {
    }

    public BindingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The route table or a message is malformed; nothing is changed. Python: <c>instances.RouteError</c>,
/// a <c>ValueError</c>, hence an <see cref="ArgumentException"/> here.
/// </summary>
public sealed class RouteException : ArgumentException
{
    public RouteException()
    {
    }

    public RouteException(string message)
        : base(message)
    {
    }

    public RouteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

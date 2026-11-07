using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Helpers.Python;

// Python exception classes the stateless helpers can raise that State.Core does not model yet. They belong
// in State.Core's PyErrors.cs (see PORTING.md); they live here so this port does not edit another project.

/// <summary>Python <c>AttributeError</c>, for example <c>'list' object has no attribute 'get'</c>.</summary>
public sealed class PyAttributeError : PyException
{
    public PyAttributeError(string message) : base(message)
    {
    }

    public override string PythonType => "AttributeError";
}

/// <summary>
/// Python <c>KeyError</c> for a key that is not a string, such as <c>KeyError(slice(None, 10, None))</c>:
/// <c>str(exc)</c> is the repr of the key, given here as text.
/// </summary>
public sealed class PyKeyErrorRepr : PyKeyError
{
    private readonly string _repr;

    public PyKeyErrorRepr(string repr) : base(repr)
    {
        _repr = repr;
    }

    public override string Message => _repr;
}

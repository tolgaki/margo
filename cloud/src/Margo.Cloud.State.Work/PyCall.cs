using System.Globalization;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// The <c>TypeError</c>s CPython 3.13 raises when a method is called with <c>**data</c> from model-supplied
/// JSON (<c>ledger.source(**data)</c>): unexpected keywords (with the "Did you mean" suggestion of
/// <c>Python/suggestions.c</c>), duplicate values and missing required arguments, in CPython's order.
/// (Belongs in State.Core: every script that dispatches with <c>**data</c> needs it.)
/// </summary>
public static class PyCall
{
    private const int MoveCost = 2;
    private const int CaseCost = 1;
    private const int MaxStringSize = 40;
    private const int MaxCandidateItems = 750;

    /// <summary>
    /// The call-site merge of explicit keywords with <c>**data</c>
    /// (<c>f(x, revision=r, **data)</c>): the first key of <paramref name="data"/> that is also explicit raises
    /// <c>"{function} got multiple values for keyword argument '{key}'"</c>, where <paramref name="function"/> is
    /// <c>module.Class.method()</c>.
    /// </summary>
    public static void MergeKeywords(string function, IReadOnlyCollection<string> explicitKeywords, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(explicitKeywords);
        ArgumentNullException.ThrowIfNull(data);
        foreach (var (key, _) in data)
        {
            if (explicitKeywords.Contains(key))
            {
                throw new PyTypeError(function + " got multiple values for keyword argument '" + key + "'");
            }
        }
    }

    /// <summary>
    /// Binds keyword arguments to a function's parameters as CPython's <c>initialize_locals</c> does.
    /// <paramref name="parameters"/> are all positional-or-keyword parameters in order (including <c>self</c>);
    /// the first <paramref name="required"/> have no default; the first <paramref name="positional"/> were passed
    /// positionally. <paramref name="keywords"/> are the keyword names in call order (explicit ones, then the
    /// dict's). Returns nothing; throws <c>TypeError</c> with Python's message.
    /// </summary>
    public static void Bind(string qualname, IReadOnlyList<string> parameters, int required, int positional,
                            IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(qualname);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(keywords);
        var bound = new bool[parameters.Count];
        for (var i = 0; i < positional && i < parameters.Count; i++)
        {
            bound[i] = true;
        }

        foreach (var keyword in keywords)
        {
            var index = -1;
            for (var i = 0; i < parameters.Count; i++)
            {
                if (string.Equals(parameters[i], keyword, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                var suggestion = Suggest(parameters, keyword);
                throw new PyTypeError(qualname + "() got an unexpected keyword argument '" + keyword + "'"
                                      + (suggestion is null ? "" : ". Did you mean '" + suggestion + "'?"));
            }

            if (bound[index])
            {
                throw new PyTypeError(qualname + "() got multiple values for argument '" + keyword + "'");
            }

            bound[index] = true;
        }

        var missing = new List<string>();
        for (var i = 0; i < required; i++)
        {
            if (!bound[i])
            {
                missing.Add("'" + parameters[i] + "'");
            }
        }

        if (missing.Count > 0)
        {
            var names = missing.Count switch
            {
                1 => missing[0],
                2 => missing[0] + " and " + missing[1],
                _ => string.Join(", ", missing.Take(missing.Count - 1)) + ", and " + missing[^1],
            };
            throw new PyTypeError(string.Format(CultureInfo.InvariantCulture, "{0}() missing {1} required positional argument{2}: {3}",
                                                qualname, missing.Count, missing.Count == 1 ? "" : "s", names));
        }
    }

    /// <summary><c>_Py_CalculateSuggestions(candidates, name)</c>: the closest candidate, or null.</summary>
    public static string? Suggest(IReadOnlyList<string> candidates, string name)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(name);
        if (candidates.Count >= MaxCandidateItems)
        {
            return null;
        }

        byte[] nameBytes;
        try
        {
            nameBytes = PyIO.EncodeUtf8(name);
        }
        catch (PyUnicodeError)
        {
            return null;
        }

        string? suggestion = null;
        var suggestionDistance = long.MaxValue;
        foreach (var item in candidates)
        {
            if (name == "_")
            {
                continue;
            }

            var itemBytes = PyIO.EncodeUtf8(item);
            if (string.Equals(name, item, StringComparison.Ordinal))
            {
                continue;
            }

            // No more than 1/3 of the involved characters should need changed.
            long maxDistance = (nameBytes.Length + itemBytes.Length + 3) * MoveCost / 6;
            // Don't take matches we've already beaten.
            maxDistance = Math.Min(maxDistance, suggestionDistance - 1);
            var current = Levenshtein(nameBytes, itemBytes, maxDistance);
            if (current > maxDistance)
            {
                continue;
            }

            if (suggestion is null || current < suggestionDistance)
            {
                suggestion = item;
                suggestionDistance = current;
            }
        }

        return suggestion;
    }

    private static int SubstitutionCost(int a, int b)
    {
        if ((a & 31) != (b & 31))
        {
            return MoveCost;
        }

        if (a == b)
        {
            return 0;
        }

        if (a is >= 'A' and <= 'Z')
        {
            a += 'a' - 'A';
        }

        if (b is >= 'A' and <= 'Z')
        {
            b += 'a' - 'A';
        }

        return a == b ? CaseCost : MoveCost;
    }

    private static long Levenshtein(byte[] first, byte[] second, long maxCost)
    {
        int aStart = 0, aSize = first.Length, bStart = 0, bSize = second.Length;
        // Trim away common affixes.
        while (aSize > 0 && bSize > 0 && first[aStart] == second[bStart])
        {
            aStart++;
            aSize--;
            bStart++;
            bSize--;
        }

        while (aSize > 0 && bSize > 0 && first[aStart + aSize - 1] == second[bStart + bSize - 1])
        {
            aSize--;
            bSize--;
        }

        if (aSize == 0 || bSize == 0)
        {
            return (long)(aSize + bSize) * MoveCost;
        }

        if (aSize > MaxStringSize || bSize > MaxStringSize)
        {
            return maxCost + 1;
        }

        var a = first.AsSpan(aStart, aSize);
        var b = second.AsSpan(bStart, bSize);
        // Prefer shorter buffer.
        if (b.Length < a.Length)
        {
            var swap = a;
            a = b;
            b = swap;
        }

        // Quick fail when a match is impossible.
        if ((long)(b.Length - a.Length) * MoveCost > maxCost)
        {
            return maxCost + 1;
        }

        var buffer = new long[a.Length];
        long tmp = MoveCost;
        for (var i = 0; i < a.Length; i++)
        {
            buffer[i] = tmp;
            tmp += MoveCost;
        }

        long result = 0;
        for (var bIndex = 0; bIndex < b.Length; bIndex++)
        {
            var code = (int)(sbyte)b[bIndex];
            long distance = result = (long)bIndex * MoveCost;
            var minimum = long.MaxValue;
            for (var index = 0; index < a.Length; index++)
            {
                var substitute = distance + SubstitutionCost(code, (sbyte)a[index]);
                distance = buffer[index];
                var insertDelete = Math.Min(result, distance) + MoveCost;
                result = Math.Min(insertDelete, substitute);
                buffer[index] = result;
                if (result < minimum)
                {
                    minimum = result;
                }
            }

            if (minimum > maxCost)
            {
                return maxCost + 1;
            }
        }

        return result;
    }
}

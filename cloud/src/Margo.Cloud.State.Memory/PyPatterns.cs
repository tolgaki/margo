namespace Margo.Cloud.State.Memory;

/// <summary>
/// The regular expressions of memory_store.py and memory_governance.py, matched with Python <c>re</c> semantics
/// over code points (<c>\w</c>, <c>\s</c> and <c>\b</c> from CPython 3.13's Unicode tables, <c>(?i)</c> with
/// CPython's extra case equivalents such as KELVIN SIGN for <c>k</c>). Each matcher documents the pattern it
/// implements; .NET's <c>Regex</c> is not used because its <c>\w</c>, <c>\s</c> and case folding differ.
/// </summary>
internal static class PyPatterns
{
    private static readonly string[] RecordPrefixes = ["mem", "item", "outcome", "meeting", "artifact", "feedback", "rule", "action"];
    private static readonly string[] IdentifierPrefixes = ["mem_", "act_", "work_"];
    private static readonly string[] CredentialWords = ["bearer", "password", "refresh.token", "api.key"];

    private static bool AsciiLetter(int c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool AsciiDigit(int c) => c is >= '0' and <= '9';

    private static bool LowerHex(int c) => AsciiDigit(c) || c is >= 'a' and <= 'f';

    private static bool AnyHex(int c) => LowerHex(c) || c is >= 'A' and <= 'F';

    /// <summary><c>re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*:[^\s]+", value)</c>.</summary>
    public static bool NamespacedKey(string value)
    {
        var points = Py.Points(value);
        if (points.Length == 0 || !AsciiLetter(points[0]))
        {
            return false;
        }

        var index = 1;
        while (index < points.Length && (AsciiLetter(points[index]) || AsciiDigit(points[index]) || points[index] is '_' or '.' or '-'))
        {
            index++;
        }

        if (index >= points.Length || points[index] != ':' || index + 1 >= points.Length)
        {
            return false;
        }

        for (var i = index + 1; i < points.Length; i++)
        {
            if (Py.IsSpace(points[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>re.fullmatch(r"(?:mem|item|outcome|meeting|artifact|feedback|rule|action)_[0-9a-f]+", value)</c>.</summary>
    public static bool RecordKey(string value)
    {
        foreach (var prefix in RecordPrefixes)
        {
            var head = prefix + "_";
            if (value.Length > head.Length && value.StartsWith(head, StringComparison.Ordinal)
                && value.AsSpan(head.Length).ToArray().All(c => LowerHex(c)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>re.fullmatch(r"[0-9a-f]{64}", value)</c>.</summary>
    public static bool Sha256Hex(string value) => value.Length == 64 && value.All(c => LowerHex(c));

    /// <summary>
    /// <c>any(re.search(pattern, encoded) for pattern in patterns)</c> for export_preview's identifier, URL, path and
    /// credential patterns.
    /// </summary>
    public static bool HasPrivateMarker(string encoded)
    {
        var points = Py.Points(encoded);
        return Email(points) || encoded.Contains("http://", StringComparison.Ordinal) || encoded.Contains("https://", StringComparison.Ordinal)
               || HomePath(points) || DrivePath(points) || Uuid(points) || Identifier(points) || Credential(points);
    }

    private static bool Word(int[] points, int index) => index >= 0 && index < points.Length && Py.IsWord(points[index]);

    private static bool Boundary(int[] points, int index) => Word(points, index - 1) != Word(points, index);

    /// <summary><c>\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b</c>.</summary>
    private static bool Email(int[] points)
    {
        for (var at = 0; at < points.Length; at++)
        {
            if (points[at] != '@')
            {
                continue;
            }

            // The local part is a nonempty run of [\w.+-] ending before '@'; the match may start anywhere in it
            // where \b holds.
            var start = at;
            while (start > 0 && (Py.IsWord(points[start - 1]) || points[start - 1] is '.' or '+' or '-'))
            {
                start--;
            }

            var bounded = false;
            for (var p = start; p < at && !bounded; p++)
            {
                bounded = Boundary(points, p);
            }

            if (!bounded)
            {
                continue;
            }

            // [\w.-]+ \. [A-Za-z]{2,} \b: the dot and the letters lie inside the [\w.-] run after '@'.
            var end = at + 1;
            while (end < points.Length && (Py.IsWord(points[end]) || points[end] is '.' or '-'))
            {
                end++;
            }

            for (var dot = at + 2; dot < end; dot++)
            {
                if (points[dot] != '.')
                {
                    continue;
                }

                var letters = dot + 1;
                while (letters < points.Length && AsciiLetter(points[letters]))
                {
                    letters++;
                }

                // Only the end of the letter run can be followed by \b (inside it, both sides are word characters).
                if (letters - dot - 1 >= 2 && !Word(points, letters))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary><c>/(?:Users|home)/\S+</c>.</summary>
    private static bool HomePath(int[] points)
    {
        foreach (var head in new[] { "/Users/", "/home/" })
        {
            for (var i = 0; i + head.Length < points.Length; i++)
            {
                var match = true;
                for (var j = 0; j < head.Length && match; j++)
                {
                    match = points[i + j] == head[j];
                }

                if (match && !Py.IsSpace(points[i + head.Length]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary><c>[A-Za-z]:\\</c>.</summary>
    private static bool DrivePath(int[] points)
    {
        for (var i = 0; i + 2 < points.Length; i++)
        {
            if (AsciiLetter(points[i]) && points[i + 1] == ':' && points[i + 2] == '\\')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b</c>.</summary>
    private static bool Uuid(int[] points)
    {
        int[] groups = [8, 4, 4, 4, 12];
        for (var p = 0; p + 36 <= points.Length; p++)
        {
            if (!Boundary(points, p))
            {
                continue;
            }

            var index = p;
            var match = true;
            for (var g = 0; g < groups.Length && match; g++)
            {
                if (g > 0)
                {
                    match = points[index++] == '-';
                }

                for (var k = 0; k < groups[g] && match; k++)
                {
                    match = AnyHex(points[index++]);
                }
            }

            if (match && Boundary(points, p + 36))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>\b(?:mem_|act_|work_)[0-9a-f]{16,}</c>.</summary>
    private static bool Identifier(int[] points)
    {
        for (var p = 0; p < points.Length; p++)
        {
            if (!Boundary(points, p))
            {
                continue;
            }

            foreach (var prefix in IdentifierPrefixes)
            {
                if (!StartsWith(points, p, prefix))
                {
                    continue;
                }

                var count = 0;
                for (var i = p + prefix.Length; i < points.Length && LowerHex(points[i]); i++)
                {
                    count++;
                }

                if (count >= 16)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary><c>(?i)\b(?:bearer|password|refresh.token|api.key)\s*[:=]</c>.</summary>
    private static bool Credential(int[] points)
    {
        for (var p = 0; p < points.Length; p++)
        {
            if (!Boundary(points, p))
            {
                continue;
            }

            foreach (var word in CredentialWords)
            {
                var index = p;
                var match = true;
                foreach (var expected in word)
                {
                    if (index >= points.Length)
                    {
                        match = false;
                        break;
                    }

                    var actual = points[index++];
                    match = expected == '.' ? actual != '\n' : IgnoreCaseEquals(expected, actual);
                    if (!match)
                    {
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                while (index < points.Length && Py.IsSpace(points[index]))
                {
                    index++;
                }

                if (index < points.Length && points[index] is ':' or '=')
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether <c>re.fullmatch("(?i)" + letter, chr(actual))</c> for an ASCII lowercase letter.</summary>
    private static bool IgnoreCaseEquals(char letter, int actual) =>
        actual == letter || actual == char.ToUpperInvariant(letter)
        || (MemoryUnicodeData.IgnoreCaseExtras.TryGetValue(letter, out var extras) && extras.Contains(actual));

    private static bool StartsWith(int[] points, int index, string text)
    {
        if (index + text.Length > points.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (points[index + i] != text[i])
            {
                return false;
            }
        }

        return true;
    }
}

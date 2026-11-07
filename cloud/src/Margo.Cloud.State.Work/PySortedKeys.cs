using System.Numerics;
using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.State.Work;

/// <summary>
/// The <c>TypeError</c> CPython 3.13 raises for <c>", ".join(sorted(set(data) - allowed))</c> when <c>data</c> holds
/// keys that are not strings (only reachable through <c>dict.update</c> with a list of pairs, whose keys may be
/// <c>None</c>, bools, ints or floats). Python's text names the two types of the first comparison
/// <c>list.sort</c> makes that fails, or the first non-string item <c>str.join</c> meets, so it depends on the
/// set's iteration order. That order is reproduced here: CPython's hashes of <c>None</c> and numbers (which do
/// not depend on <c>PYTHONHASHSEED</c>), <c>setobject.c</c>'s insertion (linear probes, perturbation, resizes),
/// <c>set(dict)</c> pre-sizing, <c>set_difference</c>, and <c>listobject.c</c>'s <c>count_run</c> and
/// <c>binarysort</c>. String hashes are randomised per process, so their slots cannot be known: they are assumed
/// not to collide with the other keys, and unknown string keys are placed after them (the likely order). Only
/// then (and for 64+ keys that sort into runs of incomparable types) can Python's own text vary between runs.
/// (Belongs in State.Core with <see cref="PyDynamic"/>.)
/// </summary>
internal static class PySortedKeys
{
    private const int LinearProbes = 9;
    private const int PerturbShift = 5;
    private const int MinMerge = 64;
    private const long NoneHash = 0xFCA86420; // object.c none_hash (constant since 3.12)
    private const long InfinityHash = 314159;
    private static readonly BigInteger Modulus = (BigInteger.One << 61) - 1;

    /// <summary>
    /// The error of <c>", ".join(sorted(set(data) - allowed))</c>: <paramref name="foreign"/> are the dict's
    /// non-string keys in insertion order, <paramref name="unknownStrings"/> its string keys outside
    /// <c>allowed</c>, <paramref name="dictSize"/> the number of keys in the dict and <paramref name="allowedSize"/>
    /// the size of <c>allowed</c>.
    /// </summary>
    public static PyTypeError JoinSortedError(IReadOnlyCollection<string> unknownStrings, IReadOnlyList<JsonNode?> foreign,
                                              int dictSize, int allowedSize)
    {
        var items = DifferenceOrder(foreign, dictSize, allowedSize);
        items.AddRange(unknownStrings.Select(text => (JsonNode?)JsonValue.Create(text)));
        try
        {
            Sort(items);
        }
        catch (PyTypeError error)
        {
            return error;
        }

        // Sorted without error: only mutually ordered non-strings remain, and join fails on the smallest.
        var smallest = items[0];
        foreach (var item in items.Skip(1))
        {
            if (Less(item, smallest))
            {
                smallest = item;
            }
        }

        return new PyTypeError("sequence item 0: expected str instance, " + PyDynamic.TypeName(smallest) + " found");
    }

    /// <summary>The iteration order of the non-string keys in <c>set(data) - allowed</c>.</summary>
    private static List<JsonNode?> DifferenceOrder(IReadOnlyList<JsonNode?> foreign, int dictSize, int allowedSize)
    {
        // set(data): set_update_dict pre-sizes the table for the whole dict, then adds the keys in dict order.
        var source = new SetTable();
        if ((long)dictSize * 5 >= (long)source.Mask * 3)
        {
            source.Resize((long)dictSize * 2);
        }

        foreach (var key in foreign)
        {
            source.Add(key, Hash(key));
        }

        var order = source.Keys();
        // set_difference copies the set and discards (keeping the slot order) when it is much larger than the
        // other operand; otherwise it adds the remaining keys, in the first set's order, to a new set.
        if ((dictSize >> 2) > allowedSize)
        {
            return order;
        }

        var result = new SetTable();
        foreach (var key in order)
        {
            result.Add(key, Hash(key));
        }

        return result.Keys();
    }

    /// <summary><c>hash(x)</c> for <c>None</c>, bools, ints and floats (<c>Python/pyhash.c</c>, <c>longobject.c</c>).</summary>
    internal static long Hash(JsonNode? node)
    {
        if (node is null)
        {
            return NoneHash;
        }

        BigInteger value;
        var negative = false;
        switch (PyJson.Scalar(node))
        {
            case null:
                return NoneHash;
            case bool flag:
                return flag ? 1 : 0;
            case BigInteger integer:
                negative = integer.Sign < 0;
                value = BigInteger.Abs(integer) % Modulus;
                break;
            case double number:
                if (double.IsInfinity(number))
                {
                    return number > 0 ? InfinityHash : -InfinityHash;
                }

                if (double.IsNaN(number))
                {
                    throw new PyTypeError("NaN keys are not supported");
                }

                // number = mantissa * 2**exponent exactly; 2**61 == 1 (mod 2**61 - 1).
                var bits = BitConverter.DoubleToInt64Bits(number);
                var biased = (int)((bits >> 52) & 0x7FF);
                var mantissa = bits & ((1L << 52) - 1);
                var exponent = biased == 0 ? -1074 : biased - 1075;
                if (biased != 0)
                {
                    mantissa |= 1L << 52;
                }

                negative = number < 0;
                value = mantissa % Modulus * BigInteger.ModPow(2, ((exponent % 61) + 61) % 61, Modulus) % Modulus;
                break;
            default:
                throw new PyTypeError("unhashable type: '" + PyDynamic.TypeName(node) + "'");
        }

        var hash = (long)value;
        if (negative)
        {
            hash = -hash;
        }

        return hash == -1 ? -2 : hash;
    }

    /// <summary><c>v &lt; w</c> as <c>PyObject_RichCompare</c> evaluates it for JSON keys.</summary>
    private static bool Less(JsonNode? left, JsonNode? right)
    {
        if (PyDynamic.IsNumber(left) && PyDynamic.IsNumber(right))
        {
            return PyDynamic.CompareNumbers(left, right) < 0;
        }

        if (PyDynamic.AsString(left) is { } a && PyDynamic.AsString(right) is { } b)
        {
            return PyRepr.CompareStrings(a, b) < 0;
        }

        throw new PyTypeError("'<' not supported between instances of '" + PyDynamic.TypeName(left) + "' and '"
                              + PyDynamic.TypeName(right) + "'");
    }

    /// <summary>
    /// <c>list.sort()</c> up to its first failing comparison: natural runs (<c>count_run</c>) extended to
    /// <c>minrun</c> by binary insertion, as <c>listsort_impl</c> does. When every run sorts, the first merge's first
    /// comparison (<c>gallop_right</c>: the second run's first key against the first run's first key) is taken
    /// between adjacent runs, left to right (an approximation of the merge order for 64+ keys).
    /// </summary>
    private static void Sort(List<JsonNode?> items)
    {
        var count = items.Count;
        if (count < 2)
        {
            return;
        }

        var minrun = MinRun(count);
        var runs = new List<int>();
        for (var low = 0; low < count;)
        {
            var remaining = count - low;
            var run = CountRun(items, low, remaining);
            if (run < minrun)
            {
                var force = remaining <= minrun ? remaining : minrun;
                BinarySort(items, low, force, run);
                run = force;
            }

            runs.Add(low);
            low += run;
        }

        for (var index = 1; index < runs.Count; index++)
        {
            Less(items[runs[index]], items[runs[index - 1]]);
        }
    }

    /// <summary><c>merge_compute_minrun</c>.</summary>
    private static int MinRun(int count)
    {
        var rest = 0;
        while (count >= MinMerge)
        {
            rest |= count & 1;
            count >>= 1;
        }

        return count + rest;
    }

    /// <summary><c>count_run</c> (CPython 3.13): the ascending run at <paramref name="low"/>, reversing a descending one.</summary>
    private static int CountRun(List<JsonNode?> items, int low, int remaining)
    {
        bool NextSmaller(int n) => Less(items[low + n], items[low + n - 1]);
        bool NextLarger(int n) => Less(items[low + n - 1], items[low + n]);

        int n;
        for (n = 1; n < remaining; ++n)
        {
            if (NextSmaller(n))
            {
                break;
            }
        }

        if (n == remaining)
        {
            return n;
        }

        if (n > 1)
        {
            if (Less(items[low], items[low + n - 1]))
            {
                return n;
            }

            items.Reverse(low, n);
        }

        ++n;
        var equal = 0;
        for (; n < remaining; ++n)
        {
            if (NextSmaller(n))
            {
                ReverseLastEqual(items, low, n, ref equal);
            }
            else if (NextLarger(n))
            {
                break;
            }
            else
            {
                ++equal;
            }
        }

        ReverseLastEqual(items, low, n, ref equal);
        items.Reverse(low, n);
        for (; n < remaining; ++n)
        {
            if (NextSmaller(n))
            {
                break;
            }
        }

        return n;
    }

    private static void ReverseLastEqual(List<JsonNode?> items, int low, int n, ref int equal)
    {
        if (equal != 0)
        {
            ++equal;
            items.Reverse(low + n - equal, equal);
            equal = 0;
        }
    }

    /// <summary><c>binarysort</c>: binary insertion of <c>items[low+ok .. low+n)</c> into the sorted prefix.</summary>
    private static void BinarySort(List<JsonNode?> items, int low, int n, int ok)
    {
        if (ok == 0)
        {
            ++ok;
        }

        for (; ok < n; ++ok)
        {
            int left = 0, right = ok;
            var pivot = items[low + ok];
            do
            {
                var middle = (left + right) >> 1;
                if (Less(pivot, items[low + middle]))
                {
                    right = middle;
                }
                else
                {
                    left = middle + 1;
                }
            }
            while (left < right);

            items.RemoveAt(low + ok);
            items.Insert(low + left, pivot);
        }
    }

    /// <summary>A <c>setobject.c</c> hash table without dummies (nothing is deleted here).</summary>
    private sealed class SetTable
    {
        private Entry?[] _slots = new Entry?[8];

        public ulong Mask { get; private set; } = 7;

        private long Fill { get; set; }

        private long Used { get; set; }

        public List<JsonNode?> Keys() => _slots.Where(slot => slot is not null).Select(slot => slot!.Key).ToList();

        /// <summary><c>set_table_resize(so, minused)</c>.</summary>
        public void Resize(long minUsed)
        {
            ulong size = 8;
            while (size <= (ulong)minUsed)
            {
                size <<= 1;
            }

            var old = _slots;
            _slots = new Entry?[size];
            Mask = size - 1;
            Fill = Used;
            foreach (var entry in old)
            {
                if (entry is not null)
                {
                    InsertClean(entry);
                }
            }
        }

        /// <summary><c>set_add_entry(so, key, hash)</c>.</summary>
        public void Add(JsonNode? key, long hash)
        {
            var mask = Mask;
            var perturb = unchecked((ulong)hash);
            var i = perturb & mask;
            while (true)
            {
                var probes = i + LinearProbes <= mask ? LinearProbes : 0;
                var j = i;
                while (true)
                {
                    var entry = _slots[j];
                    if (entry is null)
                    {
                        _slots[j] = new Entry(key, hash);
                        Fill++;
                        Used++;
                        if ((ulong)Fill * 5 >= mask * 3)
                        {
                            Resize(Used > 50000 ? Used * 2 : Used * 4);
                        }

                        return;
                    }

                    if (entry.Hash == hash && PyDynamic.Eq(entry.Key, key))
                    {
                        return;
                    }

                    if (probes-- == 0)
                    {
                        break;
                    }

                    j++;
                }

                perturb >>= PerturbShift;
                i = (i * 5 + 1 + perturb) & mask;
            }
        }

        /// <summary><c>set_insert_clean</c>.</summary>
        private void InsertClean(Entry entry)
        {
            var mask = Mask;
            var perturb = unchecked((ulong)entry.Hash);
            var i = perturb & mask;
            while (true)
            {
                if (_slots[i] is null)
                {
                    _slots[i] = entry;
                    return;
                }

                if (i + LinearProbes <= mask)
                {
                    for (ulong j = 1; j <= LinearProbes; j++)
                    {
                        if (_slots[i + j] is null)
                        {
                            _slots[i + j] = entry;
                            return;
                        }
                    }
                }

                perturb >>= PerturbShift;
                i = (i * 5 + 1 + perturb) & mask;
            }
        }

        private sealed record Entry(JsonNode? Key, long Hash);
    }
}

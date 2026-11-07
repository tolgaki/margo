using System.Numerics;
using System.Text.Json.Nodes;

namespace Margo.Cloud.State.Core;

/// <summary>
/// The <c>TypeError</c> CPython 3.13 raises for <c>", ".join(sorted(set(data) - allowed))</c> when <c>data</c> holds
/// keys that are not strings (only reachable through <c>dict.update</c> with a list of pairs, whose keys may be
/// <c>None</c>, bools, ints or floats). Python's text names the two types of the first comparison
/// <c>list.sort</c> makes that fails, or the first non-string item <c>str.join</c> meets, so it depends on the
/// set's iteration order. That order is reproduced here: CPython's hashes of <c>None</c> and numbers (which do
/// not depend on <c>PYTHONHASHSEED</c>), <c>setobject.c</c>'s insertion (linear probes, perturbation, resizes),
/// <c>set(dict)</c> pre-sizing and <c>set_difference</c>; the sort is <see cref="PySort"/> (CPython's
/// <c>listsort</c>, merges included) with <see cref="PyObj.Lt"/>. String hashes are randomised per process, so their
/// slots cannot be known: they are assumed not to collide with the other keys, and unknown string keys are placed
/// after them (the likely order). Only then can Python's own text vary between runs.
/// </summary>
internal static class PySortedKeys
{
    private const int LinearProbes = 9;
    private const int PerturbShift = 5;
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
        var sorted = items.ToArray();
        try
        {
            PySort.Sort(sorted, PyObj.Lt);
        }
        catch (PyTypeError error)
        {
            return error;
        }

        // Sorted without error: only mutually ordered non-strings remain, and join fails on the first.
        return new PyTypeError("sequence item 0: expected str instance, " + PyNode.TypeName(sorted[0]) + " found");
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
                throw new PyTypeError("unhashable type: '" + PyNode.TypeName(node) + "'");
        }

        var hash = (long)value;
        if (negative)
        {
            hash = -hash;
        }

        return hash == -1 ? -2 : hash;
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

                    if (entry.Hash == hash && PyJson.PyEquals(entry.Key, key))
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

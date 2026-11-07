using System.Numerics;

namespace Margo.Cloud.State.Core;

/// <summary>
/// CPython 3.13 arithmetic on <c>int</c> (<see cref="BigInteger"/>) and <c>float</c> (<see cref="double"/>)
/// where .NET's defaults differ: correctly rounded <c>float(int)</c> with <c>OverflowError</c>, correctly
/// rounded <c>int / int</c>, exact mixed int/float comparison, <c>sum()</c>'s compensated float summation,
/// <c>int(float)</c>, <c>float // float</c> and <c>value + n</c> on values read from SQLite.
/// </summary>
public static class PyNumbers
{
    private static readonly BigInteger TwoTo53 = BigInteger.One << 53;

    /// <summary><c>float(n)</c> (<c>PyLong_AsDouble</c>): round half to even; <c>OverflowError</c> past the float range.</summary>
    public static double IntToDouble(BigInteger value)
    {
        var magnitude = BigInteger.Abs(value);
        if (magnitude < TwoTo53)
        {
            return (double)(long)value;
        }

        var bits = (long)magnitude.GetBitLength();
        var shift = (int)(bits - 54);
        var top = magnitude >> shift; // 54 significant bits
        var sticky = !(magnitude & ((BigInteger.One << shift) - 1)).IsZero;
        var mantissa = top >> 1;
        if (!(top & 1).IsZero && (sticky || !mantissa.IsEven))
        {
            mantissa += 1;
        }

        var result = Math.ScaleB((double)(long)mantissa, shift + 1);
        if (double.IsInfinity(result))
        {
            throw new PyOverflowError("int too large to convert to float");
        }

        return value.Sign < 0 ? -result : result;
    }

    /// <summary><c>a / b</c> for Python ints (<c>long_true_divide</c>): the correctly rounded quotient.</summary>
    public static double TrueDivide(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new PyZeroDivisionError("division by zero");
        }

        if (numerator.IsZero)
        {
            return denominator.Sign < 0 ? -0.0 : 0.0;
        }

        var negative = numerator.Sign != denominator.Sign;
        var a = BigInteger.Abs(numerator);
        var b = BigInteger.Abs(denominator);
        if (a < TwoTo53 && b < TwoTo53)
        {
            var quotient = (double)(long)a / (long)b; // both exact: IEEE division rounds correctly
            return negative ? -quotient : quotient;
        }

        // Scale so the integer quotient has 55 or 56 bits, then round to 53 with a sticky remainder.
        var k = 55 - ((long)a.GetBitLength() - (long)b.GetBitLength());
        var num = k >= 0 ? a << (int)k : a;
        var den = k >= 0 ? b : b << (int)-k;
        var q = BigInteger.DivRem(num, den, out var remainder);
        var extra = (int)((long)q.GetBitLength() - 53);
        var mantissa = q >> extra;
        var dropped = q & ((BigInteger.One << extra) - 1);
        var half = BigInteger.One << (extra - 1);
        if (dropped > half || (dropped == half && (!remainder.IsZero || !mantissa.IsEven)))
        {
            mantissa += 1;
        }

        var exponent = extra - k;
        if (exponent > 2000)
        {
            throw new PyOverflowError("integer division result too large for a float");
        }

        var result = Math.ScaleB((double)(long)mantissa, (int)Math.Max(exponent, -2000));
        if (double.IsInfinity(result))
        {
            throw new PyOverflowError("integer division result too large for a float");
        }

        return negative ? -result : result;
    }

    /// <summary>
    /// Three-way comparison of two Python numbers (<c>bool</c>, <c>int</c> or <c>float</c>), exact across
    /// int and float as <c>float_richcompare</c> is. Returns null when either side is NaN (every ordering
    /// comparison is then false).
    /// </summary>
    public static int? Compare(object left, object right)
    {
        left = AsNumber(left);
        right = AsNumber(right);
        switch (left)
        {
            case BigInteger a when right is BigInteger b:
                return a.CompareTo(b);
            case double a when right is double b:
                return double.IsNaN(a) || double.IsNaN(b) ? null : a.CompareTo(b) switch { < 0 => -1, > 0 => 1, _ => 0 };
            case BigInteger a when right is double b:
                return double.IsNaN(b) ? null : CompareIntFloat(a, b);
            case double a when right is BigInteger b:
                return double.IsNaN(a) ? null : -CompareIntFloat(b, a);
            default:
                throw new ArgumentException("not a Python number");
        }
    }

    /// <summary>The sign of <c>i - d</c> computed exactly (<paramref name="d"/> is not NaN).</summary>
    public static int CompareIntFloat(BigInteger i, double d)
    {
        if (double.IsPositiveInfinity(d))
        {
            return -1;
        }

        if (double.IsNegativeInfinity(d))
        {
            return 1;
        }

        var floor = Math.Floor(d);
        var order = i.CompareTo(new BigInteger(floor));
        if (order != 0)
        {
            return order;
        }

        return floor == d ? 0 : -1;
    }

    /// <summary>Normalises a Python number: <c>bool</c> and CLR integers become <see cref="BigInteger"/>.</summary>
    public static object AsNumber(object value) => value switch
    {
        bool flag => flag ? BigInteger.One : BigInteger.Zero,
        BigInteger integer => integer,
        double number => number,
        float number => (double)number,
        long number => new BigInteger(number),
        int number => new BigInteger(number),
        _ => throw new ArgumentException("not a Python number"),
    };

    /// <summary>
    /// <c>sum(values)</c> over floats with the default start <c>0</c> (CPython 3.12+): the int <c>0</c> when
    /// empty, otherwise <c>0 + first</c> followed by Neumaier's compensated summation, the compensation
    /// added at the end only when it is non-zero and finite.
    /// </summary>
    public static object Sum(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var items = values.GetEnumerator();
        if (!items.MoveNext())
        {
            return BigInteger.Zero;
        }

        var result = 0.0 + items.Current;
        var compensation = 0.0;
        while (items.MoveNext())
        {
            var x = items.Current;
            var t = result + x;
            if (Math.Abs(result) >= Math.Abs(x))
            {
                compensation += (result - t) + x;
            }
            else
            {
                compensation += (x - t) + result;
            }

            result = t;
        }

        if (compensation != 0 && double.IsFinite(compensation))
        {
            result += compensation;
        }

        return result;
    }

    /// <summary><c>x / n</c> for a Python number <paramref name="value"/> (int or float) and an int divisor.</summary>
    public static double Divide(object value, long divisor) => AsNumber(value) switch
    {
        BigInteger integer => TrueDivide(integer, divisor),
        double number => number / divisor,
        _ => throw new ArgumentException("not a Python number"),
    };

    /// <summary><c>int(x)</c> of a float: exact and unbounded; NaN and infinities raise as in Python.</summary>
    public static BigInteger Trunc(double value) =>
        double.IsNaN(value) ? throw new PyValueError("cannot convert float NaN to integer")
        : double.IsInfinity(value) ? throw new PyOverflowError("cannot convert float infinity to integer")
        : new BigInteger(Math.Truncate(value));

    /// <summary>
    /// Python <c>value + n</c> for a value read from SQLite: an int stays an int (a sum beyond 64 bits is a
    /// <see cref="BigInteger"/>, which sqlite3 binding refuses with <c>OverflowError</c>, as in Python), a float
    /// (REAL in a hand-edited INTEGER column) stays a float; str, bytes and None raise Python's <c>TypeError</c>.
    /// </summary>
    public static object Add(object? value, long n)
    {
        switch (value)
        {
            case long integer:
                var sum = (BigInteger)integer + n;
                return sum >= long.MinValue && sum <= long.MaxValue ? (object)(long)sum : sum;
            case BigInteger big:
                return big + n;
            case double number:
                return number + n;
            case string:
                throw new PyTypeError("can only concatenate str (not \"int\") to str");
            case byte[]:
                throw new PyTypeError("can't concat int to bytes");
            default:
                throw new PyTypeError("unsupported operand type(s) for +: '" + PyObj.TypeName(value) + "' and 'int'");
        }
    }

    /// <summary>
    /// <c>float // float</c> (CPython's <c>float_floor_div</c>/<c>_float_div_mod</c>): the remainder from
    /// <c>fmod</c>, then the quotient snapped to the nearest integral value.
    /// </summary>
    public static double FloorDivide(double vx, double wx)
    {
        if (wx == 0)
        {
            throw new PyZeroDivisionError("float floor division by zero");
        }

        var mod = vx % wx; // C# % on doubles is C fmod: the remainder has the dividend's sign
        var div = (vx - mod) / wx;
        if (mod != 0)
        {
            if ((wx < 0) != (mod < 0))
            {
                div -= 1.0;
            }
        }

        double floordiv;
        if (div != 0)
        {
            floordiv = Math.Floor(div);
            if (div - floordiv > 0.5)
            {
                floordiv += 1.0;
            }
        }
        else
        {
            floordiv = Math.CopySign(0.0, vx / wx);
        }

        return floordiv;
    }
}

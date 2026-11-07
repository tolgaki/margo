namespace Margo.Cloud.Guard;

/// <summary>
/// Charged before each provider call, so work is bounded before it starts (<c>guard.py</c> <c>CallBudget</c>).
/// The engine's <c>margo-state</c> tool and the voice server charge the same type.
/// </summary>
/// <remarks>Safe to charge from several threads: the count never exceeds <see cref="Limit"/>.</remarks>
public sealed class CallBudget
{
    private int _used;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative
    /// (Python: <c>ValueError("budget limit must be a non-negative integer")</c>).</exception>
    public CallBudget(int limit)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "budget limit must be a non-negative integer");
        }

        Limit = limit;
    }

    /// <summary>The number of calls allowed.</summary>
    public int Limit { get; }

    /// <summary>The number of calls charged so far.</summary>
    public int Used => Volatile.Read(ref _used);

    /// <summary>Charge one call: <see langword="true"/> when it fits, <see langword="false"/> (and no change) when the budget is spent.</summary>
    public bool Charge()
    {
        while (true)
        {
            var used = Volatile.Read(ref _used);
            if (used >= Limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _used, used + 1, used) == used)
            {
                return true;
            }
        }
    }
}

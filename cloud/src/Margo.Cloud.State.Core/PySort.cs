namespace Margo.Cloud.State.Core;

/// <summary>
/// CPython 3.13's <c>list.sort</c> (<c>Objects/listobject.c</c>: natural runs found by <c>count_run</c>,
/// binary insertion to <c>minrun</c>, powersort merge policy, galloping <c>merge_lo</c>/<c>merge_hi</c>),
/// ported line for line so the exact sequence of <c>&lt;</c> comparisons is CPython's. That matters for
/// Python-visible behaviour: a comparison that raises (<c>TypeError</c> between a str and a list) is raised
/// for the same input, and inconsistent orders (NaN) leave the same permutation. Stable; only
/// <paramref name="lessThan"/> is consulted.
/// </summary>
public static class PySort
{
    private const int MinGallop = 7;
    private const int MaxMinrun = 64;

    /// <summary><c>items.sort()</c> in place with <c>&lt;</c> given by <paramref name="lessThan"/>.</summary>
    public static void Sort<T>(T[] items, Func<T, T, bool> lessThan)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(lessThan);
        new MergeState<T>(items, lessThan).Run();
    }

    /// <summary><c>sorted(items, key=key)</c> with Python's <c>&lt;</c> on the keys (<see cref="PyObj.Lt"/>).</summary>
    public static List<T> SortedBy<T>(IEnumerable<T> items, Func<T, object?> key) =>
        SortedBy(items, key, PyObj.Lt);

    /// <summary><c>sorted(items, key=key)</c> with a custom <c>&lt;</c> on the keys.</summary>
    public static List<T> SortedBy<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, Func<TKey, TKey, bool> lessThan)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        // All keys are computed first, in order, as list.sort does.
        var pairs = items.Select(item => new KeyValuePair<TKey, T>(key(item), item)).ToArray();
        Sort(pairs, (a, b) => lessThan(a.Key, b.Key));
        return pairs.Select(pair => pair.Value).ToList();
    }

    private struct Run
    {
        public int Base;
        public int Len;
        public int Power;
    }

    private sealed class MergeState<T>(T[] a, Func<T, T, bool> lt)
    {
        private readonly Run[] _pending = new Run[64];
        private T[] _temp = [];
        private int _minGallop = MinGallop;
        private int _n;

        public void Run()
        {
            var nremaining = a.Length;
            if (nremaining < 2)
            {
                return;
            }

            var minrun = ComputeMinrun(nremaining);
            var lo = 0;
            do
            {
                var n = CountRun(lo, nremaining);
                if (n < minrun)
                {
                    var force = nremaining <= minrun ? nremaining : minrun;
                    BinarySort(lo, force, n);
                    n = force;
                }

                FoundNewRun(n);
                _pending[_n] = new Run { Base = lo, Len = n };
                ++_n;
                lo += n;
                nremaining -= n;
            }
            while (nremaining != 0);

            MergeForceCollapse();
        }

        private static int ComputeMinrun(int n)
        {
            var r = 0;
            while (n >= MaxMinrun)
            {
                r |= n & 1;
                n >>= 1;
            }

            return n + r;
        }

        private void Reverse(int lo, int count)
        {
            Array.Reverse(a, lo, count);
        }

        private void BinarySort(int lo, int n, int ok)
        {
            if (ok == 0)
            {
                ++ok;
            }

            for (; ok < n; ++ok)
            {
                var l = 0;
                var r = ok;
                var pivot = a[lo + ok];
                do
                {
                    var m = (l + r) >> 1;
                    if (lt(pivot, a[lo + m]))
                    {
                        r = m;
                    }
                    else
                    {
                        l = m + 1;
                    }
                }
                while (l < r);

                for (var m = ok; m > l; --m)
                {
                    a[lo + m] = a[lo + m - 1];
                }

                a[lo + l] = pivot;
            }
        }

        private int CountRun(int lo, int nremaining)
        {
            int n;
            for (n = 1; n < nremaining; ++n)
            {
                if (lt(a[lo + n], a[lo + n - 1]))
                {
                    break;
                }
            }

            if (n == nremaining)
            {
                return n;
            }

            if (n > 1)
            {
                if (lt(a[lo], a[lo + n - 1]))
                {
                    return n;
                }

                Reverse(lo, n);
            }

            ++n;
            var neq = 0;
            for (; n < nremaining; ++n)
            {
                if (lt(a[lo + n], a[lo + n - 1]))
                {
                    if (neq != 0)
                    {
                        ++neq;
                        Reverse(lo + n - neq, neq);
                        neq = 0;
                    }
                }
                else if (lt(a[lo + n - 1], a[lo + n]))
                {
                    break;
                }
                else
                {
                    ++neq;
                }
            }

            if (neq != 0)
            {
                ++neq;
                Reverse(lo + n - neq, neq);
            }

            Reverse(lo, n);
            for (; n < nremaining; ++n)
            {
                if (lt(a[lo + n], a[lo + n - 1]))
                {
                    break;
                }
            }

            return n;
        }

        private int GallopLeft(T key, T[] array, int start, int n, int hint)
        {
            var p = start + hint;
            var lastofs = 0;
            var ofs = 1;
            if (lt(array[p], key))
            {
                var maxofs = n - hint;
                while (ofs < maxofs)
                {
                    if (lt(array[p + ofs], key))
                    {
                        lastofs = ofs;
                        ofs = (ofs << 1) + 1;
                    }
                    else
                    {
                        break;
                    }
                }

                if (ofs > maxofs)
                {
                    ofs = maxofs;
                }

                lastofs += hint;
                ofs += hint;
            }
            else
            {
                var maxofs = hint + 1;
                while (ofs < maxofs)
                {
                    if (lt(array[p - ofs], key))
                    {
                        break;
                    }

                    lastofs = ofs;
                    ofs = (ofs << 1) + 1;
                }

                if (ofs > maxofs)
                {
                    ofs = maxofs;
                }

                var k = lastofs;
                lastofs = hint - ofs;
                ofs = hint - k;
            }

            ++lastofs;
            while (lastofs < ofs)
            {
                var m = lastofs + ((ofs - lastofs) >> 1);
                if (lt(array[start + m], key))
                {
                    lastofs = m + 1;
                }
                else
                {
                    ofs = m;
                }
            }

            return ofs;
        }

        private int GallopRight(T key, T[] array, int start, int n, int hint)
        {
            var p = start + hint;
            var lastofs = 0;
            var ofs = 1;
            if (lt(key, array[p]))
            {
                var maxofs = hint + 1;
                while (ofs < maxofs)
                {
                    if (lt(key, array[p - ofs]))
                    {
                        lastofs = ofs;
                        ofs = (ofs << 1) + 1;
                    }
                    else
                    {
                        break;
                    }
                }

                if (ofs > maxofs)
                {
                    ofs = maxofs;
                }

                var k = lastofs;
                lastofs = hint - ofs;
                ofs = hint - k;
            }
            else
            {
                var maxofs = n - hint;
                while (ofs < maxofs)
                {
                    if (lt(key, array[p + ofs]))
                    {
                        break;
                    }

                    lastofs = ofs;
                    ofs = (ofs << 1) + 1;
                }

                if (ofs > maxofs)
                {
                    ofs = maxofs;
                }

                lastofs += hint;
                ofs += hint;
            }

            ++lastofs;
            while (lastofs < ofs)
            {
                var m = lastofs + ((ofs - lastofs) >> 1);
                if (lt(key, array[start + m]))
                {
                    ofs = m;
                }
                else
                {
                    lastofs = m + 1;
                }
            }

            return ofs;
        }

        private void EnsureTemp(int need)
        {
            if (_temp.Length < need)
            {
                _temp = new T[need];
            }
        }

        private void MergeLo(int pa, int na, int pb, int nb)
        {
            EnsureTemp(na);
            Array.Copy(a, pa, _temp, 0, na);
            var dest = pa;
            var ia = 0;
            var ib = pb;
            try
            {
                a[dest++] = a[ib++];
                --nb;
                if (nb == 0)
                {
                    goto Succeed;
                }

                if (na == 1)
                {
                    goto CopyB;
                }

                var minGallop = _minGallop;
                for (;;)
                {
                    var acount = 0;
                    var bcount = 0;
                    for (;;)
                    {
                        if (lt(a[ib], _temp[ia]))
                        {
                            a[dest++] = a[ib++];
                            ++bcount;
                            acount = 0;
                            --nb;
                            if (nb == 0)
                            {
                                goto Succeed;
                            }

                            if (bcount >= minGallop)
                            {
                                break;
                            }
                        }
                        else
                        {
                            a[dest++] = _temp[ia++];
                            ++acount;
                            bcount = 0;
                            --na;
                            if (na == 1)
                            {
                                goto CopyB;
                            }

                            if (acount >= minGallop)
                            {
                                break;
                            }
                        }
                    }

                    ++minGallop;
                    do
                    {
                        minGallop -= minGallop > 1 ? 1 : 0;
                        _minGallop = minGallop;
                        var k = GallopRight(a[ib], _temp, ia, na, 0);
                        acount = k;
                        if (k != 0)
                        {
                            Array.Copy(_temp, ia, a, dest, k);
                            dest += k;
                            ia += k;
                            na -= k;
                            if (na == 1)
                            {
                                goto CopyB;
                            }

                            if (na == 0)
                            {
                                goto Succeed;
                            }
                        }

                        a[dest++] = a[ib++];
                        --nb;
                        if (nb == 0)
                        {
                            goto Succeed;
                        }

                        k = GallopLeft(_temp[ia], a, ib, nb, 0);
                        bcount = k;
                        if (k != 0)
                        {
                            Array.Copy(a, ib, a, dest, k);
                            dest += k;
                            ib += k;
                            nb -= k;
                            if (nb == 0)
                            {
                                goto Succeed;
                            }
                        }

                        a[dest++] = _temp[ia++];
                        --na;
                        if (na == 1)
                        {
                            goto CopyB;
                        }
                    }
                    while (acount >= MinGallop || bcount >= MinGallop);

                    ++minGallop;
                    _minGallop = minGallop;
                }
            }
            catch
            {
                // CPython's Fail path: the list stays a permutation of its input.
                if (na != 0)
                {
                    Array.Copy(_temp, ia, a, dest, na);
                }

                throw;
            }

        Succeed:
            if (na != 0)
            {
                Array.Copy(_temp, ia, a, dest, na);
            }

            return;
        CopyB:
            Array.Copy(a, ib, a, dest, nb);
            a[dest + nb] = _temp[ia];
        }

        private void MergeHi(int pa, int na, int pb, int nb)
        {
            EnsureTemp(nb);
            var dest = pb + nb - 1;
            Array.Copy(a, pb, _temp, 0, nb);
            var basea = pa;
            var ib = nb - 1;
            var ia = pa + na - 1;
            try
            {
                a[dest--] = a[ia--];
                --na;
                if (na == 0)
                {
                    goto Succeed;
                }

                if (nb == 1)
                {
                    goto CopyA;
                }

                var minGallop = _minGallop;
                for (;;)
                {
                    var acount = 0;
                    var bcount = 0;
                    for (;;)
                    {
                        if (lt(_temp[ib], a[ia]))
                        {
                            a[dest--] = a[ia--];
                            ++acount;
                            bcount = 0;
                            --na;
                            if (na == 0)
                            {
                                goto Succeed;
                            }

                            if (acount >= minGallop)
                            {
                                break;
                            }
                        }
                        else
                        {
                            a[dest--] = _temp[ib--];
                            ++bcount;
                            acount = 0;
                            --nb;
                            if (nb == 1)
                            {
                                goto CopyA;
                            }

                            if (bcount >= minGallop)
                            {
                                break;
                            }
                        }
                    }

                    ++minGallop;
                    do
                    {
                        minGallop -= minGallop > 1 ? 1 : 0;
                        _minGallop = minGallop;
                        var k = GallopRight(_temp[ib], a, basea, na, na - 1);
                        k = na - k;
                        acount = k;
                        if (k != 0)
                        {
                            dest -= k;
                            ia -= k;
                            Array.Copy(a, ia + 1, a, dest + 1, k);
                            na -= k;
                            if (na == 0)
                            {
                                goto Succeed;
                            }
                        }

                        a[dest--] = _temp[ib--];
                        --nb;
                        if (nb == 1)
                        {
                            goto CopyA;
                        }

                        k = GallopLeft(a[ia], _temp, 0, nb, nb - 1);
                        k = nb - k;
                        bcount = k;
                        if (k != 0)
                        {
                            dest -= k;
                            ib -= k;
                            Array.Copy(_temp, ib + 1, a, dest + 1, k);
                            nb -= k;
                            if (nb == 1)
                            {
                                goto CopyA;
                            }

                            if (nb == 0)
                            {
                                goto Succeed;
                            }
                        }

                        a[dest--] = a[ia--];
                        --na;
                        if (na == 0)
                        {
                            goto Succeed;
                        }
                    }
                    while (acount >= MinGallop || bcount >= MinGallop);

                    ++minGallop;
                    _minGallop = minGallop;
                }
            }
            catch
            {
                if (nb != 0)
                {
                    Array.Copy(_temp, 0, a, dest - (nb - 1), nb);
                }

                throw;
            }

        Succeed:
            if (nb != 0)
            {
                Array.Copy(_temp, 0, a, dest - (nb - 1), nb);
            }

            return;
        CopyA:
            Array.Copy(a, ia + 1 - na, a, dest + 1 - na, na);
            dest -= na;
            a[dest] = _temp[ib];
        }

        private void MergeAt(int i)
        {
            var pa = _pending[i].Base;
            var na = _pending[i].Len;
            var pb = _pending[i + 1].Base;
            var nb = _pending[i + 1].Len;
            _pending[i].Len = na + nb;
            if (i == _n - 3)
            {
                _pending[i + 1] = _pending[i + 2];
            }

            --_n;
            var k = GallopRight(a[pb], a, pa, na, 0);
            pa += k;
            na -= k;
            if (na == 0)
            {
                return;
            }

            nb = GallopLeft(a[pa + na - 1], a, pb, nb, nb - 1);
            if (nb <= 0)
            {
                return;
            }

            if (na <= nb)
            {
                MergeLo(pa, na, pb, nb);
            }
            else
            {
                MergeHi(pa, na, pb, nb);
            }
        }

        private static int PowerLoop(long s1, long n1, long n2, long n)
        {
            var result = 0;
            var x = (2 * s1) + n1;
            var y = x + n1 + n2;
            for (;;)
            {
                ++result;
                if (x >= n)
                {
                    x -= n;
                    y -= n;
                }
                else if (y >= n)
                {
                    break;
                }

                x <<= 1;
                y <<= 1;
            }

            return result;
        }

        private void FoundNewRun(int n2)
        {
            if (_n == 0)
            {
                return;
            }

            var s1 = _pending[_n - 1].Base;
            var n1 = _pending[_n - 1].Len;
            var power = PowerLoop(s1, n1, n2, a.Length);
            while (_n > 1 && _pending[_n - 2].Power > power)
            {
                MergeAt(_n - 2);
            }

            _pending[_n - 1].Power = power;
        }

        private void MergeForceCollapse()
        {
            while (_n > 1)
            {
                var n = _n - 2;
                if (n > 0 && _pending[n - 1].Len < _pending[n + 1].Len)
                {
                    --n;
                }

                MergeAt(n);
            }
        }
    }
}

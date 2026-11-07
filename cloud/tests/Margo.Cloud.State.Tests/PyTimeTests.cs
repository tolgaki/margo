using Margo.Cloud.State.Core;
using Xunit;

namespace Margo.Cloud.State.Tests;

public sealed class PyTimeTests
{
    public static TheoryData<string, string, string> IsoCases => PythonFixtures.Isoformat;

    public static TheoryData<string, string> TimestampCases => PythonFixtures.Timestamps;

    [Theory]
    [MemberData(nameof(IsoCases))]
    public void Fromisoformat_matches_cpython(string kind, string input, string expected)
    {
        var (code, stdout, stderr) = ScriptRunner.Run(new StateCoreProbeScript(), ["isoformat", "--kind", kind, "--", input]);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        Assert.Equal(expected + "\n", stdout);
    }

    [Theory]
    [MemberData(nameof(TimestampCases))]
    public void Validate_timestamp_matches_margo_store(string input, string expected)
    {
        string actual;
        try
        {
            actual = StateStore.ValidateTimestamp(input);
        }
        catch (StateError ex)
        {
            actual = "!" + ex.PythonType + ": " + ex.Message;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Validate_timestamp_rejects_non_strings()
    {
        foreach (var value in new object?[] { null, 17, PyJson.Loads("17") })
        {
            Assert.Equal("timestamp must be an ISO-8601 datetime with an explicit timezone",
                         Assert.Throws<StateError>(() => StateStore.ValidateTimestamp(value)).Message);
        }

        Assert.Equal("2026-01-01T00:00:00.000000+00:00", StateStore.ValidateTimestamp(PyJson.Loads("\"2026-01-01T09:00:00+09:00\"")));
    }

    [Fact]
    public void Now_omits_zero_microseconds_only_under_auto()
    {
        var instant = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        using (PyRuntime.Override(clock: () => instant))
        {
            Assert.Equal("2026-11-07T12:00:00+00:00", PyDateTime.Now().Isoformat());
            Assert.Equal("2026-11-07T12:00:00.000000+00:00", StateStore.UtcNow());
        }

        using (PyRuntime.Override(clock: () => instant.AddTicks(1234567)))
        {
            Assert.Equal("2026-11-07T12:00:00.123456+00:00", PyDateTime.Now().Isoformat());
            Assert.Equal("2026-11-07 12:00:00.123456+00:00", PyDateTime.Now().ToString());
        }
    }

    [Fact]
    public void Timedelta_text_and_arithmetic()
    {
        Assert.Equal("1 day, 0:00:05", PyTimeDelta.Create(days: 1, seconds: 5).ToString());
        Assert.Equal("-1 day, 23:59:59", PyTimeDelta.FromSeconds(-1).ToString());
        Assert.Equal("0:00:00.000001", PyTimeDelta.FromMicroseconds(1).ToString());
        Assert.Equal("-2 days, 0:00:00", PyTimeDelta.FromDays(-2).ToString());
        Assert.Equal("datetime.timedelta(days=1, seconds=1800)", PyTimeDelta.Create(hours: 24, minutes: 30).Repr());
        Assert.Equal("datetime.timedelta(0)", PyTimeDelta.Zero.Repr());
        Assert.Equal(-1e-06, (PyDateTime.FromIsoformat("2026-01-01T00:00:00Z") - PyDateTime.FromIsoformat("2026-01-01T00:00:00.000001Z")).TotalSeconds());
        Assert.Throws<PyOverflowError>(() => PyTimeDelta.FromDays(1_000_000_000));
    }

    [Fact]
    public void Naive_and_aware_values_do_not_mix()
    {
        var naive = PyDateTime.FromIsoformat("2026-01-01T00:00:00");
        var aware = PyDateTime.FromIsoformat("2026-01-01T00:00:00Z");
        Assert.Equal("can't compare offset-naive and offset-aware datetimes",
                     Assert.Throws<PyTypeError>(() => naive < aware).Message);
        Assert.Equal("can't subtract offset-naive and offset-aware datetimes",
                     Assert.Throws<PyTypeError>(() => naive - aware).Message);
        Assert.False(naive == aware);
        Assert.True(PyDateTime.FromIsoformat("2026-01-01T09:00:00+09:00") == aware);
        Assert.True(aware + PyTimeDelta.FromMinutes(1) > PyDateTime.FromIsoformat("2026-01-01T09:00:59+09:00"));
        Assert.Throws<PyOverflowError>(() => PyDateTime.FromIsoformat("9999-12-31T23:59:59Z") + PyTimeDelta.FromSeconds(1));
    }

    [Fact]
    public void Dates_and_times_combine()
    {
        var date = PyDate.FromIsoformat("2026-02-28");
        Assert.Equal("2026-03-01", date.AddDays(PyTimeDelta.FromDays(1)).Isoformat());
        var time = PyTimeOfDay.FromIsoformat("09:30");
        Assert.Equal("2026-02-28T09:30:00+01:00", PyDateTime.Combine(date, time, PyTimeDelta.FromHours(1)).Isoformat());
        Assert.Equal("2026-02-28T08:30:00+00:00", PyDateTime.Combine(date, time, PyTimeDelta.FromHours(1)).ToUtc().Isoformat());
    }
}

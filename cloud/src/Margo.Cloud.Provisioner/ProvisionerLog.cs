using System.Globalization;

namespace Margo.Cloud.Provisioner;

/// <summary>
/// The job's log lines, as Python's <c>logging.basicConfig(level=logging.INFO, format="%(asctime)s
/// %(levelname)s %(name)s | %(message)s")</c> wrote them for the <c>margo.provision</c> logger:
/// <c>2026-11-07 09:30:00,123 INFO margo.provision | processed 0 message(s)</c>, local time, to
/// standard error.
/// </summary>
public sealed class ProvisionerLog
{
    /// <summary>The Python logger's name.</summary>
    public const string LoggerName = "margo.provision";

    private readonly TextWriter _writer;
    private readonly TimeProvider _time;

    public ProvisionerLog(TextWriter writer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>One INFO line. Python's default <c>asctime</c> is <c>%Y-%m-%d %H:%M:%S</c> in local
    /// time plus <c>,</c> and the truncated milliseconds.</summary>
    public void Info(string message)
    {
        var now = _time.GetLocalNow();
        _writer.Write(now.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture) + " INFO " + LoggerName + " | " + message + "\n");
        _writer.Flush();
    }
}

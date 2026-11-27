using Margo.Cloud.State.Core;
using Margo.Cloud.StateCli;

// margo-state SCRIPT.py ARGS...: stdin is decoded as strict UTF-8 (no BOM skipping) and stdout/stderr are
// written as UTF-8 with "\n" line ends, as python3 does under LC_ALL=C.UTF-8 and PYTHONIOENCODING=utf-8.
// Like CPython: arguments keep undecodable bytes as surrogateescape code points (not U+FFFD), and SIGXFSZ is
// ignored so a write past the file size limit fails with an OSError instead of killing the process.
PosixNative.IgnoreFileSizeLimitSignal();
args = PyRuntime.CommandLineArguments(args);
using var stdin = PyIO.StdinReader(Console.OpenStandardInput());
using var stdout = PyIO.OutputWriter(Console.OpenStandardOutput());
using var stderr = PyIO.ErrorWriter(Console.OpenStandardError());
int status;
try
{
    status = StateCliHost.Run(args, stdin, stdout, stderr);
}
finally
{
    stdout.Flush();
    stderr.Flush();
}

return status;

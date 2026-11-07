using Margo.Cloud.State.Core;
using Margo.Cloud.StateCli;

// margo-state SCRIPT.py ARGS...: stdin is decoded as strict UTF-8 (no BOM skipping) and stdout/stderr are
// written as UTF-8 with "\n" line ends, as python3 does under LC_ALL=C.UTF-8.
using var stdin = PyIO.StdinReader(Console.OpenStandardInput());
using var stdout = PyIO.OutputWriter(Console.OpenStandardOutput());
using var stderr = PyIO.OutputWriter(Console.OpenStandardError());
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

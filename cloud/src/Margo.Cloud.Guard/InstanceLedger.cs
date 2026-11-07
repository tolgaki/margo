using Margo.Cloud.Core;
using Margo.Cloud.State.Work;

namespace Margo.Cloud.Guard;

/// <summary>An instance's private work ledger (<c>instance.py</c> <c>InstanceBinding.open_ledger</c>).</summary>
public static class InstanceLedger
{
    /// <summary>
    /// Open this instance's work ledger through the ledger's public API: <c>Ledger(account=binding.account,
    /// state_root=state_root)</c>. The account key combines the agent identity and the principal, so two instances,
    /// or one instance re-bound to another person, never open each other's ledger.
    /// </summary>
    /// <param name="binding">The instance binding recorded at hire.</param>
    /// <param name="stateRoot">The private state root; <see langword="null"/> means the store's default
    /// (<c>MARGO_STATE_DIR</c>, then the private home directory), as Python's <c>None</c> does.</param>
    /// <returns>An open ledger the caller owns and must dispose (it creates or adopts the work schema, as in Python).</returns>
    /// <remarks>Synchronous SQLite work: callers on a request path should run it off the request thread, as Python's
    /// <c>asyncio.to_thread(lambda: binding.open_ledger(root).close())</c> does.</remarks>
    public static Ledger OpenLedger(this InstanceBinding binding, string? stateRoot)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new Ledger(binding.Account, stateRoot);
    }
}

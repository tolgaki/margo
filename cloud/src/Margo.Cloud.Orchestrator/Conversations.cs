using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Orchestrator;

/// <summary>
/// The manager conversations this process is carrying, in memory only: for each, the items the engine sends back to
/// the model on the next turn (<see cref="Margo.Cloud.Engine.EngineResult.History"/>). The model service stores
/// nothing, so this is the only copy; it is gone when the process stops, and the next message then starts a new
/// conversation (Margo's durable memory is her state, not this).
/// </summary>
/// <remarks>
/// Bounded three ways: the engine trims each conversation to its budget before it is kept here; a conversation not
/// used for <see cref="Idle"/> is dropped; and at most <see cref="Capacity"/> conversations are kept, the least
/// recently used going first. Thread-safe.
/// </remarks>
internal sealed class ConversationStore
{
    private readonly Dictionary<string, (JsonArray Items, DateTimeOffset Used)> _conversations = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public ConversationStore(TimeSpan idle, int capacity, TimeProvider clock)
    {
        Idle = idle;
        Capacity = Math.Max(capacity, 1);
        _clock = clock;
    }

    /// <summary>How long an unused conversation is kept.</summary>
    public TimeSpan Idle { get; }

    /// <summary>The most conversations kept.</summary>
    public int Capacity { get; }

    /// <summary>The conversations kept now (expired ones are not counted).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Sweep();
                return _conversations.Count;
            }
        }
    }

    /// <summary>A copy of the conversation's items, or null when there is none or it sat idle too long.</summary>
    public JsonArray? Get(string key)
    {
        lock (_gate)
        {
            Sweep();
            return _conversations.TryGetValue(key, out var found) ? (JsonArray)PyNode.Copy(found.Items)! : null;
        }
    }

    /// <summary>Keeps the conversation as it stands after a turn; an empty one is forgotten.</summary>
    public void Set(string key, JsonArray items)
    {
        lock (_gate)
        {
            Sweep();
            if (items.Count == 0)
            {
                _conversations.Remove(key);
                return;
            }

            _conversations[key] = ((JsonArray)PyNode.Copy(items)!, _clock.GetUtcNow());
            while (_conversations.Count > Capacity)
            {
                _conversations.Remove(_conversations.MinBy(pair => pair.Value.Used).Key);
            }
        }
    }

    /// <summary>Forgets the conversation.</summary>
    public void Remove(string key)
    {
        lock (_gate)
        {
            _conversations.Remove(key);
        }
    }

    private void Sweep()
    {
        var cutoff = _clock.GetUtcNow() - Idle;
        foreach (var key in _conversations.Where(pair => pair.Value.Used <= cutoff).Select(pair => pair.Key).ToList())
        {
            _conversations.Remove(key);
        }
    }
}

/// <summary>
/// The activities of each account that were recently taken up, in memory only, so a redelivered activity (a platform
/// retry, or the router's forward outliving its caller) does not run a second manager turn. Per account the newest
/// <see cref="Limit"/> IDs are remembered. Thread-safe.
/// </summary>
/// <remarks>In memory: a redelivery that reaches another container, or arrives after a restart, is not recognised.
/// A durable record would belong in the instance's state.</remarks>
internal sealed class RecentActivities(int limit)
{
    private readonly Dictionary<string, (HashSet<string> Seen, Queue<string> Order)> _accounts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The most activity IDs remembered per account.</summary>
    public int Limit { get; } = Math.Max(limit, 1);

    /// <summary>Takes the activity up: true the first time, false when it was taken up before (and not released).</summary>
    public bool TryClaim(string account, string activityId)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(account, out var recent))
            {
                recent = (new HashSet<string>(StringComparer.Ordinal), new Queue<string>());
                _accounts[account] = recent;
            }

            if (!recent.Seen.Add(activityId))
            {
                return false;
            }

            recent.Order.Enqueue(activityId);
            while (recent.Order.Count > Limit)
            {
                recent.Seen.Remove(recent.Order.Dequeue());
            }

            return true;
        }
    }

    /// <summary>Forgets an activity whose turn did not complete, so a redelivery may run it.</summary>
    public void Release(string account, string activityId)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(account, out var recent) && recent.Seen.Remove(activityId))
            {
                var kept = recent.Order.Where(id => id != activityId).ToList();
                recent.Order.Clear();
                kept.ForEach(recent.Order.Enqueue);
            }
        }
    }
}

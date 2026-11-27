using System.Text.Json.Nodes;
using Margo.Cloud.State.Core;

namespace Margo.Cloud.Engine;

/// <summary>
/// The conversation an <see cref="Engine"/> carries between turns, as Responses API input items: the manager's
/// messages, the model's output items (reasoning, function calls, messages) and the function results. The model
/// service keeps none of it (<c>store: false</c>); the caller holds it in memory.
/// </summary>
/// <remarks>A turn is a manager message and everything up to the next one. Turns are only ever dropped whole, oldest
/// first, so a function call is never separated from its result or from the reasoning before it.</remarks>
public static class ConversationHistory
{
    /// <summary>Whether an item is a manager message, which starts a turn.</summary>
    public static bool StartsTurn(JsonNode? item) =>
        item is JsonObject message && PyNode.Is(message["role"], "user")
        && (!message.ContainsKey("type") || PyNode.Is(message["type"], "message"));

    /// <summary>The size of the items as the JSON text sent (UTF-16 units): the budget's measure.</summary>
    public static long Chars(JsonArray items)
    {
        ArgumentNullException.ThrowIfNull(items);
        long total = 0;
        foreach (var item in items)
        {
            total += Size(item);
        }

        return total;
    }

    /// <summary>The number of turns (manager messages) in the items.</summary>
    public static int Turns(JsonArray items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count(StartsTurn);
    }

    /// <summary>
    /// A copy of <paramref name="items"/> within the budget: the oldest whole turns are dropped until at most
    /// <paramref name="maxChars"/> remain, and when <paramref name="overTokenBudget"/> (the last request used more input
    /// tokens than allowed) the older half of the turns that are left goes too. A single turn above the budget leaves
    /// nothing: the next message starts a new conversation. Items before the first manager message belong to no turn
    /// and are dropped.
    /// </summary>
    public static JsonArray Trim(JsonArray items, long maxChars, bool overTokenBudget = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        var turns = new List<(int Start, int End, long Chars)>();
        for (var index = 0; index < items.Count; index++)
        {
            if (StartsTurn(items[index]))
            {
                turns.Add((index, index + 1, Size(items[index])));
            }
            else if (turns.Count > 0)
            {
                var last = turns[^1];
                turns[^1] = (last.Start, index + 1, last.Chars + Size(items[index]));
            }
        }

        var first = 0;
        var total = turns.Sum(turn => turn.Chars);
        while (first < turns.Count && total > maxChars)
        {
            total -= turns[first].Chars;
            first++;
        }

        if (overTokenBudget)
        {
            first += (turns.Count - first + 1) / 2;
        }

        var kept = new JsonArray();
        if (first < turns.Count)
        {
            for (var index = turns[first].Start; index < items.Count; index++)
            {
                kept.Add(PyNode.Copy(items[index]));
            }
        }

        return kept;
    }

    private static long Size(JsonNode? item) => PyJson.Dumps(item, ensureAscii: false).Length;
}

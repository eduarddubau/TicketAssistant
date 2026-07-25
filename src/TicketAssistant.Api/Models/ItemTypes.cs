namespace TicketAssistant.Api.Models;

/// <summary>
/// Text helpers for an item's *type* — the backend's own word for what a thing is ("Task", "Bug",
/// "Story", "Ticket"). Types are never an enum here: Jira projects define their own issue types,
/// so the only honest model is to carry whatever the backend calls it and normalize at the edges.
///
/// The two edges are: turning a type into a heading ("Task" -> "Tasks"), and matching the loose
/// word a user or model sends against a stored type ("tasks", "TASK" -> "Task"), which is what
/// lets "list my tasks" filter reliably without a fixed vocabulary.
/// </summary>
public static class ItemTypes
{
    /// <summary>The type used when a backend has no concept of one (the mock board, the stub).</summary>
    public const string Ticket = "Ticket";

    /// <summary>Plural form for a group heading. Handles the -y/-s/-x/-ch/-sh cases English needs.</summary>
    public static string Plural(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Items";

        type = type.Trim();
        if (type.EndsWith('y') && type.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(type[^2])))
            return type[..^1] + "ies";
        if (type.EndsWith('s') || type.EndsWith('x') || type.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
            return type + "es";
        return type + "s";
    }

    /// <summary>
    /// Whether a ticket of type <paramref name="type"/> is what the caller asked for in
    /// <paramref name="filter"/>. Case-insensitive and plural-tolerant, because the filter comes
    /// from a model relaying the user's words ("tasks", "bugs", "story") rather than from a picker.
    /// A blank filter matches everything.
    /// </summary>
    public static bool Matches(string? type, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || string.Equals(Singular(type), Singular(filter), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Best-effort singular of a type word, for comparisons only — "Tasks" -> "Task",
    /// "Stories" -> "Story", "Bugs" -> "Bug". Never shown to anyone; the stored type is.
    /// </summary>
    private static string Singular(string? word)
    {
        var w = (word ?? "").Trim();
        if (w.Length < 3) return w;

        if (w.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) return w[..^3] + "y";
        if (w.EndsWith("ses", StringComparison.OrdinalIgnoreCase)
            || w.EndsWith("xes", StringComparison.OrdinalIgnoreCase)
            || w.EndsWith("ches", StringComparison.OrdinalIgnoreCase)
            || w.EndsWith("shes", StringComparison.OrdinalIgnoreCase))
            return w[..^2];
        if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) return w[..^1];
        return w;
    }
}

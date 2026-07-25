namespace TicketAssistant.Api.Models;

public sealed class CanonicalTicket
{
    public required string Id { get; init; }

    /// <summary>Which backend this ticket lives in ("jira", "mock-ticketing", "in-memory").</summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// <see cref="ProviderName"/> in the words the answer should use. Reads fan out across every
    /// connected backend, so a list can mix real tickets with demo data and the reader needs to be
    /// told which is which. Serialized into tool results deliberately: a small model repeats the
    /// phrasing a tool hands it far more reliably than it follows an instruction to translate
    /// "mock-ticketing" itself. Unknown backends fall back to their raw name rather than lying.
    /// </summary>
    public string Source => ProviderName switch
    {
        "jira" => "Jira",
        "mock-ticketing" => "the mock board (demo data, not a real ticket)",
        "in-memory" => "the in-memory stub (demo data, not a real ticket)",
        var other => other
    };

    /// <summary>
    /// The project this ticket belongs to (its key, e.g. "SUP"). With several backends and Jira
    /// sites in play, this plus <see cref="ProviderName"/> is what tells a reader — and the
    /// model — where a ticket actually lives.
    /// </summary>
    public string? Project { get; init; }

    public required string Title { get; init; }
    public string? Description { get; init; }
    public required TicketStatus Status { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public string? Assignee { get; init; }
    public string? Reporter { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Ids of tickets covering a similar issue (set when a near-duplicate was created anyway).</summary>
    public IReadOnlyList<string> RelatedTo { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Optional deadline; past this date and not resolved/closed means overdue.</summary>
    public DateTimeOffset? DueAt { get; init; }
    public required Uri Url { get; init; }
}

public enum TicketStatus
{
    Open,
    InProgress,
    Blocked,
    Resolved,
    Closed
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Urgent
}

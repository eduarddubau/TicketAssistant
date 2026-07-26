namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// What language the console is being read in, from <c>X-Language</c> — the same per-request-header
/// trick as the LLM switchers and the two read filters.
///
/// It exists because a translated console answering in English is only half a translation: the
/// assistant's replies are most of what the user reads. Unlike the filters, this one *is* asked of
/// the model rather than enforced — language is what the reply is written in, so there is nothing to
/// enforce after the fact and no honest way to fix a reply that came back in the wrong one.
///
/// Unknown or missing means English, which is also what the prompt says on its own; the extra
/// instruction is added only when it has something to add.
/// </summary>
public sealed class LanguageScope(IHttpContextAccessor accessor)
{
    public const string Header = "X-Language";

    /// <summary>The languages the console offers. Anything else is ignored rather than passed on.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["ro"] = "Romanian",
        ["de"] = "German",
    };

    /// <summary>The requested language's tag, or "en" when absent or unrecognised.</summary>
    public string Tag =>
        accessor.HttpContext?.Request.Headers[Header].ToString() is { Length: > 0 } raw
        && Names.ContainsKey(raw)
            ? raw.ToLowerInvariant()
            : "en";

    /// <summary>The language in English, for the instruction the model is given.</summary>
    public string Name => Names[Tag];

    /// <summary>
    /// The line appended to the system prompt, or empty for English. Spelled out rather than left
    /// implicit: a small model handed a Romanian question will often answer in Romanian anyway, but
    /// it just as often answers in English, and the console has a definite answer to which language
    /// the reader wants — so it says so instead of leaving it to chance.
    ///
    /// Ticket ids, statuses and priorities stay as the backend spells them: they are identifiers the
    /// user has to be able to match against their own board, not prose.
    /// </summary>
    public string PromptInstruction => Tag == "en"
        ? ""
        : $"""


           LANGUAGE

           Write every reply to the user in {Name}, whatever language they write to you in.
           Ticket ids, statuses, priorities and project keys keep the spelling the backend gave
           them — those are identifiers, and translating them would stop them matching the board.
           """;

    /// <summary>
    /// The words a listing is built from, in this language.
    ///
    /// A read hands the model finished lines and tells it to copy them exactly — which is what
    /// makes listings reliable, and also means the listing's own words are the last thing in the
    /// app still speaking English. An obedient model copies "Bugs in the mock board" into an
    /// otherwise Romanian answer; the listing guard, when it fires, prints those same words
    /// directly. So they are composed here rather than hardcoded at the point of use.
    ///
    /// What isn't translated: ids, project keys, statuses and priorities (identifiers — the user
    /// matches them against their own board), the backends' names for item types ("Bug", "Story" —
    /// a Jira project defines those, and this app has no business renaming them), and ticket
    /// titles, which are whatever the person who filed them wrote.
    /// </summary>
    public ListingWords Words => Tag switch
    {
        "ro" => new ListingWords(
            HeadingPattern: "{0} în {1}",
            MockBoard: "panoul demonstrativ (date demo, nu muncă reală)",
            InMemory: "sursa în memorie (date demo, nu muncă reală)",
            Project: "proiect",
            Due: "termen",
            Overdue: "RESTANT (avea termen {0})"),
        "de" => new ListingWords(
            HeadingPattern: "{0} in {1}",
            MockBoard: "dem Demo-Board (Demodaten, keine echte Arbeit)",
            InMemory: "dem In-Memory-Board (Demodaten, keine echte Arbeit)",
            Project: "Projekt",
            Due: "fällig",
            Overdue: "ÜBERFÄLLIG (war fällig am {0})"),
        _ => new ListingWords(
            HeadingPattern: "{0} in {1}",
            MockBoard: "the mock board (demo data, not real work)",
            InMemory: "the in-memory stub (demo data, not real work)",
            Project: "project",
            Due: "due",
            Overdue: "OVERDUE (was due {0})"),
    };
}

/// <summary>The handful of words a listing is made of, outside the data itself.</summary>
/// <param name="HeadingPattern">"{kinds} in {system}" — the connector is a word in some languages
/// and a case ending in others, so the whole pattern moves rather than just the preposition.</param>
public sealed record ListingWords(
    string HeadingPattern,
    string MockBoard,
    string InMemory,
    string Project,
    string Due,
    string Overdue);

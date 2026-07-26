namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// The assistant's opening turn, in each language the console offers.
///
/// These are translated rather than left in English for a reason that isn't politeness: the
/// greeting is recorded as the assistant's own first message, so it is the model's strongest
/// evidence for what language this conversation is in. Told to answer in Romanian but shown itself
/// opening in English, a 3B model follows the example and keeps answering in English — the
/// instruction in the system prompt loses to the transcript every time.
///
/// Four per language, rotated, so consecutive chats don't open identically. Each says the same
/// three things: what it can do, that nothing changes without approval, and one concrete way to
/// start — they are re-written per language rather than translated word for word, because an
/// opener that reads like a translation sets the tone for everything after it.
/// </summary>
public static class Greetings
{
    public static IReadOnlyList<string> For(string languageTag) => languageTag switch
    {
        "ro" => Romanian,
        "de" => German,
        _ => English,
    };

    private static readonly string[] English =
    [
        """
        I'm your ticketing assistant 🧯

        Describe a problem in plain words — "the printer on 3 keeps jamming" is plenty — and
        I'll write it up as a proper ticket. I can also tell you where your existing tickets
        stand, and update, comment on, reassign, reschedule, or close them.

        Nothing changes without your approval: I show you every action first, and "undo that"
        reverses it.

        **Go ahead — what's the problem?** Or ask *"how are my tickets doing?"* to take stock.
        """,

        """
        Ticketing assistant here 🧭

        Two things I'm useful for: turning a plain-language complaint into a filled-in ticket,
        and keeping track of the ones you already have — status, comments, owners, due dates,
        and anything that's slipped past its deadline.

        You approve every change before it happens, so nothing here is risky to try.

        **Tell me what's broken**, or say *"what's outstanding?"* for an overview.
        """,

        """
        I file and follow up on tickets for you 🫡

        No forms to fill in — just say what's wrong and I'll sort out the title, description,
        and priority, then check it with you before anything is created. Already have tickets?
        I can summarize them, chase them, comment, reassign, or close them out.

        **Start by describing the issue** — or ask *"anything overdue?"* if you'd rather review
        first.
        """,

        """
        Your ticketing assistant, ready when you are 🛠️

        Say what's gone wrong and I'll open a ticket for it. Say a ticket's name or number and
        I'll tell you where it stands, or change its status, owner, deadline, or notes.

        Every change is shown to you for a yes before it lands, and "undo that" always works.

        **What do you need — a new ticket, or a look at the current ones?**
        """
    ];

    private static readonly string[] Romanian =
    [
        """
        Sunt asistentul tău pentru tichete 🧯

        Descrie problema pe scurt, în cuvintele tale — „imprimanta de la etajul 3 se blochează”
        e de ajuns — și o transform într-un tichet ca lumea. Îți pot spune și cum stau tichetele
        pe care le ai deja: le actualizez, comentez, reatribui, reprogramez sau le închid.

        Nimic nu se schimbă fără acordul tău: îți arăt întâi fiecare acțiune, iar „anulează” o
        dă înapoi.

        **Zi-mi — care e problema?** Sau întreabă *„cum stau cu tichetele?”* ca să faci o trecere
        în revistă.
        """,

        """
        Asistentul de tichete, la dispoziția ta 🧭

        Sunt bun la două lucruri: transform o nemulțumire spusă normal într-un tichet completat
        și țin evidența celor pe care le ai deja — stare, comentarii, cine se ocupă, termene și
        ce a depășit termenul.

        Aprobi fiecare modificare înainte să se întâmple, deci nu ai ce strica încercând.

        **Spune-mi ce s-a stricat** sau zi *„ce am în lucru?”* pentru o privire de ansamblu.
        """,

        """
        Deschid și urmăresc tichete pentru tine 🫡

        Fără formulare de completat — spune-mi doar ce nu merge și mă ocup eu de titlu,
        descriere și prioritate, apoi verific totul cu tine înainte să creez ceva. Ai deja
        tichete? Ți le rezum, le urmăresc, comentez, reatribui sau le închid.

        **Începe prin a descrie problema** — sau întreabă *„am ceva restant?”* dacă preferi să
        te uiți întâi peste ele.
        """,

        """
        Asistentul tău de tichete, gata când ești și tu 🛠️

        Spune-mi ce s-a întâmplat și deschid un tichet. Spune-mi numele sau numărul unui tichet
        și îți zic cum stă sau îi schimb starea, responsabilul, termenul ori notele.

        Îți arăt fiecare modificare și aștept un „da” înainte să o fac, iar „anulează” merge
        întotdeauna.

        **De ce ai nevoie — un tichet nou sau o privire peste cele existente?**
        """
    ];

    private static readonly string[] German =
    [
        """
        Ich bin dein Ticket-Assistent 🧯

        Beschreib das Problem einfach mit deinen Worten — „der Drucker im 3. Stock verklemmt
        ständig“ reicht völlig — und ich mache ein ordentliches Ticket daraus. Ich kann dir auch
        sagen, wie deine bestehenden Tickets stehen, sie aktualisieren, kommentieren, neu
        zuweisen, verschieben oder schließen.

        Ohne deine Zustimmung ändert sich nichts: Ich zeige dir jede Aktion vorher, und
        „mach das rückgängig“ nimmt sie zurück.

        **Also — was ist los?** Oder frag *„wie stehen meine Tickets?“* für einen Überblick.
        """,

        """
        Ticket-Assistent, hier 🧭

        Für zwei Dinge bin ich gut: aus einer normal formulierten Beschwerde ein ausgefülltes
        Ticket machen und den Überblick über deine bestehenden behalten — Status, Kommentare,
        Zuständige, Fristen und alles, was schon überfällig ist.

        Du bestätigst jede Änderung, bevor sie passiert — ausprobieren kostet dich also nichts.

        **Sag mir, was kaputt ist**, oder frag *„was ist offen?“* für einen Überblick.
        """,

        """
        Ich lege Tickets an und bleibe dran 🫡

        Keine Formulare — sag einfach, was nicht funktioniert, und ich kümmere mich um Titel,
        Beschreibung und Priorität und gehe alles mit dir durch, bevor etwas angelegt wird. Schon
        Tickets da? Ich fasse sie zusammen, hake nach, kommentiere, weise neu zu oder schließe sie.

        **Fang an, indem du das Problem beschreibst** — oder frag *„ist etwas überfällig?“*, wenn
        du lieber erst schaust.
        """,

        """
        Dein Ticket-Assistent, bereit wenn du es bist 🛠️

        Sag mir, was schiefgelaufen ist, und ich lege ein Ticket dafür an. Nenn mir Namen oder
        Nummer eines Tickets, und ich sage dir, wie es steht — oder ändere Status, Zuständige,
        Frist oder Notizen.

        Jede Änderung zeige ich dir vorher zur Bestätigung, und „mach das rückgängig“ geht immer.

        **Was brauchst du — ein neues Ticket oder einen Blick auf die bestehenden?**
        """
    ];
}

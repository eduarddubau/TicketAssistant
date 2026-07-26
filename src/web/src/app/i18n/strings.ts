/**
 * Every string the console shows, in the three languages it speaks.
 *
 * English is the source of truth: `Strings` is derived from it, so `ro` and `de` are checked
 * against its keys at compile time and a translation that falls behind is a build error rather
 * than a word of English appearing in a Romanian sentence. Placeholders are `{name}` and are
 * filled by I18nService.t().
 *
 * What is deliberately *not* here: the debug console. Its content is a server-side trace — stage
 * names, tool names, JSON payloads — that arrives in English whatever the console is set to, and a
 * panel with translated buttons around untranslated evidence reads worse than one that is honestly
 * a developer's tool. Backend names (Jira, the mock board) and the colour schemes' own names stay
 * put for the same reason: they are what those things are called.
 */
export const en = {
  'brand.sub': 'one chat · every backend',
  'header.newChat': 'New chat',
  'header.newChatTitle': 'Start a new conversation',
  'header.connectBanner':
    'Connect Jira (top right) to fold in your Jira tickets and projects — chatting works without it.',

  'toolbar.sources.all': 'All sources',
  'toolbar.sources.empty': 'No backends have answered yet.',
  'toolbar.sources.hint': 'Which systems the assistant reads from',
  'toolbar.kinds.all': 'All kinds',
  'toolbar.kinds.empty': 'No kinds yet — connect a backend first.',
  'toolbar.kinds.hint': 'Which kinds of item the assistant reads',
  'toolbar.kinds.count': '{count} kinds',
  'toolbar.sources.count': '{count} sources',

  'toolbar.user': 'User',
  'toolbar.userTitle':
    "Who you are on the mock board — it scopes tickets to what you raised or were assigned. " +
    "'charlie' owns nothing there, so reads come only from connected accounts; 'admin' sees the whole board.",
  'toolbar.provider': 'Provider',
  'toolbar.model': 'Model',
  'toolbar.modelTitle': 'Which model answers',
  'toolbar.compute': 'Compute',
  'toolbar.noKey': 'no key',
  'toolbar.gpuIdle': 'GPU idle',
  'toolbar.gpuOff': 'GPU off',
  'toolbar.debug': 'Debug',
  'toolbar.debugShow': 'Show what the assistant is doing under the hood (Ctrl+`)',
  'toolbar.debugHide': 'Hide the debug console (Ctrl+`)',
  'toolbar.connect': 'Connect Jira',
  'toolbar.connecting': 'Connecting…',
  'toolbar.disconnect': 'Disconnect',
  'toolbar.connected': 'Connected',
  'toolbar.sites': '{count} site(s)',

  'filter.allMeans': 'Nothing ticked means all of them.',
  'filter.none': 'Nothing to choose from yet.',

  'chat.placeholder': 'Describe an issue, or ask about your tickets…',
  'chat.send': 'Send',
  'chat.suggestion.open': 'Show me my open tickets',
  'chat.suggestion.tasks': 'What tasks do I have?',
  'chat.suggestion.create': 'Create a ticket',
  'chat.suggestion.task': 'Open a task for me',
  'chat.restarted': 'The assistant restarted, so this chat begins again — resending your message.',

  'card.provider': 'Provider',
  'card.site': 'Site',
  'card.project': 'Project',
  'card.kind': 'Kind',
  'card.field.ticket': 'Ticket',
  'card.field.title': 'Title',
  'card.field.description': 'Description',
  'card.field.severity': 'Severity',
  'card.field.status': 'New status',
  'card.field.due': 'Due date (blank = none)',
  'card.field.assignee': 'Assign to (blank = unassigned)',
  'card.field.note': 'Resolution note',
  'card.field.comment': 'Comment',
  'card.field.arguments': 'Arguments',
  'card.heading.create': '⚠️ Review & confirm new item',
  'card.heading.status': '⚠️ Confirm status change',
  'card.heading.due': '⚠️ Confirm due date',
  'card.heading.assign': '⚠️ Confirm assignment',
  'card.heading.resolve': '⚠️ Confirm resolve',
  'card.heading.comment': '⚠️ Confirm comment',
  'card.heading.other': '⚠️ Confirm: {tool}',
  'card.verb.create': 'Create {kind}',
  'card.verb.status': 'Update status',
  'card.verb.due': 'Set due date',
  'card.verb.assign': 'Assign ticket',
  'card.verb.resolve': 'Resolve ticket',
  'card.verb.comment': 'Add comment',
  'card.verb.other': 'Confirm',
  'card.cancel': 'Cancel',
  'card.cancelled': 'Cancelled',
  'card.unknownTicket': 'Unrecognised ticket — check the id',

  'appearance.toLight': 'Switch to the light theme',
  'appearance.toDark': 'Switch to the dark theme',
  'appearance.scheme': 'Colour scheme',
  'appearance.schemeNote': 'Every scheme works in both themes.',

  'language.label': 'Language',
  'language.note': 'The assistant answers in this language too.',
};

/** The shape every language must fill — derived from English, so nothing can be left out. */
export type StringKey = keyof typeof en;
export type Strings = Record<StringKey, string>;

export const ro: Strings = {
  'brand.sub': 'o singură conversație · toate sistemele',
  'header.newChat': 'Conversație nouă',
  'header.newChatTitle': 'Începe o conversație nouă',
  'header.connectBanner':
    'Conectează Jira (dreapta sus) ca să incluzi tichetele și proiectele tale din Jira — conversația merge și fără.',

  'toolbar.sources.all': 'Toate sursele',
  'toolbar.sources.empty': 'Niciun sistem nu a răspuns încă.',
  'toolbar.sources.hint': 'Din ce sisteme citește asistentul',
  'toolbar.kinds.all': 'Toate tipurile',
  'toolbar.kinds.empty': 'Niciun tip încă — conectează întâi un sistem.',
  'toolbar.kinds.hint': 'Ce tipuri de element citește asistentul',
  'toolbar.kinds.count': '{count} tipuri',
  'toolbar.sources.count': '{count} surse',

  'toolbar.user': 'Utilizator',
  'toolbar.userTitle':
    'Cine ești pe panoul demonstrativ — limitează tichetele la cele deschise de tine sau atribuite ție. ' +
    '„charlie” nu deține nimic acolo, deci se citește doar din conturile conectate; „admin” vede tot panoul.',
  'toolbar.provider': 'Furnizor',
  'toolbar.model': 'Model',
  'toolbar.modelTitle': 'Ce model răspunde',
  'toolbar.compute': 'Calcul',
  'toolbar.noKey': 'fără cheie',
  'toolbar.gpuIdle': 'GPU inactiv',
  'toolbar.gpuOff': 'GPU oprit',
  'toolbar.debug': 'Depanare',
  'toolbar.debugShow': 'Arată ce face asistentul în spate (Ctrl+`)',
  'toolbar.debugHide': 'Ascunde consola de depanare (Ctrl+`)',
  'toolbar.connect': 'Conectează Jira',
  'toolbar.connecting': 'Se conectează…',
  'toolbar.disconnect': 'Deconectează',
  'toolbar.connected': 'Conectat',
  'toolbar.sites': '{count} site-uri',

  'filter.allMeans': 'Dacă nu bifezi nimic, se afișează toate.',
  'filter.none': 'Nimic de ales deocamdată.',

  'chat.placeholder': 'Descrie o problemă sau întreabă despre tichetele tale…',
  'chat.send': 'Trimite',
  'chat.suggestion.open': 'Arată-mi tichetele mele deschise',
  'chat.suggestion.tasks': 'Ce sarcini am?',
  'chat.suggestion.create': 'Creează un tichet',
  'chat.suggestion.task': 'Deschide-mi o sarcină',
  'chat.restarted': 'Asistentul a repornit, așa că discuția începe din nou — retrimit mesajul tău.',

  'card.provider': 'Furnizor',
  'card.site': 'Site',
  'card.project': 'Proiect',
  'card.kind': 'Tip',
  'card.field.ticket': 'Tichet',
  'card.field.title': 'Titlu',
  'card.field.description': 'Descriere',
  'card.field.severity': 'Severitate',
  'card.field.status': 'Stare nouă',
  'card.field.due': 'Termen (gol = fără)',
  'card.field.assignee': 'Atribuie lui (gol = neatribuit)',
  'card.field.note': 'Notă de rezolvare',
  'card.field.comment': 'Comentariu',
  'card.field.arguments': 'Argumente',
  'card.heading.create': '⚠️ Verifică și confirmă elementul nou',
  'card.heading.status': '⚠️ Confirmă schimbarea stării',
  'card.heading.due': '⚠️ Confirmă termenul',
  'card.heading.assign': '⚠️ Confirmă atribuirea',
  'card.heading.resolve': '⚠️ Confirmă rezolvarea',
  'card.heading.comment': '⚠️ Confirmă comentariul',
  'card.heading.other': '⚠️ Confirmă: {tool}',
  'card.verb.create': 'Creează {kind}',
  'card.verb.status': 'Schimbă starea',
  'card.verb.due': 'Setează termenul',
  'card.verb.assign': 'Atribuie tichetul',
  'card.verb.resolve': 'Rezolvă tichetul',
  'card.verb.comment': 'Adaugă comentariu',
  'card.verb.other': 'Confirmă',
  'card.cancel': 'Anulează',
  'card.cancelled': 'Anulat',
  'card.unknownTicket': 'Tichet necunoscut — verifică identificatorul',

  'appearance.toLight': 'Comută pe tema deschisă',
  'appearance.toDark': 'Comută pe tema închisă',
  'appearance.scheme': 'Schemă de culori',
  'appearance.schemeNote': 'Fiecare schemă merge pe ambele teme.',

  'language.label': 'Limbă',
  'language.note': 'Asistentul răspunde tot în această limbă.',
};

export const de: Strings = {
  'brand.sub': 'ein Chat · alle Systeme',
  'header.newChat': 'Neuer Chat',
  'header.newChatTitle': 'Neue Unterhaltung beginnen',
  'header.connectBanner':
    'Verbinde Jira (oben rechts), um deine Jira-Tickets und -Projekte einzubeziehen — der Chat funktioniert auch ohne.',

  'toolbar.sources.all': 'Alle Quellen',
  'toolbar.sources.empty': 'Noch hat kein System geantwortet.',
  'toolbar.sources.hint': 'Aus welchen Systemen der Assistent liest',
  'toolbar.kinds.all': 'Alle Arten',
  'toolbar.kinds.empty': 'Noch keine Arten — verbinde zuerst ein System.',
  'toolbar.kinds.hint': 'Welche Arten von Vorgängen der Assistent liest',
  'toolbar.kinds.count': '{count} Arten',
  'toolbar.sources.count': '{count} Quellen',

  'toolbar.user': 'Benutzer',
  'toolbar.userTitle':
    'Wer du auf dem Demo-Board bist — das beschränkt Tickets auf die, die du gemeldet hast oder die dir ' +
    'zugewiesen sind. „charlie“ besitzt dort nichts, es wird also nur aus verbundenen Konten gelesen; ' +
    '„admin“ sieht das ganze Board.',
  'toolbar.provider': 'Anbieter',
  'toolbar.model': 'Modell',
  'toolbar.modelTitle': 'Welches Modell antwortet',
  'toolbar.compute': 'Rechenwerk',
  'toolbar.noKey': 'kein Schlüssel',
  'toolbar.gpuIdle': 'GPU im Leerlauf',
  'toolbar.gpuOff': 'GPU aus',
  'toolbar.debug': 'Debug',
  'toolbar.debugShow': 'Zeigen, was der Assistent im Hintergrund tut (Strg+`)',
  'toolbar.debugHide': 'Debug-Konsole ausblenden (Strg+`)',
  'toolbar.connect': 'Jira verbinden',
  'toolbar.connecting': 'Verbinde…',
  'toolbar.disconnect': 'Trennen',
  'toolbar.connected': 'Verbunden',
  'toolbar.sites': '{count} Site(s)',

  'filter.allMeans': 'Nichts angehakt bedeutet: alle.',
  'filter.none': 'Noch nichts zur Auswahl.',

  'chat.placeholder': 'Beschreibe ein Problem oder frag nach deinen Tickets…',
  'chat.send': 'Senden',
  'chat.suggestion.open': 'Zeig mir meine offenen Tickets',
  'chat.suggestion.tasks': 'Welche Aufgaben habe ich?',
  'chat.suggestion.create': 'Ticket anlegen',
  'chat.suggestion.task': 'Leg mir eine Aufgabe an',
  'chat.restarted': 'Der Assistent wurde neu gestartet, der Chat beginnt neu — deine Nachricht wird erneut gesendet.',

  'card.provider': 'Anbieter',
  'card.site': 'Site',
  'card.project': 'Projekt',
  'card.kind': 'Art',
  'card.field.ticket': 'Ticket',
  'card.field.title': 'Titel',
  'card.field.description': 'Beschreibung',
  'card.field.severity': 'Schweregrad',
  'card.field.status': 'Neuer Status',
  'card.field.due': 'Fällig am (leer = keins)',
  'card.field.assignee': 'Zuweisen an (leer = nicht zugewiesen)',
  'card.field.note': 'Lösungsnotiz',
  'card.field.comment': 'Kommentar',
  'card.field.arguments': 'Argumente',
  'card.heading.create': '⚠️ Neuen Vorgang prüfen & bestätigen',
  'card.heading.status': '⚠️ Statusänderung bestätigen',
  'card.heading.due': '⚠️ Fälligkeitsdatum bestätigen',
  'card.heading.assign': '⚠️ Zuweisung bestätigen',
  'card.heading.resolve': '⚠️ Lösung bestätigen',
  'card.heading.comment': '⚠️ Kommentar bestätigen',
  'card.heading.other': '⚠️ Bestätigen: {tool}',
  'card.verb.create': '{kind} anlegen',
  'card.verb.status': 'Status ändern',
  'card.verb.due': 'Fälligkeit setzen',
  'card.verb.assign': 'Ticket zuweisen',
  'card.verb.resolve': 'Ticket lösen',
  'card.verb.comment': 'Kommentar hinzufügen',
  'card.verb.other': 'Bestätigen',
  'card.cancel': 'Abbrechen',
  'card.cancelled': 'Abgebrochen',
  'card.unknownTicket': 'Unbekanntes Ticket — prüfe die ID',

  'appearance.toLight': 'Zum hellen Design wechseln',
  'appearance.toDark': 'Zum dunklen Design wechseln',
  'appearance.scheme': 'Farbschema',
  'appearance.schemeNote': 'Jedes Schema funktioniert in beiden Designs.',

  'language.label': 'Sprache',
  'language.note': 'Der Assistent antwortet ebenfalls in dieser Sprache.',
};

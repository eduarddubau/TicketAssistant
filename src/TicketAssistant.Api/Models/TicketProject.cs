namespace TicketAssistant.Api.Models;

/// <summary>A project a ticket can belong to — its key (e.g. "SUP") and display name, plus which
/// site (workspace) it lives in when the backend spans several. Backends without a project
/// concept (the mock) expose none; single-site backends leave the site fields null.</summary>
public sealed record TicketProject(string Key, string Name, string? SiteName = null, string? SiteUrl = null);

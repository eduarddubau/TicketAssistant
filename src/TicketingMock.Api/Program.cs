using TicketingMock.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TicketStore>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// The caller's user id scopes reads/writes. Absent (e.g. the admin board) => see all.
static string? Owner(HttpRequest request) => request.Headers["X-User-Id"].ToString() is { Length: > 0 } id ? id : null;

// List / search must be declared so the literal "search" segment is matched before "{id}".
app.MapGet("/api/tickets", (HttpRequest req, TicketStore store) => store.All(Owner(req)));

app.MapGet("/api/tickets/search", (string? q, HttpRequest req, TicketStore store) => store.Search(q ?? "", Owner(req)));

app.MapGet("/api/tickets/{id}", (string id, HttpRequest req, TicketStore store) =>
    store.Get(id, Owner(req)) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

app.MapPost("/api/tickets", (CreateTicketBody body, HttpRequest req, TicketStore store) =>
{
    var ticket = store.Create(body, Owner(req));
    return Results.Created(ticket.Url, ticket);
});

app.MapPatch("/api/tickets/{id}/status", (string id, UpdateStatusBody body, HttpRequest req, TicketStore store) =>
    store.UpdateStatus(id, body.Status, Owner(req)) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

app.MapPost("/api/tickets/{id}/comments", (string id, AddCommentBody body, HttpRequest req, TicketStore store) =>
    store.AddComment(id, string.IsNullOrWhiteSpace(body.Author) ? "anonymous" : body.Author, body.Body, Owner(req)) is { } comment
        ? Results.Ok(comment)
        : Results.NotFound());

app.Run();

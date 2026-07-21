using TicketingMock.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TicketStore>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// List / search must be declared so the literal "search" segment is matched before "{id}".
app.MapGet("/api/tickets", (TicketStore store) => store.All());

app.MapGet("/api/tickets/search", (string? q, TicketStore store) => store.Search(q ?? ""));

app.MapGet("/api/tickets/{id}", (string id, TicketStore store) =>
    store.Get(id) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

app.MapPost("/api/tickets", (CreateTicketBody body, TicketStore store) =>
{
    var ticket = store.Create(body);
    return Results.Created(ticket.Url, ticket);
});

app.MapPatch("/api/tickets/{id}/status", (string id, UpdateStatusBody body, TicketStore store) =>
    store.UpdateStatus(id, body.Status) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

app.MapPost("/api/tickets/{id}/comments", (string id, AddCommentBody body, TicketStore store) =>
    store.AddComment(id, string.IsNullOrWhiteSpace(body.Author) ? "anonymous" : body.Author, body.Body) is { } comment
        ? Results.Ok(comment)
        : Results.NotFound());

app.Run();

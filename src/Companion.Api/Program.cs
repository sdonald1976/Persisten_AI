using System.Net.WebSockets;
using System.Text;
using Companion.Api;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["Database:Path"] ?? "companion.db";
builder.Services.AddCompanion(builder.Configuration, $"Data Source={dbPath}");

// camelCase + string enums for the model-bound endpoints; the hand-written SSE/WS frames use ApiJson.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Local-first: allow a browser front-end served from anywhere on the machine to call the API.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Create/upgrade the schema before serving, same as the CLI.
try
{
    await app.Services.MigrateDatabaseAsync();
}
catch (InvalidOperationException ex)
{
    app.Logger.LogError("{Message}", ex.Message);
    return;
}

app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();   // serve wwwroot/index.html at "/"
app.UseStaticFiles();

// The active user always comes from the trusted IUserContext (a scoped/DI singleton), never
// from the request. This keeps the API ownership-safe ahead of a real auth boundary.

// ---- health / status ----

app.MapGet("/health", (ModelOptions models) =>
{
    var provider = models.UsesRealModel ? models.Provider : "Mock (offline)";
    return Results.Ok(new
    {
        status = "ok",
        provider,
        models = models.UsesRealModel
            ? new
            {
                chat = models.Chat.Model,
                extraction = models.ExtractionOrChat.Model,
                summarizer = models.SummarizerOrChat.Model,
                embeddings = models.Embeddings.Model,
                vision = models.Vision?.Model,
                transcription = models.Transcription?.Model,
            }
            : null,
    });
});

// ---- conversations ----

app.MapPost("/conversations", async (StartConversationRequest? req, IUserContext user, IConversationStore store, CancellationToken ct) =>
{
    var conv = await store.StartConversationAsync(
        user.UserId, req?.Title ?? "API session", modelUsed: null, source: req?.Source ?? "api", ct);
    return Results.Ok(new { conversationId = conv.Id.ToString() });
});

// ---- chat (non-streaming) ----

app.MapPost("/chat", async (ChatRequest req, IUserContext user, IAgent agent, CancellationToken ct) =>
{
    if (!Guid.TryParse(req.ConversationId, out var convId))
        return Results.BadRequest(new { error = "conversationId must be a GUID (start one via POST /conversations)." });

    var reply = await agent.HandleAsync(user.UserId, convId, req.Message, tokenSink: null, ct);
    return Results.Ok(ReplyDto.From(reply));
});

app.MapPost("/chat/confirm", async (ConfirmRequest req, IUserContext user, IAgent agent, CancellationToken ct) =>
{
    if (!Guid.TryParse(req.ConversationId, out var convId))
        return Results.BadRequest(new { error = "conversationId must be a GUID." });

    var reply = await agent.ConfirmAsync(user.UserId, convId, req.ConfirmationToken, req.Confirmed, ct);
    return Results.Ok(ReplyDto.From(reply));
});

// ---- chat (Server-Sent Events streaming) ----
// EventSource is GET-only, so parameters come in the query string. The user is NOT one of them.
app.MapGet("/chat/stream", async (HttpContext ctx, IUserContext user, IServiceScopeFactory scopes,
    string conversationId, string message, CancellationToken ct) =>
{
    if (!Guid.TryParse(conversationId, out var convId))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // don't let a proxy buffer the stream

    async Task Send(string ev, object data)
    {
        await ctx.Response.WriteAsync($"event: {ev}\ndata: {ApiJson.Serialize(data)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    using var scope = scopes.CreateScope();
    var agent = scope.ServiceProvider.GetRequiredService<IAgent>();
    var sink = new TokenChannelSink();

    async Task<AgentReply> Run()
    {
        try { return await agent.HandleAsync(user.UserId, convId, message, sink, ct); }
        finally { sink.Complete(); }
    }

    var work = Run();
    try
    {
        await foreach (var chunk in sink.Reader.ReadAllAsync(ct))
            await Send("token", new { text = chunk });
        var reply = await work;
        await Send("done", ReplyDto.From(reply));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        await Send("error", new { message = ex.Message });
    }
});

// ---- WebSocket (bidirectional; the avatar/voice-facing channel) ----
app.Map("/ws", async (HttpContext ctx, IUserContext user, IServiceScopeFactory scopes, IConversationStore conversations, CancellationToken ct) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

    // Each connection gets its own conversation. The user comes from trusted context, not the query.
    var conv = await conversations.StartConversationAsync(user.UserId, "WebSocket session", null, "ws", ct);
    await WebSocketConversation.RunAsync(socket, scopes, user.UserId, conv.Id, ct);
});

// ---- structured read endpoints (for rich UIs; the conversational path covers the same ground) ----

app.MapGet("/memories", async (IUserContext user, IMemoryStore store, CancellationToken ct) =>
{
    var memories = await store.GetRetrievableMemoriesAsync(user.UserId, ct);
    return Results.Ok(memories.OrderByDescending(m => m.EffectiveAt).Select(MemoryDto.From));
});

app.MapGet("/projects", async (IUserContext user, IProjectStore store, CancellationToken ct) =>
{
    var projects = await store.GetProjectsAsync(user.UserId, ct);
    return Results.Ok(projects.OrderByDescending(p => p.LastActivityAt).Select(ProjectDto.From));
});

app.MapGet("/projects/{name}", async (string name, IUserContext user,
    IEntityResolver resolver, IProjectContextService context, CancellationToken ct) =>
{
    var uid = user.UserId;
    var resolution = await resolver.ResolveProjectAsync(uid, name, ct);
    if (resolution.RequiresClarification)
        return Results.Ok(new { requiresClarification = true, question = resolution.ClarificationQuestion });
    if (resolution.Best is null)
        return Results.NotFound(new { error = $"No project matching \"{name}\"." });

    var summary = await context.GetSummaryAsync(uid, resolution.Best.Project.Id, ct);
    if (summary is null)
        return Results.NotFound(new { error = "Project has no summary." });

    return Results.Ok(new
    {
        name = summary.Project.Name,
        status = summary.Project.Status.ToString(),
        purpose = summary.Project.Purpose,
        decisions = summary.Decisions.Select(d => d.Statement),
        openLoops = summary.OpenLoops.Select(l => l.Description),
        recentActivity = summary.RecentEvents.Select(e => e.Description),
    });
});

app.MapGet("/loops", async (IUserContext user, IProjectStore store, CancellationToken ct) =>
{
    var loops = await store.GetOpenLoopsAsync(user.UserId, onlyOpen: true, ct);
    return Results.Ok(loops.Select(OpenLoopDto.From));
});

app.MapGet("/persona", async (IUserContext user, IProfileStore store, CancellationToken ct) =>
{
    var profile = await store.GetOrCreateAsync(user.UserId, ct);
    return Results.Ok(new { persona = profile.Persona });
});

app.MapPut("/persona", async (PersonaRequest req, IUserContext user, IProfileStore store, CancellationToken ct) =>
{
    await store.SetPersonaAsync(user.UserId, req.Persona, ct);
    return Results.Ok(new { persona = req.Persona });
});

// Thumbs up/down for a rich UI. Routed through the brain so it uses the exact same last-exchange
// reconstruction as saying "that was great" out loud.
app.MapPost("/feedback", async (FeedbackRequest req, IUserContext user, IAgent agent, CancellationToken ct) =>
{
    if (!Guid.TryParse(req.ConversationId, out var convId))
        return Results.BadRequest(new { error = "conversationId must be a GUID." });

    var positive = req.Rating.Equals("positive", StringComparison.OrdinalIgnoreCase);
    var phrase = positive ? "that was great" : "that was unhelpful";
    if (!string.IsNullOrWhiteSpace(req.Note))
        phrase += " — " + req.Note;

    var reply = await agent.HandleAsync(user.UserId, convId, phrase, tokenSink: null, ct);
    return Results.Ok(ReplyDto.From(reply));
});

app.Run();

/// <summary>Handles the receive/serve loop for one WebSocket connection.</summary>
internal static class WebSocketConversation
{
    public static async Task RunAsync(
        WebSocket socket, IServiceScopeFactory scopes, string userId, Guid defaultConversationId, CancellationToken ct)
    {
        await SendAsync(socket, new { type = "ready", conversationId = defaultConversationId.ToString() }, ct);

        var buffer = new byte[8 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var (text, closed) = await ReceiveTextAsync(socket, buffer, ct);
            if (closed)
                break;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            ClientFrame? frame;
            try { frame = System.Text.Json.JsonSerializer.Deserialize<ClientFrame>(text, ApiJson.Options); }
            catch { await SendAsync(socket, new { type = "error", message = "invalid JSON" }, ct); continue; }
            if (frame is null)
                continue;

            var convId = Guid.TryParse(frame.ConversationId, out var c) ? c : defaultConversationId;

            using var scope = scopes.CreateScope();
            var agent = scope.ServiceProvider.GetRequiredService<IAgent>();

            try
            {
                switch ((frame.Type ?? "").ToLowerInvariant())
                {
                    case "chat":
                        await HandleChatAsync(socket, agent, userId, convId, frame.Text ?? "", ct);
                        break;
                    case "confirm":
                        var outcome = await agent.ConfirmAsync(userId, convId, frame.Token ?? "", frame.Confirmed ?? false, ct);
                        await SendAsync(socket, ReplyFrame(outcome), ct);
                        break;
                    default:
                        await SendAsync(socket, new { type = "error", message = $"unknown frame type '{frame.Type}'" }, ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SendAsync(socket, new { type = "error", message = ex.Message }, ct);
            }
        }

        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    private static async Task HandleChatAsync(
        WebSocket socket, IAgent agent, string userId, Guid convId, string message, CancellationToken ct)
    {
        var sink = new TokenChannelSink();
        async Task<AgentReply> Run()
        {
            try { return await agent.HandleAsync(userId, convId, message, sink, ct); }
            finally { sink.Complete(); }
        }

        var work = Run();
        await foreach (var chunk in sink.Reader.ReadAllAsync(ct))
            await SendAsync(socket, new { type = "token", text = chunk }, ct);
        var reply = await work;
        await SendAsync(socket, ReplyFrame(reply), ct);
    }

    private static object ReplyFrame(AgentReply reply) => new
    {
        type = "reply",
        kind = reply.Kind.ToString(),
        intent = reply.Intent.ToString(),
        text = reply.Text,
        confirmationToken = reply.ConfirmationToken,
    };

    private static async Task SendAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(ApiJson.Serialize(payload));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<(string? Text, bool Closed)> ReceiveTextAsync(
        WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (null, true);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    private sealed record ClientFrame(
        string? Type, string? Text, string? ConversationId, string? Token, bool? Confirmed);
}

// Exposed so an integration-test host (WebApplicationFactory) can boot this app.
public partial class Program { }

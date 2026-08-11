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

// The companion's own clock: reflect (inner monologue) while the user is away, and — when a
// push channel is configured — reach out on her own when she has something real to say.
builder.Services.AddHostedService<ReflectionWorker>();
builder.Services.AddHostedService<OutreachWorker>();

var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();

// Bind to loopback by default. The API is never exposed on LAN interfaces unless the host
// explicitly configures Urls / ASPNETCORE_URLS.
if (string.IsNullOrWhiteSpace(builder.Configuration["Urls"]) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls(apiOptions.DefaultUrl);
}

// camelCase + string enums for the model-bound endpoints; the hand-written SSE/WS frames use ApiJson.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// CORS is an explicit allow-list — never a wildcard. Only the configured local dev origins may
// call the API from a browser page. No credentials/cookies (auth uses an explicit header/token).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(apiOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));

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

var logger = app.Logger;
var expectedToken = ApiToken.Resolve(apiOptions, dbPath, logger);

// Prompt overrides: defaults live in code (Prompts catalog); a prompts/ directory of text files
// overrides wording at runtime — hand-editable, and written through by PUT /prompts below.
var promptsDir = builder.Configuration["Prompts:Path"] ?? "prompts";
PromptOverrideFiles.LoadAll(promptsDir, logger);

// Outermost: turn any unhandled exception into a sanitized {error,message,correlationId} response.
// The detailed exception is logged server-side only, keyed by the same correlation id.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        // The client disconnected (tab closed, refresh, navigation) while work was in flight.
        // Normal in a browser-facing API — never an InternalError, never a stack trace.
        logger.LogDebug("Request {Path} canceled: client disconnected.", ctx.Request.Path);
    }
    catch (Exception ex)
    {
        var (status, code, message) = ApiError.Classify(ex);
        var correlationId = ctx.TraceIdentifier;
        logger.LogError(ex, "API error {Code} correlationId={CorrelationId}", code, correlationId);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ApiJson.Serialize(new ApiError(code, message, correlationId)));
        }
    }
});

app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();   // serve wwwroot/index.html at "/" (static files need no token)
// Teach the static-file middleware the 3D model MIME types so a dropped-in wwwroot/avatar.glb (the
// optional default avatar) serves correctly; the JS avatar viewer can also load a model via file-picker.
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".glb"] = "model/gltf-binary";
contentTypes.Mappings[".gltf"] = "model/gltf+json";
contentTypes.Mappings[".vrm"] = "model/gltf-binary"; // VRoid/VRM is glTF-binary under the hood
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

// Local API authentication: every REST/SSE/WebSocket call must present the local token (header
// Authorization: Bearer / X-Companion-Key, or ?access_token= for EventSource/WebSocket which can't
// set headers). Static files above are already served; preflight is handled by CORS.
app.Use(async (ctx, next) =>
{
    if (expectedToken is null || HttpMethods.IsOptions(ctx.Request.Method))
    {
        await next();
        return;
    }

    if (!ApiToken.Verify(ApiToken.FromRequest(ctx.Request), expectedToken))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(ApiJson.Serialize(
            new ApiError("Unauthorized", "A valid local API token is required.", ctx.TraceIdentifier)));
        return;
    }

    await next();
});

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
                speech = models.Speech?.Model,
            }
            : null,
    });
});

// ---- greeting (so the client never shows a blank prompt) ----

app.MapGet("/greeting", async (IUserContext user, IGreeter greeter, CancellationToken ct) =>
{
    var greeting = await greeter.GreetAsync(user.UserId, ct);
    return Results.Ok(new { message = greeting.Message, openers = greeting.Openers });
});

// ---- conversations ----

app.MapPost("/conversations", async (StartConversationRequest? req, IUserContext user, IConversationStore store, CancellationToken ct) =>
{
    var conv = await store.StartConversationAsync(
        user.UserId, req?.Title ?? "API session", modelUsed: null, source: req?.Source ?? "api", ct);
    return Results.Ok(new { conversationId = conv.Id.ToString() });
});

// ---- chat (non-streaming) ----

app.MapPost("/chat", async (ChatRequest req, IUserContext user, IConversationStore conversations, IAgent agent, CancellationToken ct) =>
{
    if (!Guid.TryParse(req.ConversationId, out var convId))
        return ApiError.BadRequest("conversationId must be a GUID (start one via POST /conversations).");
    // Validate existence + ownership before ANY work. A missing/foreign conversation is a 404 —
    // no message stored, no retrieval, no extraction, no project/loop updates.
    if (await conversations.GetConversationAsync(convId, user.UserId, ct) is null)
        return ApiError.ConversationNotFound();

    var reply = await agent.HandleAsync(user.UserId, convId, req.Message, tokenSink: null, ct);
    return Results.Ok(ReplyDto.From(reply));
});

app.MapGet("/capabilities", async (ICapabilityRegistry registry, CancellationToken ct) =>
{
    var capabilities = await registry.GetAllAsync(ct);
    return Results.Ok(capabilities.Select(c => new
    {
        c.Id,
        c.Name,
        c.Description,
        c.InputTypes,
        c.OutputTypes,
        c.Provider,
        c.Model,
        c.Availability,
        c.LocalOnly,
        confidence = Math.Round(c.Confidence, 2),
        c.LastVerifiedAt,
    }));
});

if (app.Environment.IsDevelopment())
{
    app.MapPost("/debug/chat", async (ChatRequest req, IUserContext user, IConversationStore conversations, IAgent agent, CancellationToken ct) =>
    {
        if (!Guid.TryParse(req.ConversationId, out var convId))
            return ApiError.BadRequest("conversationId must be a GUID (start one via POST /conversations).");
        if (await conversations.GetConversationAsync(convId, user.UserId, ct) is null)
            return ApiError.ConversationNotFound();

        var reply = await agent.HandleAsync(user.UserId, convId, req.Message, tokenSink: null, ct);
        return Results.Ok(DebugReplyDto.From(reply));
    });
}

app.MapPost("/chat/confirm", async (ConfirmRequest req, IUserContext user, IConversationStore conversations, IAgent agent, CancellationToken ct) =>
{
    if (!Guid.TryParse(req.ConversationId, out var convId))
        return ApiError.BadRequest("conversationId must be a GUID.");
    if (await conversations.GetConversationAsync(convId, user.UserId, ct) is null)
        return ApiError.ConversationNotFound();

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

    using var scope = scopes.CreateScope();
    var sp = scope.ServiceProvider;

    // Validate existence + ownership before streaming anything (clean 404, no side effects).
    if (await sp.GetRequiredService<IConversationStore>().GetConversationAsync(convId, user.UserId, ct) is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(ApiJson.Serialize(
            new ApiError("ConversationNotFound", "The conversation was not found.", ctx.TraceIdentifier)), ct);
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

    var agent = sp.GetRequiredService<IAgent>();
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
        // Sanitized error over the stream; the detail is logged server-side by correlation id.
        var (_, code, safe) = ApiError.Classify(ex);
        logger.LogError(ex, "SSE error {Code} correlationId={CorrelationId}", code, ctx.TraceIdentifier);
        await Send("error", new ApiError(code, safe, ctx.TraceIdentifier));
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

    // Open with memory-grounded starters so the client can show them and the user needn't initiate.
    // Deliberately the deterministic Greeter, not IGreeter: the ready frame must be instant, and a
    // real model (possibly cold-loading) would block it. The session rephrases it in the background
    // and sends a `greeting` upgrade frame when the model-written wording is ready.
    Greeting greeting;
    using (var greetScope = scopes.CreateScope())
        greeting = await greetScope.ServiceProvider.GetRequiredService<Companion.Core.Services.Greeter>().GreetAsync(user.UserId, ct);

    await WebSocketConversation.RunAsync(socket, scopes, logger, user.UserId, conv.Id, greeting, ct);
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

// ---- the inner monologue (between-session reflection) ----

// The companion's diary: recent musings, newest first. Watermark-only quiet days are skipped —
// this endpoint answers "what has it been thinking?", not "when did the job run?".
app.MapGet("/reflections", async (IUserContext user, IReflectionStore store, CancellationToken ct) =>
{
    var recent = await store.GetRecentAsync(user.UserId, 20, ct);
    return Results.Ok(recent
        .Where(r => r.HasMusing)
        .Select(r => new
        {
            id = r.Id,
            createdAt = r.CreatedAt,
            musing = r.Musing,
            messagesReflected = r.MessagesReflected,
        }));
});

// What it currently wonders about (open, not-yet-voiced curiosities).
app.MapGet("/curiosities", async (IUserContext user, IReflectionStore store, CancellationToken ct) =>
{
    var open = await store.GetOpenCuriositiesAsync(user.UserId, ct);
    return Results.Ok(open.Select(c => new
    {
        id = c.Id,
        question = c.Question,
        about = c.About,
        reason = c.Reason,
        createdAt = c.CreatedAt,
    }));
});

// ---- the prompt catalog (every editable piece of her language) ----

app.MapGet("/prompts", () => Results.Ok(Companion.Core.Services.Prompts.All
    .OrderBy(d => d.Key, StringComparer.Ordinal)
    .Select(d => new
    {
        d.Key,
        d.Description,
        @default = d.Default,
        @override = Companion.Core.Services.Prompts.OverrideFor(d.Key),
    })));

app.MapPut("/prompts/{key}", (string key, PromptEditRequest req) =>
{
    if (!Companion.Core.Services.Prompts.Exists(key))
        return Results.NotFound(new { error = $"No prompt named \"{key}\"." });
    if (string.IsNullOrWhiteSpace(req.Text))
        return ApiError.BadRequest("Provide non-empty text (or DELETE to reset to the default).");
    if (req.Text.Length > 16_000)
        return ApiError.BadRequest("Prompt text is limited to 16000 characters.");

    Companion.Core.Services.Prompts.SetOverride(key, req.Text);
    PromptOverrideFiles.Save(promptsDir, key, req.Text);
    return Results.Ok(new { key, @override = req.Text });
});

app.MapDelete("/prompts/{key}", (string key) =>
{
    if (!Companion.Core.Services.Prompts.Exists(key))
        return Results.NotFound(new { error = $"No prompt named \"{key}\"." });

    Companion.Core.Services.Prompts.SetOverride(key, null);
    PromptOverrideFiles.Delete(promptsDir, key);
    return Results.Ok(new { key, @override = (string?)null });
});

// Her own tastes — inspectable: subject, natural description, how established, why, how often reinforced.
app.MapGet("/preferences", async (IUserContext user, IPreferenceStore store, CancellationToken ct) =>
{
    var prefs = await store.GetAllAsync(user.UserId, ct);
    return Results.Ok(prefs.Select(p => new
    {
        p.Id,
        p.Subject,
        description = p.Describe(),
        affinity = Math.Round(p.Affinity, 2),
        confidence = Math.Round(p.Confidence, 2),
        p.Reason,
        p.Observations,
        p.UpdatedAt,
    }));
});

// The dated events she's holding (open anticipations): encouragement due, follow-ups owed.
app.MapGet("/anticipations", async (IUserContext user, IAnticipationStore store, CancellationToken ct) =>
{
    var open = await store.GetOpenAsync(user.UserId, ct);
    return Results.Ok(open.Select(a => new { a.Id, a.Description, a.EventAt, a.Status, a.Evidence }));
});

// ---- outreach (she reaches out on her own; needs Outreach.NtfyUrl configured) ----

// The log of messages she sent on her own initiative, newest first.
app.MapGet("/outreach", async (IUserContext user, IOutreachStore store, CancellationToken ct) =>
{
    var recent = await store.GetRecentAsync(user.UserId, 20, ct);
    return Results.Ok(recent.Select(m => new { m.Id, m.Text, m.Source, m.SentAt }));
});

// Send a test push so the ntfy setup can be verified without waiting for a real outreach.
app.MapPost("/outreach/test", async (HttpContext ctx, IOutboundChannel channel, CancellationToken ct) =>
{
    if (!channel.Configured)
        return Results.Json(
            new ApiError("OutreachUnavailable", "Outreach isn't configured (set Outreach.NtfyUrl — see docs/OUTREACH.md).", ctx.TraceIdentifier),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var ok = await channel.SendAsync("Companion", "Test message — outreach is wired up. She can reach you here.", ct);
    return ok
        ? Results.Ok(new { sent = true })
        : Results.Json(
            new ApiError("OutreachDeliveryFailed", "The push could not be delivered — check the ntfy URL/token and server logs.", ctx.TraceIdentifier),
            statusCode: StatusCodes.Status502BadGateway);
});

// Run a reflection pass right now (demo/debug; the worker normally does this while you're away).
app.MapPost("/reflect", async (IUserContext user, IReflector reflector, CancellationToken ct) =>
{
    var result = await reflector.ReflectAsync(user.UserId, ct);
    if (result is null)
        return Results.Ok(new { reflected = false, reason = "Nothing new enough to think about (or the model's output was unusable — see logs)." });

    return Results.Ok(new
    {
        reflected = true,
        musing = result.Reflection.Musing,
        quietDay = !result.Reflection.HasMusing,
        curiosities = result.Curiosities.Select(c => new { c.Question, c.About, c.Reason }),
    });
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

// The configurable personality: the active preset + the catalog to pick from, and a setter.
app.MapGet("/personality", async (IUserContext user, IProfileStore store, IPersonalityService personality, CancellationToken ct) =>
{
    var profile = await store.GetOrCreateAsync(user.UserId, ct);
    var active = personality.Active(profile);
    return Results.Ok(new
    {
        active = new { active.Name, active.Label, active.Description },
        presets = personality.Presets.Select(p => new { p.Name, p.Label, p.Description }),
    });
});

app.MapPut("/personality", async (PersonalityRequest req, IUserContext user, IProfileStore store, IPersonalityService personality, CancellationToken ct) =>
{
    var preset = personality.Find(req.Preset);
    if (preset is null)
        return Results.BadRequest(new { error = "unknown_personality", message = $"No personality named '{req.Preset}'." });

    await store.SetPersonalityPresetAsync(user.UserId, preset.Name, ct);
    return Results.Ok(new { active = new { preset.Name, preset.Label, preset.Description } });
});

// The companion's identity (name / gender / pronouns), separate from its personality.
app.MapGet("/identity", async (IUserContext user, IProfileStore store, IPersonalityService personality, CancellationToken ct) =>
{
    var profile = await store.GetOrCreateAsync(user.UserId, ct);
    var id = personality.Identity(profile);
    return Results.Ok(new { id.Name, id.Gender, id.Pronouns });
});

app.MapPut("/identity", async (IdentityRequest req, IUserContext user, IProfileStore store, IPersonalityService personality, CancellationToken ct) =>
{
    await store.SetIdentityAsync(user.UserId, req.Name, req.Gender, req.Pronouns, ct);
    var profile = await store.GetOrCreateAsync(user.UserId, ct);
    var id = personality.Identity(profile);
    return Results.Ok(new { id.Name, id.Gender, id.Pronouns });
});

// Thumbs up/down for a rich UI. Routed through the brain so it uses the exact same last-exchange
// reconstruction as saying "that was great" out loud.
app.MapPost("/feedback", async (FeedbackRequest req, IUserContext user, IConversationStore conversations, IAgent agent, CancellationToken ct) =>
{
    if (!Guid.TryParse(req.ConversationId, out var convId))
        return ApiError.BadRequest("conversationId must be a GUID.");
    if (await conversations.GetConversationAsync(convId, user.UserId, ct) is null)
        return ApiError.ConversationNotFound();

    var positive = req.Rating.Equals("positive", StringComparison.OrdinalIgnoreCase);
    var phrase = positive ? "that was great" : "that was unhelpful";
    if (!string.IsNullOrWhiteSpace(req.Note))
        phrase += " — " + req.Note;

    var reply = await agent.HandleAsync(user.UserId, convId, phrase, tokenSink: null, ct);
    return Results.Ok(ReplyDto.From(reply));
});

// Speech-to-text: upload an audio file (multipart 'file') and get its transcript back. Requires a
// configured Whisper server (Models.Transcription — see docs/AUDIO.md). The client then sends the
// text as a normal chat turn, so this stays a small, composable building block for the voice loop.
app.MapPost("/transcribe", async (HttpContext ctx, IFormFile file, CancellationToken ct) =>
{
    var transcriber = ctx.RequestServices.GetService<ITranscriber>();
    if (transcriber is null)
        return Results.Json(
            new ApiError("TranscriptionUnavailable", "Transcription isn't configured (set Models.Transcription).", ctx.TraceIdentifier),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    if (file is null || file.Length == 0)
        return ApiError.BadRequest("Upload an audio file as multipart form-data field 'file'.");

    await using var stream = file.OpenReadStream();
    var text = await transcriber.TranscribeAsync(stream, file.FileName, ct);
    return Results.Ok(new { text });
}).DisableAntiforgery();

// Text-to-speech: POST { "text": "...", "voice": "optional" } and get audio bytes back — the output
// half of the voice loop. Requires a configured TTS server (Models.Speech — see docs/AUDIO.md). The
// client plays the returned audio; a voice face pairs this with /transcribe and /chat/stream.
app.MapPost("/speak", async (HttpContext ctx, SpeakRequest? body, CancellationToken ct) =>
{
    var synthesizer = ctx.RequestServices.GetService<ISpeechSynthesizer>();
    if (synthesizer is null)
        return Results.Json(
            new ApiError("SpeechUnavailable", "Text-to-speech isn't configured (set Models.Speech).", ctx.TraceIdentifier),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    if (body is null || string.IsNullOrWhiteSpace(body.Text))
        return ApiError.BadRequest("Provide text to speak as JSON field 'text'.");

    var audio = await synthesizer.SynthesizeAsync(body.Text, body.Voice, ct);
    return Results.File(audio.Audio, audio.ContentType);
});

app.Run();

/// <summary>
/// Handles the receive/serve loop for one WebSocket connection. The `ready` frame carries the
/// instant deterministic greeting; when a real model is configured, a background task rephrases
/// it and sends a `greeting` frame so the client can upgrade the wording in place — a slow or
/// cold-loading model never leaves the user staring at a blank screen. All frames go through a
/// send lock: WebSockets allow only one send at a time, and the upgrade races the chat stream.
/// </summary>
internal sealed class WebSocketConversation
{
    private readonly WebSocket _socket;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly string _userId;
    private readonly Guid _defaultConversationId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private WebSocketConversation(
        WebSocket socket, IServiceScopeFactory scopes, ILogger logger, string userId, Guid defaultConversationId)
    {
        _socket = socket;
        _scopes = scopes;
        _logger = logger;
        _userId = userId;
        _defaultConversationId = defaultConversationId;
    }

    public static async Task RunAsync(
        WebSocket socket, IServiceScopeFactory scopes, ILogger logger, string userId, Guid defaultConversationId,
        Greeting greeting, CancellationToken ct)
    {
        var session = new WebSocketConversation(socket, scopes, logger, userId, defaultConversationId);
        try
        {
            await session.RunCoreAsync(greeting, ct);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected mid-work (tab closed, refresh). A normal way for a
            // browser-held socket to end — not an error.
            logger.LogDebug("WebSocket session ended: client disconnected.");
        }
        catch (WebSocketException ex)
        {
            // Abrupt close without a close handshake — same story, just less polite.
            logger.LogDebug("WebSocket session ended abruptly: {Reason}", ex.Message);
        }
    }

    private async Task RunCoreAsync(Greeting greeting, CancellationToken ct)
    {
        await SendAsync(new
        {
            type = "ready",
            conversationId = _defaultConversationId.ToString(),
            message = greeting.Message,
            openers = greeting.Openers,
        }, ct);

        // Rephrase the greeting with the model off the critical path; canceled when the session ends.
        using var upgradeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upgrade = UpgradeGreetingAsync(greeting, upgradeCts.Token);

        try
        {
            var buffer = new byte[8 * 1024];
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (text, closed) = await ReceiveTextAsync(buffer, ct);
                if (closed)
                    break;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                ClientFrame? frame;
                try { frame = System.Text.Json.JsonSerializer.Deserialize<ClientFrame>(text, ApiJson.Options); }
                catch { await SendAsync(new { type = "error", message = "invalid JSON" }, ct); continue; }
                if (frame is null)
                    continue;

                var convId = Guid.TryParse(frame.ConversationId, out var c) ? c : _defaultConversationId;

                using var scope = _scopes.CreateScope();
                var agent = scope.ServiceProvider.GetRequiredService<IAgent>();

                try
                {
                    switch ((frame.Type ?? "").ToLowerInvariant())
                    {
                        case "chat":
                            await HandleChatAsync(agent, convId, frame.Text ?? "", ct);
                            break;
                        case "confirm":
                            var outcome = await agent.ConfirmAsync(_userId, convId, frame.Token ?? "", frame.Confirmed ?? false, ct);
                            await SendAsync(ReplyFrame(outcome), ct);
                            break;
                        default:
                            await SendAsync(new { type = "error", message = $"unknown frame type '{frame.Type}'" }, ct);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Sanitized error frame; the detail is logged server-side by correlation id.
                    var (_, code, safe) = ApiError.Classify(ex);
                    var correlationId = Guid.NewGuid().ToString("N")[..8];
                    _logger.LogError(ex, "WebSocket error {Code} correlationId={CorrelationId}", code, correlationId);
                    await SendAsync(new { type = "error", error = code, message = safe, correlationId }, ct);
                }
            }
        }
        finally
        {
            // A still-running rephrase has no one left to greet; stop it and let it unwind
            // (it handles its own exceptions, so this await never throws).
            upgradeCts.Cancel();
            await upgrade;
        }

        if (_socket.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    private async Task UpgradeGreetingAsync(Greeting grounded, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var rephraser = scope.ServiceProvider.GetService<IGreetingRephraser>();
            if (rephraser is null)
                return; // offline mocks: the deterministic greeting IS the greeting

            var upgraded = await rephraser.RephraseAsync(grounded, _userId, ct);
            if (string.Equals(upgraded.Message, grounded.Message, StringComparison.Ordinal))
                return; // the model added nothing (or fell back) — the shown greeting stands

            await SendAsync(new { type = "greeting", message = upgraded.Message }, ct);
        }
        catch (OperationCanceledException)
        {
            // Session ended first — nothing to upgrade.
        }
        catch (Exception ex)
        {
            // The shown deterministic greeting is already a complete opener; an upgrade can
            // only ever be cosmetic, so any failure here is quietly dropped.
            _logger.LogDebug(ex, "Greeting upgrade skipped: {Reason}", ex.Message);
        }
    }

    private async Task HandleChatAsync(IAgent agent, Guid convId, string message, CancellationToken ct)
    {
        var sink = new TokenChannelSink();
        async Task<AgentReply> Run()
        {
            try { return await agent.HandleAsync(_userId, convId, message, sink, ct); }
            finally { sink.Complete(); }
        }

        var work = Run();
        await foreach (var chunk in sink.Reader.ReadAllAsync(ct))
            await SendAsync(new { type = "token", text = chunk }, ct);
        var reply = await work;
        await SendAsync(ReplyFrame(reply), ct);
    }

    private static object ReplyFrame(AgentReply reply) => new
    {
        type = "reply",
        kind = reply.Kind.ToString(),
        intent = reply.Intent.ToString(),
        text = reply.Text,
        confirmationToken = reply.ConfirmationToken,
    };

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(ApiJson.Serialize(payload));
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State != WebSocketState.Open)
                return;
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<(string? Text, bool Closed)> ReceiveTextAsync(byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, ct);
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

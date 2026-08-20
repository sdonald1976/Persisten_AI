using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Api;

/// <summary>The companion's own state: reflections, curiosities, preferences, anticipations, outreach, and the prompt catalog.</summary>
internal static class CompanionStateEndpoints
{
    public static void MapCompanionStateEndpoints(this WebApplication app, string promptsDir)
    {
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

        // The diary grouped into trains of thought: what she has kept working through across
        // reflection passes, most recently developed first.
        app.MapGet("/reflections/threads", async (IUserContext user, IReflectionStore store, CancellationToken ct) =>
        {
            var threads = await store.GetThreadsAsync(user.UserId, 12, ct);
            return Results.Ok(threads.Select(t => new
            {
                threadId = t.ThreadId,
                startedAt = t.StartedAt,
                lastDevelopedAt = t.LastDevelopedAt,
                length = t.Length,
                settled = t.Settled,
                entries = t.Entries.Select(r => new { id = r.Id, createdAt = r.CreatedAt, musing = r.Musing }),
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
        // Knowledge gaps: what she knows she doesn't know, with provenance and lifecycle
        // (docs/KNOWLEDGE_GAPS.md). Recording a gap is not a promise to ask about it.
        app.MapGet("/gaps", async (IUserContext user, IGapStore gaps, int? count, CancellationToken ct) =>
            Results.Ok((await gaps.GetRecentAsync(user.UserId, count ?? 50, ct)).Select(g => new
            {
                id = g.Id,
                kind = g.Kind,
                subject = g.Subject,
                source = g.Source,
                sourceRef = g.SourceRef,
                occurrences = g.Occurrences,
                firstSeen = g.FirstSeen,
                lastSeen = g.LastSeen,
                status = g.Status,
                pursuit = g.Pursuit,
                curiosityId = g.CuriosityId,
                resolutionNote = g.ResolutionNote,
            })));

        app.MapPost("/reflect", async (IUserContext user, IReflector reflector, CancellationToken ct) =>
        {
            var outcome = await reflector.ReflectAsync(user.UserId, ct);
            if (outcome.Result is not { } result)
            {
                return Results.Ok(new
                {
                    reflected = false,
                    skipReason = outcome.SkipReason,
                    reason = outcome.SkipReason switch
                    {
                        ReflectionSkipReason.Disabled =>
                            "Reflection is turned off in configuration (Companion:EnableReflection).",
                        ReflectionSkipReason.NotEnoughMaterial =>
                            "Nothing new enough to think about since the last pass.",
                        ReflectionSkipReason.ModelOutputUnusable =>
                            "The model replied, but not with anything usable — nothing was stored, so the same "
                            + "material will be retried. Check the summarizer model and see the logs.",
                        _ => "No reflection was produced.",
                    },
                });
            }

            return Results.Ok(new
            {
                reflected = true,
                musing = result.Reflection.Musing,
                quietDay = !result.Reflection.HasMusing,
                curiosities = result.Curiosities.Select(c => new { c.Question, c.About, c.Reason }),
            });
        });

    }
}

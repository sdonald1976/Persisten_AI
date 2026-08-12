# Outreach: she reaches out on her own

Until now, everything started with you opening the app. Outreach is the companion's first true
initiative: when you've been away a while and her between-session reflection left her holding a
real question, she sends a push notification to your phone —

> **Ava**
> You crossed my mind. How did the interview go?

Built on [ntfy](https://ntfy.sh) — a dead-simple pub/sub push service with free phone/desktop
apps, self-hostable if you'd rather keep it fully local.

## Setup (two minutes)

1. Install the **ntfy** app (Android / iOS / desktop) and subscribe to a topic with a
   hard-to-guess name, e.g. `companion-x7k2m9q4`. (Topic names are the only secret on the public
   ntfy.sh server — pick accordingly, or self-host.)
2. Point the companion at the same topic in `src/Companion.Api/appsettings.json`:

   ```jsonc
   "Outreach": {
     "NtfyUrl": "https://ntfy.sh/companion-x7k2m9q4",
     "AuthToken": "",          // only for a protected self-hosted server
     "AwayHours": 24,          // how long you must be gone before she considers it
     "MinHoursBetween": 48,    // at most one message per this window
     "QuietStartHour": 22,     // server-local; no messages 22:00 → 08:00
     "QuietEndHour": 8
   }
   ```

3. Restart the API and verify the wiring:

   ```bash
   curl -X POST http://localhost:5266/outreach/test -H "Authorization: Bearer <your-api-token>"
   ```

   Your phone should buzz. `GET /outreach` shows everything she's ever sent, with provenance.

Leave `NtfyUrl` empty and the whole feature is off — no worker, no checks, nothing.

## When she actually messages

Every gate must pass, in order:

1. **You've met.** She never messages a user who has never talked to her.
2. **The budget is clear** — at least `MinHoursBetween` since her last outreach. Rare is what
   keeps an unprompted message special instead of needy.
3. **It's a decent hour** — quiet hours are respected (window may cross midnight).
4. **She has something real to say**, in priority order:
   - **Event-day encouragement** — you mentioned a dated plan ("I have an interview on
     Thursday"); on the morning of the day she sends *"Good luck with your interview today —
     I'll be thinking of you."* This is the one message that does **not** wait for you to be
     away: arriving before the event is the point.
   - **Post-event follow-up** *(requires you've been away `AwayHours`)* — the event passed and
     was never asked about in-app: *"Been thinking of you — how did your interview go?"*
   - **A held curiosity** *(requires away)* — a question minted by her reflection pass:
     *"You crossed my mind. What's your mentor's name?"*

   Nothing to say → no message; she never sends an empty "hey" because a timer fired.

Dated plans are captured by a conservative deterministic detector during normal conversation
("I have X tomorrow / on Friday", "my X is on Saturday") and follow a once-each arc:
encouragement on the day, follow-up after, then closed — whether voiced by push or by the
greeting, whichever moment comes first. Events more than a week past expire silently (the sleep
cycle) — she doesn't dredge up an interview from three weeks ago. `GET /anticipations` shows
what she's currently holding.

Sending **spends** the curiosity (voiced-on-send, the same rule as greeting openers and in-chat
offers), so she'll never re-ask it in-app — the next session just picks up from it naturally. A
failed delivery burns nothing: the curiosity stays open, no budget is consumed, and a later
check retries.

## Design notes

- `OutreachService` (Core) holds all judgment and is fully covered by tests; `OutreachWorker`
  (Api) is a dumb timer; `NtfyChannel` (Infrastructure) is one HTTP POST behind
  `IOutboundChannel`, so SMS/email/another push service later is an adapter, not a redesign.
- Every send is logged (`OutboundMessages` table) with provenance (`curiosity:{id}`) — outreach
  stays honest and auditable like every other "alive" behavior in this project.

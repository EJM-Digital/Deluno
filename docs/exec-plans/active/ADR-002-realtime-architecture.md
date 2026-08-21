# ADR-002 — realtime architecture

**Status:** proposed, not executed
**Date:** 2026-08-18
**Context:** `main` @ `14fa1dc`
**Related:** `AUDIT-001-scheduling-and-contention.md` (findings 1–3 measure the
cost of polling), `ADR-001-module-boundaries.md` (Step 1 must land first)

The goal is that every screen reflects the truth without the user pressing
anything, and without the browser asking the same seventeen questions every five
seconds.

## The question that is not the question

"Should we use SSE?" is the wrong first question, and answering it first leads
to rewriting the transport and still having a page that polls.

The app already runs SignalR over WebSockets, authenticated, with backoff
reconnect. Measured against what the dashboard actually needs, the transport is
not what is missing. Four other things are.

### 1. The events describe actions, not state

Thirteen events are published. Mapped against the seventeen calls the dashboard
loader makes:

| Dashboard needs | Event today |
|---|---|
| search cycles | `SearchRunCompleted` |
| download telemetry, speed | `DownloadProgress` (partial) |
| indexer / client health | `HealthChanged` (partial) |
| movies, series catalogues | **none** |
| movie / series wanted counts | **none** |
| libraries | **none** |
| library automation state | **none** |
| search retry windows | **none** |
| upcoming episodes | **none** |
| setup progress | **none** |
| settings | **none** |
| policy sets | **none** |
| quality profiles | **none** |
| dashboard metrics | **none** |

Nothing fires when a movie is added. A perfectly wired client would still have
to poll for most of the page, because there is nothing to listen to.

### 2. Dispatch lifecycle events are consumed (#135)

The backend publishes thirteen action names and the frontend now models all of
them. The Queue screen refreshes for `DispatchGrabCompleted`, `DispatchDetected`,
`DispatchImportStarted` and `DispatchImportCompleted`, while
`DispatchGrabAttempt` produces a user-facing toast without causing a refetch.
The Activity screen refreshes its dispatch/import view for detection and import
completion. `ImportPipelineService` now publishes the previously missing
`DispatchImportStarted` event when a dispatch-backed import begins.

### 3. The stream is lossy and cannot be resumed

`SignalRRealtimeEventPublisher` buffers into a bounded channel of 1,000 with
`BoundedChannelFullMode.DropOldest`. Under load it silently discards events —
instrumented as `RealtimeEventsDropped`, which is good, but still discarded.

There are no sequence numbers, so a client cannot detect a gap. The client
reconnects with backoff, but `onreconnected` only updates a status badge; it
never refetches. A client that trusted this stream would drift out of sync and
never recover.

**This is the finding that matters most.** It is why "just subscribe to events
instead of polling" would be actively worse than polling: polling is at least
self-healing.

### 4. Everything is broadcast to everyone

`hubContext.Clients.All` for every event. No groups, no per-screen scoping. Every
connected tab receives every event whether it can use it or not.

And on the client side, react-router loaders replace whole payloads. There is no
entity-keyed store to apply a delta to.

## Transport decision

The deployment model decides this. Per `PRODUCT_NORTH_STAR.md`, Deluno is "the
single, local control plane for a personal media library" — one user, one
instance, a Windows installer or a Docker container, realistically a handful of
browser tabs. This is not a fan-out problem.

| | WebSocket (raw) | SSE | SignalR (current) |
|---|---|---|---|
| Direction | duplex | server→client only | duplex |
| Client subscribes to groups | hand-rolled | needs a second POST channel | built in |
| Reconnect + backoff | hand-rolled | browser auto-retry | built in |
| Resume after gap | hand-rolled | **native `Last-Event-ID`** | **absent** |
| Transport fallback | none | n/a | negotiates WS → SSE → long-poll |
| Auth over the wire | hand-rolled | header or query | `accessTokenFactory`, already wired |
| HTTP/1.1 connection cap | not affected | **6 per origin, shared with fetches** | not affected |
| Already working here | no | no | **yes** |

SSE's one genuine advantage is `Last-Event-ID`: resumability is part of the
protocol rather than something you build. That is exactly the capability
finding 3 says is missing — so it deserves to be taken seriously rather than
dismissed.

It is not enough to switch for. SSE is unidirectional, so per-screen
subscriptions would need a parallel POST channel, reintroducing the coordination
SignalR already provides. Under HTTP/1.1 its connection shares the six-per-origin
budget with the app's own fetches, which bites precisely when a user has several
tabs open — the case we are optimising for. And adopting it means running two
realtime stacks during migration.

**Decision: keep SignalR. Build sequencing and resume on top of it.** Borrow
SSE's idea, not its transport. This is roughly a hundred lines, against a
migration that touches every screen.

### Correct the transport configuration

The client currently sets `skipNegotiation: true` with
`transport: HttpTransportType.WebSockets`. That is a latency optimisation which
**disables SignalR's fallback entirely**. Behind a reverse proxy that does not
upgrade WebSockets — which the Docker deployment path makes plausible — the app
has no realtime at all and no degraded mode.

Restore negotiation so the stack degrades WS → SSE → long-polling. Deluno then
gets SSE anyway, as a fallback, without being rebuilt around it.

Done: negotiation restored (#133).

## The design

### Envelope

Every event carries a monotonic sequence and a subject:

```
{ seq: 8417, name: "MovieChanged", subject: "movie:01a0…", at: "2026-…Z", data: { … } }
```

`seq` comes from a single counter. The publisher already serialises everything
through one channel reader, so it is the natural place to stamp it.

### Resume window

Keep the last N envelopes (start at 5,000 — a bounded ring, memory only; this is
a single-instance app and a cold start is a full rehydrate anyway).

On connect the client sends its last known `seq`:

- gap is inside the window → replay the missing envelopes, client is caught up
- gap is beyond the window, or the client has none → return `resync-required`,
  client refetches from REST and adopts the current `seq`

That single rule is what makes a lossy channel safe. `DropOldest` stops being a
correctness bug and becomes a backpressure policy, because dropping now
guarantees a resync rather than silent drift.

### Entity change events

Add a small generic family alongside the existing action events, carrying
identity and version rather than whole objects:

```
MovieChanged / SeriesChanged / LibraryChanged / QualityProfileChanged /
PolicySetChanged / IntakeSourceChanged / SettingsChanged / AutomationStateChanged
```

Each says *what changed*, so the client invalidates that key and refetches it if
it is on screen. Not *what the new value is* — that keeps payloads small, avoids
leaking a second serialisation of every contract, and sidesteps ordering bugs
where a stale event overwrites a fresh read.

The existing thirteen action events stay. They drive toasts, the activity feed
and progress bars — things that are genuinely about an event happening, not
about state.

### Groups

Screens subscribe to what they show: `dashboard`, `library:{id}`, `queue`,
`activity`. `Clients.All` becomes `Clients.Group(...)`. This was implemented in
#136, including subject-aware resume replay and reconnect resubscription. It also
gives a natural seam if Deluno ever grows real multi-user scoping.

### Client

Replace the revalidate-on-interval pattern with an entity-keyed cache
(TanStack Query is the obvious fit — it already models keys, staleness and
invalidation, and it removes the loader/interval pattern rather than adding to
it).

- hydrate from REST on mount
- `*Changed` event → invalidate that key
- reconnect → process replay, or refetch everything on `resync-required`
- **gate every remaining interval on `document.visibilityState`**

REST does not go away. It becomes the hydration and recovery path, which is what
makes the whole thing correct.

### Keep one poll

A slow heartbeat — 60s, visibility-gated — as a safety net against a bug in the
feed. Realtime systems that delete every fallback path fail silently and
invisibly. This is cheap insurance, and at 60s it is 1/12th of today's dashboard
cost by itself.

## Consequences

**Good.** The measured 204 requests/min from one idle tab goes to near zero at
rest. Screens are correct within milliseconds rather than up to five seconds.
The five orphaned `Dispatch*` events become useful. Reconnect stops being a
silent correctness hole. Backgrounded tabs cost nothing.

**Cost.** Every screen's data access changes. This is a frontend rewrite of the
data layer, not a transport swap — which is precisely why the transport question
should not consume the budget.

**Risk.** Getting resume wrong is worse than polling, because the failure is
silent. The resume path needs a test that kills the connection mid-stream,
replays, and asserts the client converged.

**Ordering.** `ADR-001` Step 1 must finish first. The realtime work touches the
same endpoint files that Quality, Connections and Libraries are still being
carved out of; running both means constant conflict, and ADR-001's own ground
rules forbid it.

## Not doing

- Switching to raw WebSockets. It means reimplementing reconnect, fallback and
  auth that already work.
- Switching to SSE as the primary transport. Its resumability is worth copying;
  its unidirectionality and connection-cap cost are not worth paying.
- Pushing full entity payloads over the feed. Identity plus version, then refetch.
- Persisting the replay buffer. Single instance, in-memory, cold start rehydrates.

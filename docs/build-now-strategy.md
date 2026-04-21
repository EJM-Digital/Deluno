# Build-Now Strategy

## Goal

Build quickly now without making commercialization impossible later.

## Constraints

- Build clean-room.
- Do not copy code from existing media automation projects.
- Keep the app self-hosted and user-operated.
- Treat metadata providers as replaceable.
- Avoid product decisions that assume perpetual free access to commercial metadata.

## Fastest Sensible Technical Baseline

For the first phase, optimize for iteration speed over perfect final tech choices.

Recommended approach:

- one monorepo
- one backend app shell
- one future web app
- isolated movie and series packages
- plain JavaScript runtime to avoid toolchain drag in the first few days

This is intentionally pragmatic. Once the product shape is proven, the codebase can be typed more heavily or moved deeper into TypeScript without undoing the boundaries.

## What We Build First

### Phase 0

- repo skeleton
- clear domain boundaries
- basic API shell
- docs and guardrails

### Phase 1

Movie vertical slice:

- add movie by title or external ID
- store movie record
- attach monitoring state
- stub search/grab/import lifecycle

### Phase 2

Series vertical slice:

- add show by title or external ID
- store show, seasons, episodes
- attach monitoring state
- stub search/grab/import lifecycle

### Phase 3

Shared platform capabilities:

- settings
- provider credentials
- activity feed
- notifications

### Phase 4

Real integrations:

- metadata provider adapters
- download client adapters
- indexer adapters

## Guardrails

- No movie table or module may depend on series business rules.
- No series module may reuse movie decision logic.
- Shared packages must remain infrastructure-only.
- External IDs must be first-class from day one.
- Metadata provenance must be stored so provider swaps are possible later.

## Product Strategy

We are not monetizing yet.

That means the immediate goal is:

- validate demand
- prove usability
- prove that one app with two engines is materially better

It does not mean:

- ignore licensing forever
- ignore provider abstraction
- use copied code as a shortcut

## Immediate Next Tasks

1. Add a storage layer with domain-separated persistence.
2. Define DTOs for movies, shows, seasons, and episodes.
3. Implement provider interfaces and local stub adapters.
4. Build the first add-and-list flows for both domains.
5. Add a frontend shell once the API shape stabilizes.

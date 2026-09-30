# Deluno Metadata Gateway

This is the narrow, public-facing metadata gateway used by normal Deluno
installations. It implements the existing Deluno broker contract:

- `GET /health`
- `GET /metadata/search?mediaType=movies|tv&query=...&year=...&providerId=...`

It is intentionally not a general media API. It accepts only a title lookup,
year/type, and an optional TMDb ID; it does not receive library paths, queues,
download history, credentials from Deluno users, or a library inventory.

## Security and operational policy

- `TMDB_API_KEY` is a Cloudflare Worker secret, never a source-controlled value
  or a Deluno UI setting.
- Successful searches are cached for 12 hours in KV.
- Each install (IP address) may make 120 calls a minute to TMDb or TheTVDB, with
  a best-effort shared ceiling of 400 every 10 seconds under TMDb's own limit.
  Only calls that reach a provider count: answers served from the gateway cache
  are never counted or limited. The counts use Workers Rate Limiting bindings
  (`wrangler.jsonc`, no KV writes); a missing binding allows the call. A spent
  budget answers `429 rate_limited` with `Retry-After`. The Worker emits no application logs containing the title query.
- TMDb provider failures return generic, actionable Deluno status messages and
  never disclose upstream credentials or response bodies.
- OMDb is not part of the launch gateway.

## Deploy

1. Install/log into Wrangler: `npx wrangler login`.
2. From this directory, run `npx wrangler deploy`. Wrangler provisions the KV
   binding declared in `wrangler.jsonc` when the account supports automatic
   binding provisioning; otherwise create a KV namespace named
   `deluno-metadata-cache` and add its ID to that file.
3. Set the provider secret without echoing it: `npx wrangler secret put
   TMDB_API_KEY`.
4. Verify `https://<worker>.<account>.workers.dev/health`, then point each
   Deluno deployment at `DELUNO_METADATA_BROKER_URL=https://<worker>.<account>.workers.dev`.
5. Run a controlled movie and TV lookup before enabling the gateway for a real
   library.

Use a Cloudflare custom domain only after the Worker proves healthy; it is a
routing change and does not alter Deluno's broker contract.

## TheTVDB (TV first)

The gateway also serves TheTVDB v4 under `/metadata/tvdb/...` (routes, cache
lifetimes and the terms they follow are in `docs/metadata-broker-contract.md`).
It needs two Worker secrets, set the same way as `TMDB_API_KEY`:

- `TVDB_API_KEY` - required for the TVDB routes (they answer 503 without it).
- `TVDB_PIN` - only for a subscriber-supported key; leave unset for a licensed key.

Every TVDB payload carries an `attribution` object; Deluno must show it.
Code is in `src/tvdb.js`, tests in `test/tvdb.test.js` (run by the CI metadata
gateway check).

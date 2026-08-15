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
- Public callers are best-effort limited to 30 lookups per IP address per
  minute. The Worker emits no application logs containing the title query.
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

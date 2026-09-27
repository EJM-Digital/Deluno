# Deluno Metadata Broker Contract

Deluno supports three metadata modes:

- `direct`: Deluno calls provider APIs from the local install using locally configured keys.
- `broker`: Deluno calls a metadata broker service so users do not need provider keys.
- `hybrid`: Deluno calls the broker first, then falls back to local direct provider keys.

The application provider expects broker services to expose the following contract.

## Search

`GET /metadata/search`

Query parameters:

- `mediaType`: `movies` or `tv`.
- `query`: title text.
- `year`: optional release/start year.
- `providerId`: optional provider-specific ID for exact refreshes.

Response:

```json
{
  "provider": "deluno-broker",
  "mode": "broker",
  "resultCount": 1,
  "results": [
    {
      "provider": "tmdb",
      "providerId": "603",
      "mediaType": "movies",
      "title": "The Matrix",
      "originalTitle": "The Matrix",
      "year": 1999,
      "overview": "A computer hacker learns...",
      "posterUrl": "https://image.tmdb.org/t/p/w500/...",
      "backdropUrl": "https://image.tmdb.org/t/p/w1280/...",
      "rating": 8.2,
      "ratings": [],
      "genres": ["Action", "Science Fiction"],
      "imdbId": "tt0133093",
      "externalUrl": "https://www.themoviedb.org/movie/603"
    }
  ]
}
```

## Local Development Endpoint

The app exposes a local authenticated broker-compatible endpoint at:

- `GET /api/metadata/broker/status`
- `GET /api/metadata/broker/search`

This endpoint deliberately bypasses broker/hybrid orchestration and uses direct TMDb lookup, so it can be used to verify the contract without causing recursive provider calls.

## Hosted Broker Notes

A hosted broker can implement the same `/metadata/search` response shape and add:

- provider-key isolation
- attribution enforcement
- request caching
- rate-limit protection
- provider failover
- telemetry for source quality and latency

Commercial/licensing terms must be reviewed before any hosted broker is offered beyond internal development.

## Language and ratings region

Deluno's Settings → Metadata page stores a details language
(`metadata.language`, e.g. `en`, `pt-BR`) and a ratings-region country
(`metadata.certificationCountry`, e.g. `US`, `AU`). Neither has a parameter in
the `/metadata/search` contract above, and the managed gateway deliberately
accepts only title lookup terms, media type/year, and optional TMDb IDs (see
below) — it is not a general-purpose query passthrough.

Both settings therefore apply on the **direct** TMDb path only:

- `direct` mode sends the saved language as TMDb's `language` query parameter
  on search and detail requests, and picks a movie's or show's certification
  for the saved country from TMDb's `release_dates` / `content_ratings`
  detail data, falling back to `US` when that country has none.
- `broker` mode returns whatever language and certification the broker itself
  chose; Deluno cannot ask it for a different one under this contract.
- `hybrid` mode behaves like `broker` for anything the broker answers, and
  like `direct` only for the fallback calls it makes itself.

A future broker contract could add `language` and `region` search parameters
to close this gap; until then, an installation that cares about a
non-English language or a non-US ratings board should use `direct` mode with
its own TMDb key.

## Episode groups (TV numbering, #863)

`GET /tv/{id}/episode-groups`

A series-only, gateway-shaped endpoint alongside the `/tv/{id}/catalogue`
gateway route the app's own broker client already calls: TMDb's
`episode_groups` feature, which publishes alternate episode orderings such as
DVD order, absolute numbering, and story arcs. Deluno's numbering switcher
uses this to offer only the orderings a title actually has, instead of a
fixed list nobody checked against the provider.

Response:

```json
{
  "groups": [
    {
      "type": 3,
      "providerGroupId": "5f0a1b2c3d4e5f6a7b8c9d0e",
      "name": "DVD Order",
      "episodes": [
        { "originalSeasonNumber": 1, "originalEpisodeNumber": 1, "groupSeasonNumber": 1, "groupEpisodeNumber": 1 }
      ]
    }
  ]
}
```

`type` follows TMDb's own codes (1 original air date, 2 absolute, 3 DVD, 4
digital, 5 story arc, 6 production, 7 TV). Deluno only resolves episodes by
type 2 and 3 today; every other type may still be listed, with an empty
`episodes` array, purely so its name can be shown as something the provider
offers. When the broker has no episode groups for a title (including when it
does not implement this route at all), hybrid and direct modes fall back to
TMDb directly, the same as every other broker call in this contract.

## Managed gateway deployment

The Deluno-managed Cloudflare Worker is deployed at
`https://deluno-metadata-gateway.ejmdigital.workers.dev`. Its Deluno-owned
TMDb credential is stored as a Worker secret, and it serves the public broker
contract without exposing that credential to Deluno users or installations.
The address is a deployment endpoint, not a user-configurable setting: fresh
supported Deluno installs use it by default.

Before a release, verify controlled movie and TV lookups. Operators only need
`DELUNO_METADATA_PROVIDER_MODE` and `DELUNO_METADATA_BROKER_URL` when they
deliberately override the managed route. The gateway accepts only title lookup
terms, media type/year, and optional TMDb IDs; it does not receive library
paths, inventory, queues, or user provider keys.

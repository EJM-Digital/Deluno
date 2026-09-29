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

The ratings region applies on the **direct** TMDb path only:

- `direct` mode picks a movie's or show's certification for the saved country
  from TMDb's `release_dates` / `content_ratings` detail data, falling back to
  `US` when that country has none.
- `broker` mode returns whatever certification the broker itself chose.
- `hybrid` mode behaves like `broker` for anything the broker answers.

The details language reaches every mode through the translation routes below
(#1018), so it needs no `language` search parameter.

## Translations (#1018)

Radarr keeps every translation TMDb publishes for a film and shows the one
chosen as its movie info language; Sonarr has no series translations. Deluno
does the same with one small route per source. English or a blank language
changes nothing: no translation is ever asked for.

`GET /metadata/movie/{id}/translations` and `GET /metadata/tv/{id}/translations`
(TMDb id) return every language TMDb has text for, once per title, so the answer
is cached for a week whatever language an install asks for:

```json
{
  "provider": "tmdb",
  "providerId": "603",
  "translations": [
    { "language": "fr", "region": "FR", "title": "Matrix", "overview": "Un pirate...", "tagline": "Bienvenue dans le monde reel." }
  ]
}
```

Entries with no title, overview or tagline are left out. TV titles that have a
TheTVDB id also use `GET /metadata/tvdb/tv/{tvdbId}/translations/{lang}`, where
`{lang}` is TheTVDB's three-letter code (`fra`, `deu`, ...). It returns
`{ "provider": "tvdb", "language": "fra", "title": "...", "overview": "..." }`,
or `404 not_found` when TheTVDB has none (cached seven days).

The app resolves each field in this order: TheTVDB (TV only), then TMDb's
translation (the saved language and region, else the same language in any
region), then the text the provider already sent. The title itself is never
replaced: Deluno keeps it (folders, searches, the blocklist), stores the
localized name beside it, and adds it to the title's known alternate names so
release matching still sees every spelling. Direct and hybrid installs fall
back to TMDb's own `/{movie|tv}/{id}/translations` and TheTVDB's
`/series/{id}/translations/{lang}` with their own keys.

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

## TheTVDB (TV first, #989)

For TV, TheTVDB is the first source and TMDb the fallback (the same choice
Sonarr makes, so season/episode, absolute and specials numbering line up with
release names). Movies stay on TMDb. TheTVDB is served through the broker
exactly like TMDb: the key lives only in the gateway's secrets, and an install
using the managed broker never holds it.

### What stays and what changes

- A show's identity is unchanged: the TMDb id remains the provider id, so every
  existing show keeps its id. TheTVDB's id is stored beside it
  (`MetadataSearchResult.TvDbId`, in the stored metadata) and is filled in on
  the show's next normal metadata refresh; there is no separate backfill.
- TheTVDB wins field by field (title, overview, artwork, banner, logo, genres,
  network, status, runtime, aliases). A field TheTVDB leaves empty keeps TMDb's
  value. Ratings, cast, crew, keywords and the ratings-board certification for
  the owner's chosen country stay TMDb's. TheTVDB's status words map to Deluno's
  (`Continuing` to `Returning Series`, `Upcoming` to `In Production`).
- Search: TheTVDB's hits come first, each tied to its TMDb identity through
  TheTVDB's remote ids; results only TMDb found follow. A TheTVDB show with no
  TMDb id cannot be stored and is left to TMDb's own results.
- Episodes: the aired list and each episode's absolute number come from
  TheTVDB (blank titles/overviews/dates are filled from TMDb by season and
  episode number). DVD and absolute orderings come from TheTVDB's season types.
- No file is renumbered by a refresh. A show whose existing episodes would not
  all appear in TheTVDB's aired list stays on TMDb's numbering
  (`MetadataCatalogueChoice`); moving it is a deliberate choice through the
  normal numbering and rename preview. Alternate orderings are only taken from
  TheTVDB for a show that sits on TheTVDB's numbering.
- If TheTVDB does not answer while a show sits on TheTVDB's numbering, the
  show's episodes are left as they are and the next refresh tries again (decided
  by the owner, 30 Sep 2026). A TheTVDB episode list that does not match the show's
  episodes is rejected rather than applied, and Activity notes it. A wrong
  TheTVDB id therefore cannot overwrite a show's episodes.
- A TheTVDB failure is never a metadata outage: the app falls back to TMDb and
  does not record the failure against the provider status.

### Gateway routes

All under the gateway's `/metadata` base, `GET`, JSON. Every payload carries
`attribution: { text, url }`. `404` means TheTVDB has no such record; `503`
means TheTVDB (or its key) is unavailable, and the app falls back to TMDb.

| Route | Answer |
| --- | --- |
| `/metadata/tvdb/search?query=&year=` | `{ provider: "tvdb", resultCount, results: [series] }` |
| `/metadata/tvdb/tv/{tvdbId}` | `{ provider: "tvdb", series }` (extended record, English translation, best artwork) |
| `/metadata/tvdb/tv/{tvdbId}/catalogue` | the `/tv/{id}/catalogue` shape, each episode also has `absoluteNumber` and `runtimeMinutes` |
| `/metadata/tvdb/tv/{tvdbId}/episode-groups` | the episode-groups shape: type 3 = DVD order, type 2 = absolute order, keyed to the aired list |
| `/metadata/tvdb/remote/{imdbId}` | `{ tvdbId }` for an IMDb id, when a search result carried no TheTVDB id |

`series` is `{ provider, tvdbId, tmdbId, imdbId, title, originalTitle, year,
overview, posterUrl, backdropUrl, bannerUrl, logoUrl, screenshots, genres,
network, status, runtimeMinutes, originalLanguage, certification, aliases,
externalUrl }`. The TMDb search/detail payload also gained `tvDbId`.

### Gateway secrets (set by the owner of the gateway, never committed)

| Secret | Meaning |
| --- | --- |
| `TVDB_API_KEY` | The TheTVDB v4 API key. Required for the routes above; without it they answer `503`. |
| `TVDB_PIN` | Not set on the broker: its key is a licensed project key and sends no PIN. Only meaningful in direct mode, for a subscriber-supported ("user-supported") key. |

`npx wrangler secret put TVDB_API_KEY` (no PIN: the broker key is licensed). The bearer
token from `/login` lasts one month and is cached in KV for 25 days.

### Direct mode

A person not using the broker can set their own key in the environment
(`TVDB_API_KEY`, optional `TVDB_PIN`, or `Deluno:Metadata:TvdbApiKey` /
`Deluno:Metadata:TvdbPin` in configuration). Direct and hybrid mode then call
TheTVDB's v4 API themselves and map the answers to the same records. Broker-only
mode never reads a local key.

### TheTVDB terms this follows (read 29 Sep 2026)

Sources: https://thetvdb.com/api-information and the `thetvdb/v4-api` README
and OpenAPI file.

- Licence: free with attribution for projects under USD 50k annual revenue;
  paid tiers above that (USD 1,000/yr from 50k, USD 10,000/yr from 250k, contact
  for 1M+). They "reserve the right to change our interfaces, fees, or licensing
  terms at any point without notice". Open-source projects can ask for a discount.
- Two access models: a negotiated licence for companies, or a user-supported key
  where each end user holds a paid subscription (USD 12/yr) and their PIN is
  sent with the key at `/login`.
- Attribution: "with a direct link to TheTVDB.com must be displayed to end users
  viewing metadata from our API." Deluno shows "TV metadata provided by
  TheTVDB" linking to https://thetvdb.com on the add-show search results and on
  a show's page whenever it carries a TheTVDB id; the broker sends the same text
  in `attribution`.
- Caching: TheTVDB recommends a caching proxy and to "set time to live for each
  endpoint based on the needs of your users"; rarely-changing lists may be kept
  "a week or even longer". No fixed maximum is stated. The gateway uses: search
  12 h, series record 6 h, episode list 6 h, DVD/absolute orders 24 h, IMDb to
  TheTVDB id 7 days. The app keeps a 30 minute (search) / 1 hour (series) memory
  cache only to avoid repeat identical questions; episode lists are not cached
  in the app. Artwork is public CDN imagery and is cached on disk like TMDb's.
- Rates: none published. The gateway's own 30 lookups per IP per minute applies
  to these routes too.
- Bearer token: valid one month.

Key type (decided by the owner, 29 Sep 2026): the gateway's TheTVDB key is a
licensed project key, so the broker sends no PIN and `TVDB_PIN` is not needed
for it. PIN support stays only for direct-mode users who hold their own
subscriber-supported key.

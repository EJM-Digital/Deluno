# Account requests

Rows of the coverage register that can only be proven with an outside account. The owner (31 Oct 2026) has no account with these services and will not create one: the features are built and their failures are in plain words, proven as far as possible without an account (in-process tests, recorded refusals and no-key replies), and people test them once they are live and report problems. Each row below carries `"waived"` in `docs/testing/coverage-register.json`, keeps issue 1343 and its `account`, is not counted as a gap and does not block `--strict`, and is counted and listed apart by `node scripts/check-coverage-register.mjs`. When an account exists, prove the rows, add the proofs, and remove the `waived` field.

## Waived until an account exists (owner, 31 Oct 2026)

85 rows. What proving each service needs is the account named; the register row's `reason` and `needs` say what to record or run.

### Trakt (19)

Needs: a real Trakt account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `settings-lists.import-lists-page-settings.trakt-save-trakt-app-credentials-posts-api-trakt`: Trakt: Save Trakt app credentials POSTs /api/trakt/credentials, toasts "Trakt app credentials saved", card switches to Connect
- `api.integrations.post-api-trakt-credentials`: POST /api/trakt/credentials: Save Trakt client credentials
- `api.integrations.post-api-trakt-connect`: POST /api/trakt/connect: Start Trakt device authorisation
- `api.integrations.post-api-trakt-connect-poll`: POST /api/trakt/connect/poll: Poll Trakt for the device authorisation result
- `api.integrations.post-api-trakt-disconnect`: POST /api/trakt/disconnect: Disconnect Trakt
- `integrations.4-trakt.start-device-authorization-code-url`: Start device authorization (code + URL)
- `integrations.4-trakt.poll-device-token-approved-tokens`: Poll device token: approved -> tokens
- `integrations.4-trakt.poll-400-429-pending-410-expired-418-404`: Poll: 400/429 pending, 410 expired, 418/404 denied
- `integrations.4-trakt.poll-409-already-used-mapped-to-denied-and`: Poll: 409 already used (mapped to Denied) and other statuses (5xx -> throws)
- `integrations.4-trakt.refresh-token-new-pair-on-success-null-when`: Refresh token: new pair on success; null when refresh token stopped working
- `integrations.4-trakt.authenticated-get-with-401-token-revoked-429-rate`: Authenticated GET with 401 (token revoked), 429 rate limit (Trakt sends Retry-After), 5xx -> `EnsureSuccessStatusCode` throws; no retry or backoff
- `integrations.4-trakt.token-provider-no-credentials-null-never-connected-n`: Token provider: no credentials -> null; never connected -> null; valid token reused; near-expiry refreshed and saved; dead refresh token marks "needs reconnect"
- `integrations.4-trakt.token-provider-under-two-concurrent-callers-double-r`: Token provider under two concurrent callers (double refresh race)
- `integrations.4-trakt.api-api-trakt-status-credentials-connect-connect-pol`: API: `/api/trakt/status`, `/credentials`, `/connect`, `/connect/poll`, `/disconnect`; secret and tokens stored protected
- `integrations.4-trakt.list-source-trakt-built-in-trending-authenticated`: List source: Trakt built-in Trending, authenticated
- `integrations.4-trakt.list-source-built-in-list-while-trakt-not`: List source: built-in list while Trakt not connected is refused with a plain message
- `integrations.4-trakt.list-sources-trakt-popular-anticipated-weekly-watche`: List sources: Trakt Popular, Anticipated, Weekly Watched, Recommended
- `integrations.4-trakt.list-source-trakt-custom-list-watchlist-via-authenti`: List source: Trakt custom list / watchlist via authenticated API path (`.../items`), and unauthenticated RSS fallback
- `integrations.4-trakt.list-source-mdblist-with-api-key-api-mdblist`: List source: MDBList with API key (`api.mdblist.com`), cursor paging

### TMDb API key (8)

Needs: a real TMDb API key account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.metadata-broker-monitoring-e.hybrid-mode-the-broker-is-asked-first-when`: Hybrid mode: the broker is asked first; when it returns nothing or fails, Deluno falls back to direct TMDb with the local key, and counts the fallback in a metr
- `engines.metadata-broker-monitoring-e.direct-mode-tmdb-search-movie-or-search-tv`: Direct mode: TMDb search/movie or search/tv with include_adult=false, query, year (year or first_air_date_year) and the saved language, at most 12 results, each
- `engines.metadata-broker-monitoring-e.direct-exact-lookup-a-confirmed-tmdb-404-is`: Direct exact lookup: a confirmed TMDb 404 is Missing with no title-search fallback (a deleted record is never relinked to a similar title)
- `engines.metadata-broker-monitoring-e.detail-fields-runtime-or-first-episode-length-popularity`: Detail fields: runtime (or first episode length), popularity, vote count, studio, network, collection name and id, director, tagline, homepage, original languag
- `engines.metadata-broker-monitoring-e.tmdb-catalogue-through-the-gateway-metadata-tv-id`: TMDb catalogue through the gateway (/metadata/tv/{id}/catalogue) first, then directly from TMDb with the local key when the gateway cannot answer, and nothing w
- `engines.metadata-broker-monitoring-e.direct-tmdb-catalogue-seasons-0-upward-at-most`: Direct TMDb catalogue: seasons 0 upward (at most 50), one call per season, a season TMDb cannot give is skipped and the rest kept, episodes numbered 0 or less d
- `engines.metadata-broker-monitoring-e.release-dates-for-a-movie-in-cinemas-digital`: Release dates for a movie (in cinemas, digital, physical): broker /movie/{id}/release-dates first, then TMDb direct, earliest of each type across regions, premi
- `engines.metadata-broker-monitoring-e.movie-collection-tmdb-collection-a-movie-belongs-to`: Movie collection (TMDb collection a movie belongs to, including titles not yet in the catalogue): broker first, hybrid falls back to direct TMDb, broker-only re

### TheTVDB (7)

Needs: a real TheTVDB account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.2-metadata.tvdb-direct-login-with-api-key-subscriber-pin`: TVDB direct: login with API key; subscriber PIN only when configured
- `integrations.2-metadata.tvdb-direct-wrong-key-login-refused-expired-token`: TVDB direct: wrong key (login refused), expired token re-login on 401, token cached 25 days
- `integrations.tvdb.findbyimdbidasync`: tvdb does FindByImdbIdAsync against the real app and reports every outcome (src/Deluno.Integrations/Metadata/TvdbDirectClient.cs)
- `integrations.tvdb.getepisodegroupsasync`: tvdb does GetEpisodeGroupsAsync against the real app and reports every outcome (src/Deluno.Integrations/Metadata/TvdbDirectClient.cs)
- `integrations.tvdb.getseriesasync`: tvdb does GetSeriesAsync against the real app and reports every outcome (src/Deluno.Integrations/Metadata/TvdbDirectClient.cs)
- `integrations.tvdb.gettranslationasync`: tvdb does GetTranslationAsync against the real app and reports every outcome (src/Deluno.Integrations/Metadata/TvdbDirectClient.cs)
- `integrations.tvdb.searchasync`: tvdb does SearchAsync against the real app and reports every outcome (src/Deluno.Integrations/Metadata/TvdbDirectClient.cs)

### OpenSubtitles.com (6)

Needs: a real OpenSubtitles.com account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `connections.cross-cutting-security-healt.subtitle-provider-replies-have-recordings`: Subtitle-provider replies (OpenSubtitles.com, SubDL, Addic7ed) have recordings
- `integrations.5-subtitle-providers.opensubtitles-search-by-title-year-episode-imdb-api`: OpenSubtitles: search by title/year/episode/imdb (API key header)
- `integrations.5-subtitle-providers.opensubtitles-login-username-password-key-bearer-tok`: OpenSubtitles: login (username/password/key) -> bearer token used for download
- `integrations.5-subtitle-providers.opensubtitles-download-link-then-file-bytes`: OpenSubtitles: download link then file bytes
- `integrations.5-subtitle-providers.opensubtitles-daily-quota-spent-http-406-with-reset`: OpenSubtitles: daily quota spent, HTTP 406 with reset time
- `integrations.5-subtitle-providers.opensubtitles-200-with-no-link-quota-spent-no`: OpenSubtitles: 200 with no link = quota spent, no reset time

### Plex (6)

Needs: a real Plex account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.1b-plex.test-wrong-token-401-shown-as-unreachable-refused`: Test: wrong token (401) shown as unreachable "refused the token"
- `integrations.1b-plex.watchlist-from-plex-tv-discover-read-ok`: Watchlist from plex.tv discover: read OK
- `integrations.1b-plex.account-is-everyone-proof-plex-tv-owner-check`: "Account is everyone" proof (plex.tv owner check + server `/accounts`): positive and every negative (friend, home user, second account, other id, owner id 1, to
- `integrations.1b-plex.leaving-soon-collection-create-one-per-section-add`: Leaving-soon collection: create one per section, add members, show on home when asked
- `integrations.1b-plex.collection-put-back-a-hand-edit-take-off`: Collection: put back a hand edit, take off home, delete when empty (never an item)
- `integrations.7-extra-outbound-integra.plex-tv-watchlist-and-owner-proof-counted-in`: Plex.tv watchlist and owner proof (counted in section 1b)

### a private tracker (4)

Needs: a real a private tracker account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.3-cardigann-style-tracke.tr13-sign-in-refused-wrong-password-plain-message`: Sign-in refused (wrong password) -> plain message
- `integrations.3-cardigann-style-tracke.tr19-api-key-passkey-rsskey-trackers-about-129-definition`: API-key / passkey / rsskey trackers (about 129 definitions): the ordinary API key field becomes the tracker's apikey setting; missing credentials named before a
- `integrations.3-cardigann-style-tracke.tr20-two-factor-token-settings-on-private-sites`: Two-factor / token settings on private sites
- `integrations.3-cardigann-style-tracke.tr36-search-grab-and-test-of-a-real-private`: Search, grab and test of a real private tracker (login, cookie, passkey)

### Assrt (4)

Needs: a real Assrt account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.5-subtitle-providers.assrt-api-key-search-then-per-match-detail`: Assrt (API key): search then per-match detail; one detail with two languages = two candidates; season pack picks the wanted episode's file
- `integrations.5-subtitle-providers.assrt-download-refused-key-quota`: Assrt: download; refused key; quota
- `integrations.subtitles-assrt.downloadasync`: subtitles-assrt does DownloadAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/AssrtSubtitleProvider.cs)
- `integrations.subtitles-assrt.searchasync`: subtitles-assrt does SearchAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/AssrtSubtitleProvider.cs)

### Ktuvit (4)

Needs: a real Ktuvit account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.5-subtitle-providers.ktuvit-sign-in-session-cookie-search-movie-read`: Ktuvit: sign in (session cookie), search movie, read subtitle table
- `integrations.5-subtitle-providers.ktuvit-no-credentials-asks-nothing-episodes-by-serie`: Ktuvit: no credentials = asks nothing; episodes by series id/season/episode; download signs in again, requests identifier, fetches file
- `integrations.subtitles-ktuvit.downloadasync`: subtitles-ktuvit does DownloadAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/KtuvitSubtitleProvider.cs)
- `integrations.subtitles-ktuvit.searchasync`: subtitles-ktuvit does SearchAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/KtuvitSubtitleProvider.cs)

### Titlovi (4)

Needs: a real Titlovi account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.5-subtitle-providers.titlovi-sign-in-gettoken-search-skip-whole-season`: Titlovi: sign in (`gettoken`), search, skip whole-season packs; no credentials = asks nothing
- `integrations.5-subtitle-providers.titlovi-download-wrong-credentials-token-expiry`: Titlovi: download; wrong credentials; token expiry
- `integrations.subtitles-titlovi.downloadasync`: subtitles-titlovi does DownloadAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/TitloviSubtitleProvider.cs)
- `integrations.subtitles-titlovi.searchasync`: subtitles-titlovi does SearchAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/TitloviSubtitleProvider.cs)

### a TMDb API key (the Worker run for real with it) (3)

Needs: a real a TMDb API key (the Worker run for real with it) account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.metadata-broker-monitoring-e.the-tmdb-api-key-is-never-part-of`: The TMDb API key is never part of a cache key or a response body
- `engines.metadata-broker-monitoring-e.a-search-with-a-providerid-returns-that-one`: A search with a providerId returns that one record (TMDb detail with external_ids, credits, release_dates, content_ratings, videos, keywords appended), or an em
- `engines.metadata-broker-monitoring-e.tmdb-5xx-or-a-network-error-on-a`: TMDb 5xx or a network error on a search answers 503 provider_unavailable (generic text, no upstream body or credential) and is remembered for 60 s

### SubDL (3)

Needs: a real SubDL account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.5-subtitle-providers.subdl-api-key-search-download`: SubDL (API key): search, download
- `integrations.subtitles-subdl.downloadasync`: subtitles-subdl does DownloadAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/SubDlSubtitleProvider.cs)
- `integrations.subtitles-subdl.searchasync`: subtitles-subdl does SearchAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/SubDlSubtitleProvider.cs)

### SubSource (3)

Needs: a real SubSource account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.5-subtitle-providers.subsource-api-key-search-download`: SubSource (API key): search, download
- `integrations.subtitles-subsource.downloadasync`: subtitles-subsource does DownloadAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/SubSourceSubtitleProvider.cs)
- `integrations.subtitles-subsource.searchasync`: subtitles-subsource does SearchAsync against the real app and reports every outcome (src/Deluno.Integrations/Subtitles/Providers/SubSourceSubtitleProvider.cs)

### a Letterboxd member (1)

Needs: a real a Letterboxd member account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.4-trakt.list-sources-rss-and-letterboxd-feeds`: List sources: RSS and Letterboxd feeds

### a signed-in subtitle provider (1)

Needs: a real a signed-in subtitle provider account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `settings-lists.title-level-subtitle-actions.real-subtitle-providers-answering-real-requests-search-download`: Real subtitle providers answering real requests (search, download) from a running Deluno

### Discord, Slack, Telegram and Pushover (1)

Needs: a real Discord, Slack, Telegram and Pushover account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `settings-lists.notifications-backend-engine.triggers-fire-only-when-their-preference-is-on`: Triggers fire only when their preference is on: Grab, Title added (on and off), Application update (first run records version only, a version change fires when 

### Discord, Slack, Telegram, Pushover (1)

Needs: a real Discord, Slack, Telegram, Pushover account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.webhook-ingestion-seerr-outb.no-provider-payload-was-recorded-from-the-real`: No provider payload was recorded from the real service; every shape is asserted against the code's reading of each service's documentation, with a stub HTTP han

### MDBList (1)

Needs: a real MDBList account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `settings-lists.import-lists-providers-and.provider-mdblist-public-url-as-a-custom-list`: Provider MDbList public URL as a custom list without an API key

### NZBGeek, NZBPlanet, DrunkenSlug and NinjaCentral (1)

Needs: a real NZBGeek, NZBPlanet, DrunkenSlug and NinjaCentral account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.1-torznab-and-newznab.tn49-version-dialect-differences-between-real-servers-jac`: Version/dialect differences between real servers (Jackett, Prowlarr, NZBGeek, NZBPlanet, DrunkenSlug, NinjaCentral: attr names, error codes, caps layout)

### OMDb (1)

Needs: a real OMDb account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.2-metadata.omdb-ratings-imdb-rotten-tomatoes-added-to-a`: OMDb ratings (IMDb, Rotten Tomatoes) added to a TMDb record; OMDb quota/wrong key

### OpenSubtitles.com (a real provider auth failure and quota reply) (1)

Needs: a real OpenSubtitles.com (a real provider auth failure and quota reply) account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.5-subtitle-providers.fetch-service-provider-auth-failure-recorded-once-in`: Fetch service: provider auth failure recorded once in Activity; non-auth failures not; rate-limited provider left alone; quota-exhausted pauses provider with re

### the metadata broker (a real refused gateway key) (1)

Needs: a real the metadata broker (a real refused gateway key) account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.2-metadata.key-refusal-at-the-gateway-broker-answers-401`: Key refusal at the gateway (broker answers 401/403) and wording "did not accept Deluno's connection"

### the metadata Worker run for real (wrangler) with and without its TMDb and TheTVDB secrets (1)

Needs: a real the metadata Worker run for real (wrangler) with and without its TMDb and TheTVDB secrets account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.metadata-broker-monitoring-e.tmdb-routes-answer-503-error-service-unavailable-when`: TMDb routes answer 503 {error:"service_unavailable"} when the TMDB_API_KEY secret is not configured

### the metadata Worker run for real with a TMDb key (1)

Needs: a real the metadata Worker run for real with a TMDb key account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.metadata-broker-monitoring-e.the-same-remembered-failure-rule-on-the-catalogue`: The same remembered-failure rule on the catalogue-type TMDb routes (catalogue, release dates, collection, translations, episode groups, popular)

### the metadata Worker run for real without its TheTVDB secret (1)

Needs: a real the metadata Worker run for real without its TheTVDB secret account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.metadata-broker-monitoring-e.without-the-tvdb-api-key-secret-every-tvdb`: Without the TVDB_API_KEY secret every TVDB route answers 503 service_unavailable ("TheTVDB is not configured on this metadata service.")

### TMDb (1)

Needs: a real TMDb account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `integrations.tmdb.searchdirectasync`: tmdb does SearchDirectAsync against the real app and reports every outcome (src/Deluno.Integrations/Metadata/TmdbMetadataProvider.cs)

### TMDb and TheTVDB API keys (1)

Needs: a real TMDb and TheTVDB API keys account or key; record its real replies (success, refused key, quota) into tests/fixtures/recorded and replay them, or run the row against the live service.

- `engines.metadata-broker-monitoring-e.no-test-sends-a-request-through-a-real`: No test sends a request through a real Cloudflare Worker (wrangler dev) or reaches real TMDb or TheTVDB; every gateway test uses a hand-written stub of the prov


// Records what the subtitle providers that need an account or a key really answer when the account or key is wrong, and Trakt's answers to a
// client id it does not know: the calls SubDlSubtitleProvider, SubSourceSubtitleProvider, OpenSubtitlesSubtitleProvider, KtuvitSubtitleProvider,
// TitloviSubtitleProvider and the Trakt client make, tests/fixtures/recorded/<app>/<date>/*.json (#1301).
//
//   node scripts/recorded-replies/docker/subtitle-refusals.mjs
//
// No account is made and no real key or password is used: every call carries a made-up key or login, which is what a wrong one looks like to the
// service, so each recording is the service's own refusal. The success replies (a sign-in, a search that finds something, a download link) need an
// account and are listed in tests/fixtures/recorded/MISSING.md. Real unauthenticated calls from the public internet with the user agent
// `Deluno-test-recording`; the services have no API version in their reply, so the recordings sit under the date of the recording.
import { Client, addSecret, save } from './lib.mjs';

const day = new Date().toISOString().slice(0, 10);
const agent = { 'user-agent': 'Deluno-test-recording', accept: 'application/json' };
const wrongKey = 'wrong-key-recorded-by-deluno-tests';
const wrongUser = 'nobody-recorded-by-deluno-tests@example.com';
const wrongPassword = 'wrong-password-recorded-by-deluno-tests';
for (const secret of [wrongKey, wrongPassword]) addSecret(secret);

const keep = ['content-type', 'retry-after', 'www-authenticate', 'location'];

// ---- OpenSubtitles.com ----
const opensubtitles = new Client('https://api.opensubtitles.com', { headers: { ...agent, 'api-key': wrongKey }, keep });
const osSearch = await opensubtitles.get('/api/v1/subtitles?query=Sintel&languages=en&year=2010');
// OpenSubtitles answers a query not written in its canonical form (lower case, parameters in order) with a 301 to that form, then refuses the key there.
const osCanonical = await opensubtitles.get('/api/v1/subtitles?languages=en&query=sintel&year=2010');
const osLogin = await opensubtitles.send({ method: 'POST', path: '/api/v1/login', json: { username: wrongUser, password: wrongPassword } });
const osDownload = await opensubtitles.send({ method: 'POST', path: '/api/v1/download', json: { file_id: 1 } });
save('opensubtitles', day, 'wrong-key', 'A search (answered with a redirect to its canonical form, then followed), a sign-in and a download request sent with an API key (and a username and password) OpenSubtitles does not know: its own refusal of each.', [osSearch, osCanonical, osLogin, osDownload]);

// ---- SubDL ----
const subdl = new Client('https://api.subdl.com', { headers: agent, keep });
const subdlSearch = await subdl.get(`/api/v1/subtitles?api_key=${wrongKey}&film_name=Sintel&type=movie&subs_per_page=10&languages=EN`);
save('subdl', day, 'wrong-key', 'A search sent with an API key SubDL does not know: its own refusal.', [subdlSearch]);

// ---- SubSource ----
const subsource = new Client('https://api.subsource.net', { headers: { ...agent, 'x-api-key': wrongKey }, keep });
const subsourceSearch = await subsource.get('/api/v1/subtitles?query=Sintel&langs=en&year=2010');
save('subsource', day, 'wrong-key', 'A search sent with an API key SubSource does not know: its own refusal.', [subsourceSearch]);

// ---- Titlovi ----
const titlovi = new Client('https://kodi.titlovi.com', { headers: agent, keep });
const titloviToken = await titlovi.send({ method: 'POST', path: `/api/subtitles/gettoken?username=${encodeURIComponent(wrongUser)}&password=${wrongPassword}&json=True` });
save('titlovi', day, 'wrong-login', 'The sign-in (gettoken) with a username and password Titlovi does not know: its own refusal.', [titloviToken]);

// ---- Ktuvit ----
const ktuvit = new Client('https://www.ktuvit.me', { headers: agent, keep: [...keep, 'set-cookie'] });
const ktuvitLogin = await ktuvit.send({ method: 'POST', path: '/Services/MembershipService.svc/Login', json: { request: { Email: wrongUser, Password: wrongPassword } } });
save('ktuvit', day, 'wrong-login', 'The sign-in with an e-mail address and password Ktuvit does not know: its own refusal.', [ktuvitLogin]);

// ---- Trakt ----
const trakt = new Client('https://api.trakt.tv', { headers: { ...agent, 'trakt-api-version': '2', 'trakt-api-key': wrongKey }, keep: [...keep, 'x-ratelimit'] });
const traktCode = await trakt.send({ method: 'POST', path: '/oauth/device/code', json: { client_id: wrongKey } });
const traktPoll = await trakt.send({ method: 'POST', path: '/oauth/device/token', json: { code: 'not-a-device-code', client_id: wrongKey, client_secret: wrongPassword } });
const traktRefresh = await trakt.send({ method: 'POST', path: '/oauth/token', json: { refresh_token: 'not-a-refresh-token', client_id: wrongKey, client_secret: wrongPassword, redirect_uri: 'urn:ietf:wg:oauth:2.0:oob', grant_type: 'refresh_token' } });
const traktTrending = await trakt.get('/movies/trending?limit=1');
save('trakt', day, 'unknown-client-id', 'The device-code request, the device-token poll, the token refresh and a public list read, all sent with a Trakt client id the service does not know: its own refusal of each.', [traktCode, traktPoll, traktRefresh, traktTrending]);

// ---- TMDb, TheTVDB and OMDb read directly with a key of the person's own (direct metadata mode) ----
const tmdb = new Client('https://api.themoviedb.org', { headers: agent, keep });
const tmdbSearch = await tmdb.get(`/3/search/movie?api_key=${wrongKey}&query=The%20Matrix&include_adult=false`);
const tmdbConfiguration = await tmdb.get(`/3/configuration?api_key=${wrongKey}`);
save('tmdbapi', day, 'wrong-key', 'A title search and the configuration read sent to the TMDb v3 API itself (not Deluno\'s metadata gateway) with an API key it does not know: its own refusal of each.', [tmdbSearch, tmdbConfiguration]);
const tvdb = new Client('https://api4.thetvdb.com', { headers: agent, keep });
const tvdbLogin = await tvdb.send({ method: 'POST', path: '/v4/login', json: { apikey: wrongKey } });
save('tvdbapi', day, 'wrong-key', 'The login sent to TheTVDB v4 with an API key it does not know: its own refusal.', [tvdbLogin]);
const omdb = new Client('https://www.omdbapi.com', { headers: agent, keep });
const omdbRatings = await omdb.get(`/?apikey=${wrongKey}&i=tt0133093&plot=short&r=json`);
save('omdb', day, 'wrong-key', 'The ratings read for one title sent to OMDb with an API key it does not know: its own refusal.', [omdbRatings]);

console.log('done');
for (const [name, e] of Object.entries({ osSearch, osCanonical, osLogin, osDownload, subdlSearch, subsourceSearch, titloviToken, ktuvitLogin, traktCode, traktPoll, traktRefresh, traktTrending, tmdbSearch, tmdbConfiguration, tvdbLogin, omdbRatings })) {
  console.log(name, e.reply.status, (e.reply.bodyText ?? e.reply.error ?? '').slice(0, 160).replace(/\s+/g, ' '));
}

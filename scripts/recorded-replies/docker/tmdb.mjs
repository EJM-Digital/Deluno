// Records the replies of the project's deployed metadata gateway (a Cloudflare Worker in front of TMDb and TheTVDB; it holds the keys) to the
// calls Deluno's metadata provider makes through it in broker mode (#1237): title search, the lookup by provider id that the metadata link
// preview and a refresh use, the series catalogue, episode groups, release dates, collection, translations, the popular lists and the TheTVDB routes.
//
//   node scripts/recorded-replies/docker/tmdb.mjs
//
// Real unauthenticated GETs from the public internet, 25 calls in all, with the user agent `Deluno-test-recording`. The gateway has no version, so the
// recordings sit under the date of the recording. A long reply is shortened to its first entries and the note says so. Nothing here needs Docker.
import { Client, replyJson, save } from './lib.mjs';

const GATEWAY = process.env.DELUNO_GATEWAY ?? 'https://deluno-metadata-gateway.ejmdigital.workers.dev';
const g = new Client(GATEWAY, { keep: ['content-type', 'cache-control', 'retry-after'], headers: { 'user-agent': 'Deluno-test-recording', accept: 'application/json' } });
const day = new Date().toISOString().slice(0, 10);
const get = (path) => g.get(path);
/** A long list shortened to its first entries, with the note saying so. */
function shorten(exchange, pick, keep) {
  const body = replyJson(exchange);
  const list = pick(body);
  if (!Array.isArray(list) || list.length <= keep) return '';
  const was = list.length;
  list.length = keep;
  exchange.reply.bodyText = JSON.stringify(body);
  return ` The reply had ${was} entries; the first ${keep} are kept.`;
}

const calls = [
  ['search-movie', 'GET /metadata/search for a film by title and year.', '/metadata/search?mediaType=movies&query=Sintel&year=2010'],
  ['search-movie-by-provider-id', 'GET /metadata/search with a providerId: how a refresh and the metadata link preview read one exact TMDb record.', '/metadata/search?mediaType=movies&query=Sintel&providerId=45745&year=2010'],
  ['search-tv', 'GET /metadata/search for a series by title.', '/metadata/search?mediaType=tv&query=Breaking%20Bad'],
  ['search-tv-by-provider-id', 'GET /metadata/search with a providerId for a series (the link preview of a series).', '/metadata/search?mediaType=tv&query=Breaking%20Bad&providerId=1396'],
  ['search-provider-record-missing', 'GET /metadata/search with a providerId TMDb has no record for: 404 with the error provider_record_missing.', '/metadata/search?mediaType=movies&query=Sintel&providerId=999999999'],
  ['search-no-results', 'GET /metadata/search for a title nothing matches.', '/metadata/search?mediaType=movies&query=Zzqxjv%20Nonexistent%20Title%20Qwerty'],
  ['search-bad-media-type', 'GET /metadata/search with a media type the gateway does not serve.', '/metadata/search?mediaType=books&query=Sintel'],
  ['catalogue-tv', 'GET /metadata/tv/{id}/catalogue: the seasons and episodes of a series (TMDb 1396).', '/metadata/tv/1396/catalogue', (b) => b.seasons?.flatMap?.((s) => s.episodes ?? []) && null],
  ['catalogue-tv-missing', 'GET /metadata/tv/{id}/catalogue for a TMDb id that has no series: 404 not_found.', '/metadata/tv/999999999/catalogue'],
  ['episode-groups-tv', 'GET /metadata/tv/{id}/episode-groups: the alternate episode orderings of a series.', '/metadata/tv/1396/episode-groups'],
  ['release-dates-movie', 'GET /metadata/movie/{id}/release-dates for a film (TMDb 603).', '/metadata/movie/603/release-dates'],
  ['collection-movie', 'GET /metadata/movie/{id}/collection with a TMDb collection id (2344, The Matrix Collection): the collection and its films.', '/metadata/movie/2344/collection'],
  ['collection-missing', 'GET /metadata/movie/{id}/collection with an id TMDb has no collection for: 404 not_found.', '/metadata/movie/999999999/collection'],
  ['translations-movie', 'GET /metadata/movie/{id}/translations for a film.', '/metadata/movie/45745/translations'],
  ['translations-tv', 'GET /metadata/tv/{id}/translations for a series.', '/metadata/tv/1396/translations'],
  ['popular-movies', 'GET /metadata/movies/popular?page=1 (Discover).', '/metadata/movies/popular?page=1'],
  ['popular-tv', 'GET /metadata/tv/popular?page=1 (Discover).', '/metadata/tv/popular?page=1'],
  ['tvdb-search', 'GET /tvdb/search: TheTVDB through the gateway, the first source for a series.', '/metadata/tvdb/search?query=Breaking%20Bad'],
  ['tvdb-series', 'GET /tvdb/tv/{id}: one series from TheTVDB (81189).', '/metadata/tvdb/tv/81189'],
  ['tvdb-catalogue', 'GET /tvdb/tv/{id}/catalogue: the seasons and episodes from TheTVDB.', '/metadata/tvdb/tv/81189/catalogue'],
  ['tvdb-episode-groups', 'GET /metadata/tvdb/tv/{id}/episode-groups: the alternate orderings TheTVDB publishes for a series.', '/metadata/tvdb/tv/81189/episode-groups'],
  ['tvdb-translation', 'GET /metadata/tvdb/tv/{id}/translations/{lang}: the title and overview of a series in French (three-letter code fra).', '/metadata/tvdb/tv/81189/translations/fra'],
  ['tvdb-remote', 'GET /tvdb/remote/{imdb id}: TheTVDB id for an IMDb id.', '/metadata/tvdb/remote/tt0903747'],
  ['tvdb-series-missing', 'GET /tvdb/tv/{id} for an id TheTVDB has no series for.', '/metadata/tvdb/tv/999999999']
];

for (const [state, what, path] of calls) {
  const exchange = await get(path);
  let extra = '';
  if (exchange.reply.bodyText && exchange.reply.bodyText.length > 150_000) {
    // The catalogue of a long series: keep the first two seasons' episodes only.
    const body = replyJson(exchange);
    if (Array.isArray(body?.seasons)) { const total = body.seasons.length; body.seasons = body.seasons.slice(0, 2); exchange.reply.bodyText = JSON.stringify(body); extra = ` The reply had ${total} seasons; the first two are kept.`; }
  }
  extra += shorten(exchange, (b) => b?.translations, 8) || shorten(exchange, (b) => b?.results, 12);
  save('tmdb', day, state, `${what} Through the deployed gateway (${GATEWAY.replace(/^https:\/\//, '')}).${extra}`, [exchange]);
}
console.log('done');

// Records what the time source of the System clock check really answers: the request SystemTimeHealthCheck sends to
// https://api.themoviedb.org/3/configuration (no key at all), with the `Date` header kept, tests/fixtures/recorded/tmdbapi/<date>/*.json (#1301).
//
//   node scripts/recorded-replies/docker/time-source.mjs
//
// A real unauthenticated GET from the public internet. TMDb refuses the call (no key) and still stamps its reply with the time, which is all the check reads.
import { Client, save } from './lib.mjs';

const day = new Date().toISOString().slice(0, 10);
const tmdb = new Client('https://api.themoviedb.org', { headers: { 'user-agent': 'Deluno-test-recording', accept: 'application/json' }, keep: ['content-type', 'date'] });
const configuration = await tmdb.get('/3/configuration');
save('tmdbapi', day, 'configuration-no-key', 'The configuration read the System clock check sends, with no key: TMDb\'s own refusal, stamped with its clock in the Date header.', [configuration]);
console.log('done', configuration.reply.status, configuration.reply.headers.date);

// Records what StevenLu's public list really answers (#1301): the popular-movies JSON and the reply for a list that is not there,
// tests/fixtures/recorded/stevenlu/<date>/*.json.
//
//   node scripts/recorded-replies/docker/list-feeds.mjs
//
// Real unauthenticated GETs from the public internet.
import { Client, save } from './lib.mjs';

const day = new Date().toISOString().slice(0, 10);
const headers = { 'user-agent': 'Deluno-test-recording' };
const stevenlu = new Client('https://s3.amazonaws.com', { headers, keep: ['content-type'] });
const popular = await stevenlu.get('/popular-movies/movies.json');
save('stevenlu', day, 'popular-movies', 'StevenLu\'s popular-movies list (movies.json): a JSON array of titles with their IMDb ids, as the file is served (no account).', [popular]);
const missing = await stevenlu.get('/popular-movies/no-such-list.json');
save('stevenlu', day, 'list-not-found', 'The reply for a list file the bucket does not hold (S3\'s own XML error).', [missing]);

console.log('done', popular.reply.status, missing.reply.status);

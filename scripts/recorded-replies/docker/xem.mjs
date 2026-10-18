// Records TheXEM's public mapping replies, the ones XemSceneMappingProvider asks for (GET /map/all?id=<tvdb id>&origin=tvdb&output=json).
//
//   node scripts/recorded-replies/docker/xem.mjs
//
// A real unauthenticated GET from the public internet with the user agent `Deluno-test-recording`, three calls in all. TheXEM has no version
// of its API, so the recordings sit under the date of the recording. A reply of a mapped show is long (about 74 KB for 500 episodes);
// what is kept is the beginning of the list and the entries around a change of season, and the recording's note says so.
import { Client, save } from './lib.mjs';

const x = new Client('https://thexem.info', { headers: { 'user-agent': 'Deluno-test-recording' } });
const day = new Date().toISOString().slice(0, 10);

const mapped = await x.get('/map/all?id=79824&origin=tvdb&output=json');
const body = JSON.parse(mapped.reply.bodyText);
const all = body.data;
// The entries where the scene numbering differs from the TVDB numbering are what Deluno keeps; those and their neighbours are the useful part.
const differs = (e) => e.scene && e.tvdb && (e.scene.season !== e.tvdb.season || e.scene.episode !== e.tvdb.episode);
const keep = new Set(all.slice(0, 12).map((_, i) => i));
all.forEach((e, i) => { if (differs(e) && (i % 40 === 0 || i < 3 || (i > 0 && !differs(all[i - 1])) || (i < all.length - 1 && !differs(all[i + 1])))) { keep.add(i); if (i > 0) keep.add(i - 1); if (i < all.length - 1) keep.add(i + 1); } });
const kept = [...keep].sort((a, b) => a - b).map((i) => all[i]);
mapped.reply.bodyText = JSON.stringify({ ...body, data: kept });
const size = JSON.stringify(body).length;
save('thexem', day, 'map-all-mapped', `GET /map/all for a show TheXEM maps (TVDB 79824). The reply had ${all.length} entries (${Math.round(size / 1024)} KB); ${kept.length} are kept here: the first twelve and the entries where the scene numbering differs from TVDB's, with the entry before and after each stretch.`, [mapped]);

for (const [state, id, note] of [
  ['map-all-unknown-show', 81189, 'GET /map/all for a TVDB id TheXEM has no show for: HTTP 200 with result failure and an empty list.'],
  ['map-all-id-zero', 0, 'GET /map/all with id 0: the same failure answer.']
]) {
  save('thexem', day, state, note, [await x.get(`/map/all?id=${id}&origin=tvdb&output=json`)]);
}
console.log('done');

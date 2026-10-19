// Records Gestdown's public subtitle API, the two calls GestdownSubtitleProvider makes for the "Test" button of Settings, Subtitle providers
// (a show search, then the subtitles of one episode in one language), tests/fixtures/recorded/gestdown/<date>/*.json (#1237).
//
//   node scripts/recorded-replies/docker/gestdown.mjs
//
// Gestdown needs no account: these are real unauthenticated GETs from the public internet with the user agent `Deluno-test-recording`.
// It has no version of its API, so the recordings sit under the date of the recording.
import { Client, replyJson, save } from './lib.mjs';

const g = new Client('https://api.gestdown.info', { headers: { 'user-agent': 'Deluno-test-recording', accept: 'application/json' } });
const day = new Date().toISOString().slice(0, 10);

const search = await g.get('/shows/search/Breaking%20Bad');
const showId = replyJson(search).shows[0].id;
const subtitles = await g.get(`/subtitles/get/${showId}/1/1/en`);
const kept = replyJson(subtitles);
const all = (kept.matchingSubtitles ?? kept.subtitles ?? []).length;
save('gestdown', day, 'breaking-bad-s01e01-english', `GET /shows/search/Breaking Bad, then GET /subtitles/get/{show id}/1/1/en: the show Gestdown holds and the English subtitles of its first episode (${all} found).`, [search, subtitles]);

const nothing = await g.get('/shows/search/Zzzz%20Not%20A%20Show%20Zzzz');
save('gestdown', day, 'show-not-found', 'GET /shows/search for a title Gestdown does not hold: 404 with a plain sentence naming the title.', [nothing]);
console.log('done');

// Records the keyless subtitle providers' real replies for the rows of #1301: Gestdown's download, Subf2m's pages and download, and the part of
// SuperSubtitles (feliratok.eu) that answers an unauthenticated caller, tests/fixtures/recorded/<app>/<date>/*.json.
//
//   node scripts/recorded-replies/docker/keyless-subtitles.mjs
//
// No account and no key: real unauthenticated GETs from the public internet with the user agent `Deluno-test-recording`. A reply that is not
// text (a zip) is kept as a file beside the recording (bodyFile, bodyBytes, bodySha256). Podnapisi cannot be recorded from here: its API
// host (www.podnapisi.net) does not resolve. SuperSubtitles' season listing and download answer a Cloudflare challenge page, which is recorded as it is.
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { Client, recordedRoot, replyJson, save } from './lib.mjs';

const day = new Date().toISOString().slice(0, 10);
const agent = { 'user-agent': 'Deluno-test-recording' };
const keep = ['content-type', 'content-disposition', 'location', 'retry-after'];

/** A GET whose reply is kept as a file beside the recording when it is binary. */
async function getBinary(base, path, app, name) {
  const response = await fetch(base + path, { headers: agent, redirect: 'manual' });
  const bytes = new Uint8Array(await response.arrayBuffer());
  const headers = {};
  for (const h of keep) { const v = response.headers.get(h); if (v !== null) headers[h] = v; }
  const file = `${name}.zip`;
  const dir = join(recordedRoot, app, day);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, file), bytes);
  return { request: { method: 'GET', path }, reply: { status: response.status, headers, bodyFile: file, bodyBytes: bytes.length, bodySha256: createHash('sha256').update(bytes).digest('hex') } };
}

// ---- Gestdown: the download of a subtitle its listing named ----
const gestdown = new Client('https://api.gestdown.info', { headers: { ...agent, accept: 'application/json' }, keep });
const shows = await gestdown.get('/shows/search/Breaking%20Bad');
const showId = replyJson(shows).shows[0].id;
const list = await gestdown.get(`/subtitles/get/${showId}/1/1/en`);
const first = (replyJson(list).matchingSubtitles ?? [])[0];
const download = await gestdown.get(first.downloadUri);
const unknown = await gestdown.get('/subtitles/download/00000000000000000000');
save('gestdown', day, 'download', `GET {downloadUri} of the first subtitle the English listing of Breaking Bad S01E01 named: the file itself (SubRip text), and the same call for an id Gestdown does not hold.`, [list, download, unknown]);

// ---- Subf2m ----
const subf2m = new Client('https://subf2m.co', { headers: agent, keep });
const search = await subf2m.get('/subtitles/searchbytitle?query=Sintel&l=');
const title = await subf2m.get('/subtitles/sintel');
const language = await subf2m.get('/subtitles/sintel/english');
const links = [...language.reply.bodyText.matchAll(/href=['"](\/subtitles\/sintel\/english\/\d+)['"]/g)].map((m) => m[1]);
const page = await subf2m.get(links[0]);
const downloadPath = (page.reply.bodyText.match(/href=['"]([^'"]*\/download)['"]/) ?? [])[1] ?? `${links[0]}/download`;
const redirect = await subf2m.get(downloadPath);
// The download answers a 302 to a CDN address; the archive is that address's reply (kept as a file, its path in the recording is host + path).
const cdn = new URL(redirect.reply.headers.location);
const file = await getBinary(cdn.origin, cdn.pathname + cdn.search, 'subf2m', 'download-file');
file.request.path = cdn.host + cdn.pathname;
save('subf2m', day, 'sintel-english', `The pages Subf2m answers for Sintel: the title search, the title page, the English list, one subtitle's page and its download (${downloadPath}). The links to the subtitles are /subtitles/<title>/<language>/<id>.`, [search, title, language, page, redirect, file]);
const gone = await subf2m.get('/subtitles/sintel/english/99999999999/download');
save('subf2m', day, 'download-not-found', 'The download of a subtitle number Subf2m does not hold: 404.', [gone]);
const none = await subf2m.get('/subtitles/searchbytitle?query=Zzzz-Not-A-Title-Zzzz&l=');
save('subf2m', day, 'search-no-title', 'The title search for a title Subf2m does not hold.', [none]);

// ---- SuperSubtitles (feliratok.eu) ----
const felirat = new Client('https://feliratok.eu', { headers: { ...agent, accept: 'application/json' }, keep });
const names = await felirat.get('/index.php?term=Breaking%20Bad&nyelv=0&action=autoname');
const seasons = await felirat.get('/index.php?action=xbmc&sid=252&ev=1&rtol=1');
const noName = await felirat.get('/index.php?term=Zzzz%20Not%20A%20Show%20Zzzz&nyelv=0&action=autoname');
save('supersubtitles', day, 'show-search', 'The show-name search (autoname), the season listing (action=xbmc) and a search for a show the site does not hold.', [names, seasons, noName]);
console.log('done');

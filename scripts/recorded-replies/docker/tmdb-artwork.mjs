// Records the artwork the project's deployed metadata gateway serves (#1296): the poster and the backdrop of the film its search recordings
// name (Sintel, TMDb 45745), and the answer for an artwork it does not hold. The image is kept as a file beside the recording (bodyFile,
// bodyBytes, bodySha256), tests/fixtures/recorded/tmdb/<date>/artwork-*.json.
//
//   node scripts/recorded-replies/docker/tmdb-artwork.mjs
//
// Real unauthenticated GETs from the public internet with the user agent `Deluno-test-recording`; no account, no key. The small sizes are
// recorded (w185 and w342) so the repository keeps two small images, not the full-size ones a search result points at.
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { recordedRoot, save } from './lib.mjs';

const GATEWAY = process.env.DELUNO_GATEWAY ?? 'https://deluno-metadata-gateway.ejmdigital.workers.dev';
const day = new Date().toISOString().slice(0, 10);
const agent = { 'user-agent': 'Deluno-test-recording' };
const keep = ['content-type', 'cache-control'];

async function getFile(path, name) {
  const response = await fetch(GATEWAY + path, { headers: agent, redirect: 'manual' });
  const bytes = new Uint8Array(await response.arrayBuffer());
  const headers = {};
  for (const h of keep) { const v = response.headers.get(h); if (v !== null) headers[h] = v; }
  if (!response.ok) return { request: { method: 'GET', path }, reply: { status: response.status, headers, bodyText: new TextDecoder().decode(bytes) } };
  const dir = join(recordedRoot, 'tmdb', day);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, name), bytes);
  return { request: { method: 'GET', path }, reply: { status: response.status, headers, bodyFile: name, bodyBytes: bytes.length, bodySha256: createHash('sha256').update(bytes).digest('hex') } };
}

const poster = await getFile('/artwork/w185/l98Zns4oQe1BSvbF4hJ8oL6VSf0.jpg', 'artwork-poster.jpg');
save('tmdb', day, 'artwork-poster', 'GET /artwork/w185/{file} on the gateway: the poster of Sintel (the file the search for Sintel names, in the small size), an image/jpeg.', [poster]);
const backdrop = await getFile('/artwork/w342/msqeiEyIRpPAtrCeRGFNZQ9tkJL.jpg', 'artwork-backdrop.jpg');
save('tmdb', day, 'artwork-backdrop', 'GET /artwork/w342/{file} on the gateway: the backdrop of Sintel (in a small size), an image/jpeg.', [backdrop]);
const badSize = await getFile('/artwork/w300/msqeiEyIRpPAtrCeRGFNZQ9tkJL.jpg', 'unused');
save('tmdb', day, 'artwork-size-not-served', 'GET /artwork/w300/{file} on the gateway: a size it does not serve answers 404 not_found.', [badSize]);
const gone = await getFile('/artwork/w185/zzzzzzzzzzzzzzzzzzzzzzzzzzz.jpg', 'unused');
save('tmdb', day, 'artwork-not-found', 'GET /artwork/w185/{file} for a file TMDb does not hold: the gateway answers it as it is.', [gone]);

// Makes releases the e2e fixture does not make itself (#1237): two different single files with the SAME file name, a season pack of episodes,
// a pack with one episode that cannot be read, an audio-only film. The files are made inside the fixture's container (it has ffmpeg and the
// suite's base film), under /work/films, where its web seed serves them; the .torrent goes to /work/torrents, where /dl/ serves it.
// A client given the torrent downloads every byte of it from the fixture, exactly as it does for the fixture's own releases.
import { execFileSync } from 'node:child_process';

const project = process.env.REC_PROJECT ?? 'recdocker';
const shell = process.env.REC_DOCKER ?? 'wsl -e docker';
const PUBLIC_BASE = process.env.REC_FIXTURE_BASE ?? 'http://fixture:8765';

// The script that runs inside the container (its spec is put in where __SPEC__ stands) and prints the torrent's facts as JSON.
const inside = String.raw`
const { execFileSync } = require('node:child_process');
const { createHash, randomBytes } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const spec = __SPEC__;
const WORK = '/work';
const PIECE = 256 * 1024;
const sha1 = (b) => createHash('sha1').update(b).digest();
function bencode(v) {
  if (Buffer.isBuffer(v)) return Buffer.concat([Buffer.from(v.length + ':'), v]);
  if (typeof v === 'number') return Buffer.from('i' + v + 'e');
  if (typeof v === 'string') return bencode(Buffer.from(v, 'utf8'));
  if (Array.isArray(v)) return Buffer.concat([Buffer.from('l'), ...v.map(bencode), Buffer.from('e')]);
  const keys = Object.keys(v).map((k) => Buffer.from(k, 'utf8')).sort(Buffer.compare);
  return Buffer.concat([Buffer.from('d'), ...keys.flatMap((k) => [bencode(k), bencode(v[k.toString('utf8')])]), Buffer.from('e')]);
}
const ff = (args) => execFileSync('ffmpeg', ['-y', '-v', 'error', ...args]);
function make(kind, out, label) {
  fs.mkdirSync(path.dirname(out), { recursive: true });
  if (kind === 'film') ff(['-i', WORK + '/base/film.mkv', '-c', 'copy', '-metadata', 'title=' + label, out]);
  else if (kind === 'garbage') fs.writeFileSync(out, randomBytes(60 * 1024 * 1024));
  else if (kind === 'audio') ff(['-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=320', '-ac', '2', '-c:a', 'pcm_s16le', '-metadata', 'title=' + label, out]);
  else if (kind === 'extra') fs.copyFileSync(WORK + '/base/extra.mkv', out);
  else throw new Error('unknown kind ' + kind);
}
// The base film is made the first time the fixture makes a release; ask for it if it is not there yet.
if (!fs.existsSync(WORK + '/base/film.mkv')) throw new Error('the fixture has not made its base film yet; make any release first');
const root = path.join(WORK, 'films', spec.folder);
fs.rmSync(root, { recursive: true, force: true });
const made = [];
for (const f of spec.files) { make(f.kind, path.join(root, f.path), f.label || f.path); made.push(f.path); }
let info;
if (spec.single) {
  const f = spec.files[0];
  const data = fs.readFileSync(path.join(root, f.path));
  const pieces = []; for (let at = 0; at < data.length; at += PIECE) pieces.push(sha1(data.subarray(at, at + PIECE)));
  info = { name: f.path, 'piece length': PIECE, pieces: Buffer.concat(pieces), length: data.length };
} else {
  const entries = []; const pieces = []; let buf = Buffer.alloc(0);
  const sorted = [...made].sort((a, b) => Buffer.compare(Buffer.from(a), Buffer.from(b)));
  for (const rel of sorted) {
    const data = fs.readFileSync(path.join(root, rel));
    entries.push({ length: data.length, path: rel.split('/') });
    buf = buf.length ? Buffer.concat([buf, data]) : data;
    while (buf.length >= PIECE) { pieces.push(sha1(buf.subarray(0, PIECE))); buf = buf.subarray(PIECE); }
  }
  if (buf.length) pieces.push(sha1(buf));
  info = { name: spec.folder, 'piece length': PIECE, pieces: Buffer.concat(pieces), files: entries };
}
const base = spec.single ? spec.base + '/webseed/' + spec.folder + '/' : spec.base + '/webseed/';
// A multi-file release is served from /work/films/<name>; a single file from /work/films/<folder>/<file>.
const torrent = bencode({ info, 'url-list': [base], comment: 'Deluno end-to-end fixture' });
fs.mkdirSync(WORK + '/torrents', { recursive: true });
fs.writeFileSync(path.join(WORK, 'torrents', spec.torrent), torrent);
const total = spec.single ? info.length : info.files.reduce((n, f) => n + f.length, 0);
console.log(JSON.stringify({ torrent: spec.torrent, infohash: sha1(bencode(info)).toString('hex'), size: total, files: spec.single ? [{ path: info.name, size: info.length }] : info.files.map((f) => ({ path: f.path.join('/'), size: f.length })) }));
`;

/**
 * spec: { folder, torrent, single, files: [{ path, kind: 'film'|'garbage'|'audio'|'extra', label? }] }
 * single: one file named files[0].path, kept in its own folder under the web seed (so two single files can share one name).
 */
export function makeRelease(spec) {
  const parts = shell.split(' ').filter(Boolean);
  const script = inside.replace('__SPEC__', JSON.stringify({ ...spec, base: PUBLIC_BASE }));
  const out = execFileSync(parts[0], [...parts.slice(1), 'exec', '-i', `${project}-fixture-1`, 'node', '-'], { input: script, encoding: 'utf8', timeout: 600_000, maxBuffer: 64 * 1024 * 1024 });
  const made = JSON.parse(out.trim().split('\n').pop());
  return { ...made, url: `${PUBLIC_BASE}/dl/${encodeURIComponent(spec.torrent)}`.replace(/%20/g, '%20') };
}

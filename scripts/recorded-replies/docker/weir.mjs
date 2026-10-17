// Records the Weir states the golden VM could not produce (tests/fixtures/recorded/MISSING.md), from Weir's published image in Docker.
//
//   node --experimental-transform-types --no-warnings scripts/recorded-replies/docker/weir.mjs <step>...
//   steps: openapi  scheduled  forbidden  not-taken
//
// Needs the e2e stack up under a project name of your own (tests/e2e/compose.yaml) and set up once (the recorder signs in to Weir with the
// stack's throwaway login and reads the webhook secret the stack's setup saved in artifacts/e2e/weir-secret.txt). Hand-offs name copies
// of the fixture's real film, put in Weir's watched folder with `docker exec`; every setting a step changes is put back, and the files
// it makes are removed.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { WeirApi } from '../../../tests/e2e/stack.ts';
import { Client, addMissing, addSecret, local, makeFilm, replyJson, save, sleep, until } from './lib.mjs';

const project = process.env.REC_PROJECT ?? 'recdocker';
const docker = (...args) => { const parts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean); return execFileSync(parts[0], [...parts.slice(1), ...args], { encoding: 'utf8', timeout: 120_000 }); };
const weirContainer = `${project}-weir-1`;
const WATCHED = '/downloads/completed/Movies';
const SOURCE = 'deluno';

const secret = fs.readFileSync(path.resolve(process.cwd(), 'artifacts/e2e/weir-secret.txt'), 'utf8').trim();
addSecret(secret);
const weirAdmin = await WeirApi.signIn();
const intake = new Client(local('weir'));
const headers = { 'X-Webhook-Secret': secret };

const status = (id, h = headers, source = SOURCE) => intake.get(`/api/v1/intake/handoffs/${source}/${id}`, { headers: h });
const outcome = (id, body) => intake.send({ method: 'POST', path: `/api/v1/intake/handoffs/${SOURCE}/${id}/outcome`, headers, json: body });
const cancel = (id) => intake.send({ method: 'DELETE', path: `/api/v1/intake/handoffs/${SOURCE}/${id}`, headers });
const submit = (payload, h = headers, source = SOURCE) => intake.send({ method: 'POST', path: `/api/v1/intake/webhook/${source}`, headers: h, json: payload });

const caps = replyJson(await intake.get('/api/v1/intake/capabilities', { headers }));
const VERSION = caps.version;
const folders = replyJson(await intake.get('/api/v1/intake/library-folders', { headers }));
const movie = folders.libraries.find((l) => l.media_type === 'movie');
const libraryId = movie.manager_library_key ? String(movie.manager_library_key) : 'recorded-library';

// A copy of the fixture's real film in the watched folder, where a download client would have left it.
async function baseFilm() {
  try { docker('exec', weirContainer, 'test', '-f', '/tmp/recfilm.mkv'); } catch {
    // The fixture makes its real base film (ffmpeg's test pattern and a tone, 64 MB) the first time it makes a release.
    await makeFilm({ title: 'Recorded Base' });
    execFileSync(process.env.REC_SHELL ?? 'wsl', ['-e', 'bash', '-c', `docker cp ${project}-fixture-1:/work/base/film.mkv /tmp/recfilm.mkv && docker cp /tmp/recfilm.mkv ${weirContainer}:/tmp/recfilm.mkv`]);
  }
}
await baseFilm();
const made = [];
// With `settled`, the copy looks ten minutes old, so Weir does not hold it back for "changed too recently" before it works on it.
function place(name, { settled = false } = {}) {
  const file = `${WATCHED}/${name}`;
  docker('exec', '--user', 'root', weirContainer, 'sh', '-c', `cp /tmp/recfilm.mkv '${file}' && chown app:app '${file}'${settled ? ` && touch -d '10 minutes ago' '${file}'` : ''}`);
  made.push(file);
  return file;
}
function payload(file, title, id = randomUUID()) {
  return {
    eventType: 'deluno.processor-handoff', handoffId: id, libraryId, mediaType: 'movie', sourcePath: file, releaseName: title,
    queueItemId: randomUUID(), callbackPath: '/api/integrations/processors/events', title, year: 2008
  };
}
const stateOf = (e) => replyJson(e)?.state ?? `http-${e.reply.status}`;
async function cleanUp() {
  for (const file of made) { try { docker('exec', '--user', 'root', weirContainer, 'rm', '-f', file); } catch { /* gone */ } }
  try { docker('exec', '--user', 'root', weirContainer, 'sh', '-c', 'rm -f /weir/Ready/Movies/Recorded.Weir.*'); } catch { /* none */ }
}

const WRITABLE = ['name', 'media_type', 'enabled', 'watched_folder', 'work_folder', 'output_folder', 'media_extensions_csv', 'exclude_markers_csv', 'include_patterns_csv', 'exclude_patterns_csv', 'min_file_size_mb', 'max_file_size_mb', 'rejected_file_action', 'ready_after_seconds', 'created_after', 'created_before', 'modified_after', 'modified_before', 'exclude_hidden', 'top_level_only', 'scan_interval_seconds', 'sidecar_patterns_csv', 'preserve_original_timestamps', 'remove_original_after_success', 'minimum_free_disk_space_mb', 'remux_writer', 'output_collision_policy', 'ffmpeg_strictness', 'ignore_size_changes', 'skip_access_tests', 'file_system_events_enabled', 'max_attempts', 'retry_backoff_seconds', 'retry_execution_failures', 'retry_preflight_failures', 'failure_policy', 'schedule_grid', 'schedule_enabled', 'schedule_hours_limited', 'schedule_days', 'schedule_start', 'schedule_end', 'max_concurrent_files', 'priority', 'rule_set_id', 'manager_connection_ids'];
async function movieLibrary() { return (await weirAdmin.get('/api/v1/processing/libraries')).body.find((l) => l.media_type === 'movie'); }
async function setLibrary(library, changes) {
  const body = {};
  for (const f of WRITABLE) body[f] = library[f];
  Object.assign(body, changes);
  const saved = await weirAdmin.put(`/api/v1/processing/libraries/${library.id}`, body);
  if (saved.status >= 300) throw new Error(`Weir would not save its library: ${saved.status} ${saved.text.slice(0, 400)}`);
}

// ---------------------------------------------------------------------------------------------------------------- the steps

const steps = {
  // Weir's description of its API. It is 360 KB, so what is kept is the part Deluno reads (the version) and the identity of the document.
  async openapi() {
    const e = await intake.get('/openapi.json');
    const full = replyJson(e);
    const kept = { openapi: full.openapi, info: full.info, servers: full.servers };
    const size = e.reply.bodyText.length;
    e.reply.bodyText = JSON.stringify(kept);
    save('weir', VERSION, 'openapi', `GET /openapi.json with no secret: Weir's description of its API. The reply was ${Math.round(size / 1024)} KB; only "openapi", "info" and "servers" are kept here, the paths and components are left out.`, [e]);
  },

  // "scheduled": Weir will look at the file again later. A film that stops before its end (as a download still in progress does) cannot be read to the
  // end, so Weir puts the file on hold and waits. A second hand-off for the same file (the same release sent again) is then not worked at once: it is
  // scheduled for the time the hold ends, and the status says so, with Weir's words for why.
  async scheduled() {
    try {
      const name = `Recorded.Weir.CutOff.${randomUUID().slice(0, 6)}.mkv`;
      const file = `${WATCHED}/${name}`;
      docker('exec', '--user', 'root', weirContainer, 'sh', '-c', `head -c 62000000 /tmp/recfilm.mkv > '${file}' && chown app:app '${file}' && touch -d '10 minutes ago' '${file}'`);
      made.push(file);
      const idFirst = randomUUID();
      const first = await submit(payload(file, 'Big Buck Bunny (2008) Recorded Cut Off', idFirst));
      await until('Weir to put the cut-off file on hold', 120, async () => ((await weirAdmin.get('/api/v1/processing/files')).body.files ?? []).find((f) => f.relative_path === name && f.status === 'on_hold'), 1000);
      const id = randomUUID();
      const accepted = await submit(payload(file, 'Big Buck Bunny (2008) Recorded Cut Off', id));
      const seen = new Map();
      const end = Date.now() + 120_000;
      while (Date.now() < end) {
        const s = await status(id);
        const st = stateOf(s);
        if (!seen.has(st)) seen.set(st, s);
        if (st === 'scheduled' || st === 'failed' || st === 'completed') break;
        await sleep(500);
      }
      if (!seen.has('scheduled')) { addMissing(`weir scheduled: the second hand-off for a cut-off file on hold was ${[...seen.keys()].join(', ')}, never scheduled`); return; }
      save('weir', VERSION, 'handoff-accepted-cut-off-file', 'A hand-off for a film that stops before its end (the last 5 MB are missing, as in a download still in progress): Weir accepts it, and puts the file on hold when it cannot read it to the end.', [first]);
      save('weir', VERSION, 'handoff-accepted-again-while-on-hold', 'A second hand-off for the same cut-off file, sent while Weir holds the file: Weir accepts it.', [accepted]);
      save('weir', VERSION, 'status-scheduled-file-on-hold', 'The status poll of a hand-off for a file Weir holds because it could not read it to the end: state scheduled, with the time the hold ends and the words Weir gives for why.', [seen.get('scheduled')]);
      await cancel(id);
      await cancel(idFirst);
    } finally {
      await cleanUp();
    }
  },

  // The 403 Weir gives on a status poll when no connection of Deluno's source has a webhook secret. It takes Deluno's connection away, so it is the LAST
  // step of a run: the stack's Weir cannot hand off for Deluno afterwards (make the stack again for another run).
  async forbidden() {
    const managers = (await weirAdmin.get('/api/v1/media-managers/connections')).body.filter((m) => m.kind === 'deluno');
    for (const m of managers) {
      const gone = await weirAdmin.send('DELETE', `/api/v1/media-managers/connections/${m.id}`);
      if (gone.status >= 300) { addMissing(`weir 403: Weir would not remove Deluno's connection (${gone.status} ${gone.text.slice(0, 200)})`); return; }
    }
    const withOldSecret = await status(randomUUID());
    const withNoSecret = await status(randomUUID(), {});
    const handoff = await submit(payload(`${WATCHED}/Recorded.Weir.Forbidden.mkv`, 'Recorded forbidden'));
    save('weir', VERSION, 'status-deluno-source-no-secret', "The status poll on Deluno's own source (deluno) once no connection of that source has a webhook secret set (Deluno's connection was removed in Weir): 403 with the words Weir gives, with the old secret and with none.", [withOldSecret, withNoSecret]);
    save('weir', VERSION, 'handoff-refused-deluno-source-no-secret', 'A hand-off sent to the Deluno source once no connection of it has a webhook secret set.', [handoff]);
  }
};

for (const name of process.argv.slice(2)) {
  if (!steps[name]) { console.error('unknown step: ' + name); process.exit(2); }
  await steps[name]();
}
await weirAdmin.dispose();
console.log('done');
process.exit(0);

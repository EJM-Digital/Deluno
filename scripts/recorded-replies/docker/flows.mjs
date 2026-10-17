// Records what the REAL Deluno says to Weir and what Weir says back, from the e2e stack in Docker (#1237, #1239).
//
//   node --experimental-transform-types --no-warnings scripts/recorded-replies/docker/flows.mjs <step>...
//   steps: not-taken  handoff-bodies
//
// Deluno and Weir are the published images. A recording proxy (proxy.mjs, in a container named `proxy` on the stack's network) sits in both
// directions: Deluno's processor connection and Weir's media-manager connection are pointed at it, so every hand-off, status poll, cancel and
// outcome Deluno sends, and every report Weir sends back, is kept exactly as it went. The hand-offs are made the way a person's are: a release
// is sent to qBittorrent through Deluno, the download finishes, Deluno hands the file to Weir.
//
// Deluno's side goes to tests/fixtures/recorded/deluno/<version>/ (and tests/fixtures/recorded/MANIFEST.sha256 there is rewritten on purpose by
// the commit that adds the files); Weir's own replies go beside the existing ones under weir/<version>/.
import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { setUpStack, WeirApi, URLS, until } from '../../../tests/e2e/stack.ts';
import { sendFilm, handoffOf } from '../../../tests/e2e/flow.ts';
import { routeLibraryTo, clientFinished } from '../../../tests/e2e/stack.ts';
import { execFileSync } from 'node:child_process';
import { makeRelease } from './releases.mjs';
import { addMissing, addSecret, replyJson, save, sleep } from './lib.mjs';

const proxyControl = process.env.REC_PROXY_CONTROL ?? 'http://127.0.0.1:39350';
const DELUNO_VERSION = (fs.readFileSync(path.resolve(process.cwd(), 'Directory.Build.props'), 'utf8').match(/<Version>([^<]+)<\/Version>/) ?? [])[1];
const WEIR_URL = URLS.weir;
const PROXY_WEIR = 'http://proxy:9348';
const PROXY_DELUNO = 'http://proxy:9349';

const stack = await setUpStack();
const secret1 = stack.weirSecret;
addSecret(secret1);
const caps = await (await fetch(`${WEIR_URL}/api/v1/intake/capabilities`, { headers: { 'X-Webhook-Secret': secret1 } })).json();
const WEIR_VERSION = caps.version;

// ------------------------------------------------------------------ the proxy's log, as recordings

const proxyLog = async () => await (await fetch(`${proxyControl}/_log`)).json();
const proxyClear = async () => { await fetch(`${proxyControl}/_log`, { method: 'DELETE' }); };
const proxyFault = async (fault) => { await fetch(`${proxyControl}/_fault`, { method: 'POST', body: JSON.stringify(fault) }); };

/** One logged call as an exchange of the recording format (the secret header is scrubbed by its name). */
function exchangeOf(row) {
  const request = { method: row.method, path: row.path };
  if (Object.keys(row.requestHeaders).length) request.headers = row.requestHeaders;
  if (row.requestBody) request.bodyText = row.requestBody;
  const reply = { status: row.reply.status, headers: row.reply.contentType ? { 'content-type': row.reply.contentType } : {}, bodyText: row.reply.bodyText };
  return { request, reply };
}

async function waitForRow(what, seconds, predicate) {
  return (await until(what, seconds, async () => (await proxyLog()).find(predicate), 1000)).value;
}

// ------------------------------------------------------------------ wiring the stack through the proxy

async function rerouteThroughProxy() {
  const processors = await stack.deluno.must('GET', '/api/integrations/processors/connections');
  const p = processors.find((x) => x.id === stack.processorId);
  if (!p.submissionUrl.startsWith(PROXY_WEIR)) {
    await stack.deluno.must('PUT', `/api/integrations/processors/connections/${p.id}`, {
      name: p.name, provider: p.provider, submissionUrl: p.submissionUrl.replace('http://weir:9347', PROXY_WEIR),
      authHeaderName: p.authHeaderName, secret: secret1, isEnabled: true, healthCheckUrl: `${PROXY_WEIR}/ready`
    });
  }
  const managers = await stack.weir.get('/api/v1/media-managers/connections');
  const m = managers.body.find((x) => x.kind === 'deluno' && x.webhook_secret_is_set);
  if (m.base_url !== PROXY_DELUNO) {
    const done = await stack.weir.put(`/api/v1/media-managers/connections/${m.id}`, { base_url: PROXY_DELUNO });
    if (done.status >= 300) throw new Error(`Weir would not take the proxy as Deluno's address: ${done.status} ${done.text.slice(0, 300)}`);
  }
}

const WRITABLE = ['name', 'media_type', 'enabled', 'watched_folder', 'work_folder', 'output_folder', 'media_extensions_csv', 'exclude_markers_csv', 'include_patterns_csv', 'exclude_patterns_csv', 'min_file_size_mb', 'max_file_size_mb', 'rejected_file_action', 'ready_after_seconds', 'created_after', 'created_before', 'modified_after', 'modified_before', 'exclude_hidden', 'top_level_only', 'scan_interval_seconds', 'sidecar_patterns_csv', 'preserve_original_timestamps', 'remove_original_after_success', 'minimum_free_disk_space_mb', 'remux_writer', 'output_collision_policy', 'ffmpeg_strictness', 'ignore_size_changes', 'skip_access_tests', 'file_system_events_enabled', 'max_attempts', 'retry_backoff_seconds', 'retry_execution_failures', 'retry_preflight_failures', 'failure_policy', 'schedule_grid', 'schedule_enabled', 'schedule_hours_limited', 'schedule_days', 'schedule_start', 'schedule_end', 'max_concurrent_files', 'priority', 'rule_set_id', 'manager_connection_ids'];
const weirLibrary = async (type = 'movie') => (await stack.weir.get('/api/v1/processing/libraries')).body.find((l) => l.media_type === type);
async function setWeirLibrary(library, changes) {
  const body = {};
  for (const f of WRITABLE) body[f] = library[f];
  Object.assign(body, changes);
  const saved = await stack.weir.put(`/api/v1/processing/libraries/${library.id}`, body);
  if (saved.status >= 300) throw new Error(`Weir would not save its library: ${saved.status} ${saved.text.slice(0, 400)}`);
}
const setAutomation = (on) => stack.deluno.must('PUT', '/api/settings/automation', { isEnabled: on });

const project = process.env.REC_PROJECT ?? 'recdocker';
const dockerCmd = (...args) => { const parts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean); return execFileSync(parts[0], [...parts.slice(1), ...args], { encoding: 'utf8', timeout: 120_000 }); };
const weirExec = (script) => dockerCmd('exec', '--user', 'root', `${project}-weir-1`, 'sh', '-c', script);
const weirStatus = async (id, secret = secret1) => (await (await fetch(`${WEIR_URL}/api/v1/intake/handoffs/deluno/${id}`, { headers: { 'X-Webhook-Secret': secret } })).json());

/** Sends a release to qBittorrent through Deluno as "Send anyway" does, and returns what Deluno made of it. */
async function sendRelease(release, { title, year = 2020, kind = 'movie', libraryId }) {
  const route = kind === 'movie' ? 'movies' : 'series';
  const item = await stack.deluno.must('POST', `/api/${route}`, kind === 'movie'
    ? { title, releaseYear: year, imdbId: null, monitored: true }
    : { title, startYear: year, imdbId: null, monitored: true });
  await routeLibraryTo(stack, 'qbittorrent');
  const query = kind === 'tv' && libraryId ? `?libraryId=${libraryId}` : '';
  const grabbed = await stack.deluno.call('POST', `/api/${route}/${item.id}/grab${query}`, {
    releaseName: release.name, indexerId: stack.indexerId, indexerName: 'E2E feed', candidateQuality: 'WEB 1080p',
    downloadUrl: release.url, sizeBytes: release.size, seeders: 50, force: true, overrideReason: 'recording', duplicateAction: null
  });
  if (grabbed.status >= 300) throw new Error(`Deluno would not take ${release.name}: ${grabbed.status} ${grabbed.text.slice(0, 300)}`);
  return item;
}

/** Deluno's hand-off to Weir for a release (the POST with its reply), once it appears in the proxy's log. */
async function delunoHandoffFor(name, seconds = 240) {
  const row = await waitForRow(`Deluno to hand ${name} to Weir`, seconds, (r) => r.listener === 9348 && r.method === 'POST' && r.path.includes('/intake/webhook/') && r.requestBody.includes(name));
  return row;
}

/** The TV library the way a person sets it up: a quality profile, the library with its refine-before-import workflow, qBittorrent's TV category, Weir's TV workflow linked to Deluno. */
async function ensureTv() {
  const profiles = await stack.deluno.must('GET', '/api/quality-profiles');
  const profile = profiles.find((p) => p.mediaType === 'tv') ?? await stack.deluno.must('POST', '/api/quality-profiles', {
    name: 'Balanced 1080p TV - recording', mediaType: 'tv', cutoffQuality: 'WEB 1080p', allowedQualities: 'WEB 720p,Bluray 720p,WEB 1080p,Bluray 1080p', customFormatIds: '', upgradeUntilCutoff: true, upgradeUnknownItems: false
  });
  const libraries = await stack.deluno.must('GET', '/api/libraries');
  const library = libraries.find((l) => l.mediaType === 'tv') ?? await stack.deluno.must('POST', '/api/libraries', {
    name: 'TV', mediaType: 'tv', purpose: 'Main library', rootPath: '/media/TV', downloadsPath: '/downloads/completed/TV', qualityProfileId: profile.id,
    autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false, removeEmptySourceFolders: false, searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10
  });
  const processors = await stack.deluno.must('GET', '/api/integrations/processors/connections');
  const processor = processors.find((x) => x.id === stack.processorId);
  await stack.deluno.must('PUT', `/api/libraries/${library.id}/workflow`, {
    importWorkflow: 'refine-before-import', processorConnectionId: processor.id, processorName: processor.name, processorOutputPath: '/weir/Ready/TV',
    processorTimeoutMinutes: 2880, processorFailureMode: 'block', cleanupMode: 'keep-source', removeEmptySourceFolders: false
  });
  await stack.deluno.must('PUT', `/api/libraries/${library.id}/routing`, {
    sources: [{ indexerId: stack.indexerId, priority: 1, requiredTags: '', excludedTags: '' }], downloadClients: [{ downloadClientId: stack.clientIds.qbittorrent, priority: 1 }]
  });
  await stack.qbittorrent.post('/api/v2/torrents/createCategory', { category: 'deluno-tv', savePath: '/downloads/completed/TV' });
  const weirTv = await weirLibrary('tv');
  const managers = (await stack.weir.get('/api/v1/media-managers/connections')).body;
  const manager = managers.find((m) => m.kind === 'deluno' && m.webhook_secret_is_set);
  await setWeirLibrary(weirTv, { watched_folder: '/downloads/completed/TV', output_folder: '/weir/Ready/TV', manager_connection_ids: [...new Set([...(weirTv.manager_connection_ids ?? []), manager.id])] });
  await until('Weir to watch the TV folder', 120, async () => (await weirLibrary('tv')).enabled, 2000);
  return library;
}

const steps = {
  // Two different single files with one name, one after the other, the first still in Weir's output folder: the collision policy acts (#1262).
  // The first is handed to Weir straight (the way a media manager's own send would be); the second is Deluno's own send, from a download.
  async collisions() {
    await rerouteThroughProxy();
    for (const policy of ['replace', 'keep_both']) {
      const tag = policy.replace('_', '-');
      const library = await weirLibrary();
      try {
        await setWeirLibrary(library, { output_collision_policy: policy });
        const fileName = `Recorded.Collide.${tag}.2020.1080p.WEB-DL.x264-E2E.mkv`;
        const src = `/downloads/completed/Movies/${fileName}`;
        weirExec(`cp /tmp/recfilm.mkv '${src}' && chown app:app '${src}' && touch -d '10 minutes ago' '${src}'`);
        const idA = randomUUID();
        const key = String((await (await fetch(`${WEIR_URL}/api/v1/intake/library-folders`, { headers: { 'X-Webhook-Secret': secret1 } })).json()).libraries.find((l) => l.media_type === 'movie').manager_library_key ?? 'recorded-library');
        const first = await fetch(`${WEIR_URL}/api/v1/intake/webhook/deluno`, { method: 'POST', headers: { 'X-Webhook-Secret': secret1, 'content-type': 'application/json' }, body: JSON.stringify({
          eventType: 'deluno.processor-handoff', handoffId: idA, libraryId: key, mediaType: 'movie', sourcePath: src, releaseName: fileName.replace(/\.mkv$/, ''), queueItemId: randomUUID(),
          callbackPath: '/api/integrations/processors/events', title: `Recorded Collide ${tag} first`, year: 2020 }) });
        if (!first.ok) throw new Error(`the first hand-off was refused: ${first.status}`);
        await until('Weir to finish the first file', 180, async () => (await weirStatus(idA)).state === 'completed' ? true : null, 1000);
        // Weir keeps the original for the client; take it away so the second download writes the name afresh.
        weirExec(`rm -f '${src}'`);
        const release = makeRelease({ folder: `collB-${tag}`, torrent: `Recorded.Collide.${tag}.torrent`, single: true, files: [{ path: fileName, kind: 'film', label: `collide ${tag} second` }] });
        release.name = fileName.replace(/\.mkv$/, '');
        await proxyClear();
        await sendRelease(release, { title: `Recorded Collide ${tag} second` });
        const row = await delunoHandoffFor(fileName);
        const idB = JSON.parse(row.requestBody).handoffId;
        const done = await until('Weir to finish the second file', 240, async () => { const st = await weirStatus(idB); return st.state === 'completed' ? st : null; }, 1000);
        save('deluno', DELUNO_VERSION, `handoff-collision-${tag}`, `Deluno's hand-off of a single file whose name is already in Weir's output folder from an earlier, different file, collision policy ${policy} (Deluno ${DELUNO_VERSION}, Weir ${WEIR_VERSION}). Weir finished it with output ${JSON.stringify(done.value.outputFiles)}.`, [exchangeOf(row)]);
        await until('Deluno to settle the hand-off', 240, async () => { const h = await handoffOf(stack, release.name); return h && h.status === 'completed' ? h : null; }, 2000).catch(() => undefined);
      } finally {
        await setWeirLibrary(await weirLibrary(), { output_collision_policy: library.output_collision_policy });
        try { weirExec(`rm -f /weir/Ready/Movies/Recorded.Collide.* /downloads/completed/Movies/Recorded.Collide.*`); } catch { /* none */ }
      }
    }
  },

  // A season pack, and a pack one of whose episodes cannot be cleaned: Deluno's hand-off of a TV release folder (#1262).
  async packs() {
    await rerouteThroughProxy();
    const tv = await ensureTv();
    const cases = [
      { tag: 'season-pack', failurePolicy: null, episodes: [['S01E01', 'film'], ['S01E02', 'film']], note: 'a season pack of two episodes' },
      { tag: 'partial-season-pack', failurePolicy: 'hold', episodes: [['S01E01', 'film'], ['S01E02', 'film'], ['S01E03', 'garbage']], note: 'a season pack of two good episodes and one that cannot be read, failure policy hold' }
    ];
    for (const c of cases.filter((x) => !process.env.REC_ONLY || process.env.REC_ONLY.split(',').includes(x.tag))) {
      const library = await weirLibrary('tv');
      try {
        if (c.failurePolicy) await setWeirLibrary(library, { failure_policy: c.failurePolicy });
        const folder = `Recorded.${c.tag === 'season-pack' ? 'Show' : 'Partial'}.S01.1080p.WEB-DL.x264-E2E`;
        const release = makeRelease({ folder, torrent: `${folder}.torrent`, files: c.episodes.map(([ep, kind]) => ({ path: `${folder.replace('.S01.', `.${ep}.`)}.mkv`, kind, label: `${folder} ${ep}` })) });
        release.name = folder;
        await proxyClear();
        await sendRelease(release, { title: c.tag === 'season-pack' ? 'Recorded Show' : 'Recorded Partial', kind: 'tv', libraryId: tv.id });
        const row = await delunoHandoffFor(folder);
        const id = JSON.parse(row.requestBody).handoffId;
        const end = await until('Weir to finish the pack', 150, async () => { const st = await weirStatus(id); return ['completed', 'failed', 'rejected'].includes(st.state) ? st : null; }, 1000).catch(() => null);
        const last = end?.value ?? await weirStatus(id);
        save('deluno', DELUNO_VERSION, `handoff-${c.tag}`, `Deluno's hand-off of ${c.note} (Deluno ${DELUNO_VERSION}, Weir ${WEIR_VERSION}); Weir's state when the recording ended was ${last.state}, with ${(last.outputFiles ?? []).length} output file(s).`, [exchangeOf(row)]);
      } finally {
        await setWeirLibrary(await weirLibrary('tv'), { failure_policy: library.failure_policy });
        try { weirExec('rm -rf /weir/Ready/TV/Recorded.* /downloads/completed/TV/Recorded.*'); } catch { /* none */ }
      }
    }
  },

  // An audio-only film under the reject policy: Weir turns it down, and Deluno's hand-off for it is the request.
  async rejected() {
    await rerouteThroughProxy();
    const library = await weirLibrary();
    try {
      await setWeirLibrary(library, { failure_policy: 'reject' });
      const name = 'Recorded.AudioOnly.2020.1080p.WEB-DL.x264-E2E';
      const release = makeRelease({ folder: 'audioonly', torrent: `${name}.torrent`, single: true, files: [{ path: `${name}.mkv`, kind: 'audio', label: name }] });
      release.name = name;
      await proxyClear();
      await sendRelease(release, { title: 'Recorded AudioOnly' });
      const row = await delunoHandoffFor(`${name}.mkv`);
      const id = JSON.parse(row.requestBody).handoffId;
      const end = await until('Weir to end the audio-only hand-off', 300, async () => { const st = await weirStatus(id); return ['completed', 'failed', 'rejected'].includes(st.state) ? st : null; }, 1000);
      save('deluno', DELUNO_VERSION, 'handoff-rejected-release', `Deluno's hand-off of an audio-only film under Weir's reject failure policy (Deluno ${DELUNO_VERSION}, Weir ${WEIR_VERSION}); Weir ended it ${end.value.state}.`, [exchangeOf(row)]);
      const report = (await proxyLog()).find((r) => r.listener === 9349 && r.path.includes('/processors/events') && r.requestBody.includes(id));
      if (report) save('deluno', DELUNO_VERSION, 'events-failed-rejected-known-handoff', "The report Weir sent for an audio-only film under the reject failure policy, for a hand-off Deluno sent itself: Deluno's reply is the real one.", [exchangeOf(report)]);
      else addMissing('deluno events-failed-rejected-known-handoff: Weir sent no report for the audio-only hand-off');
    } finally {
      await setWeirLibrary(await weirLibrary(), { failure_policy: library.failure_policy });
      try { weirExec('rm -f /weir/Ready/Movies/Recorded.AudioOnly* /downloads/completed/Movies/Recorded.AudioOnly*'); } catch { /* none */ }
    }
  },

  // A second connection of the same kind hands off a file whose pass belongs to the first (Weir #956, Deluno #1250).
  async 'not-taken'() {
    await rerouteThroughProxy();
    // Connection B: Weir's second media-manager connection to Deluno (with a Deluno key of its own), and Deluno's second processor connection to Weir.
    const key = await stack.deluno.must('POST', '/api/api-keys', { name: 'Weir B (recording)', scopes: 'read,imports' });
    const made = await stack.weir.post('/api/v1/media-managers/connections', { kind: 'deluno', name: 'Deluno B', base_url: PROXY_DELUNO, api_key: key.apiKey, enabled: true });
    if (made.status >= 300) throw new Error(`Weir would not take a second connection: ${made.status} ${made.text.slice(0, 300)}`);
    const connB = made.body;
    const secretB = (await stack.weir.post(`/api/v1/media-managers/connections/${connB.id}/webhook-secret`)).body.webhook_secret;
    addSecret(secretB); addSecret(key.apiKey);
    const processorB = await stack.deluno.must('POST', '/api/integrations/processors/connections', {
      name: 'Weir B', provider: 'generic-webhook', submissionUrl: `${PROXY_WEIR}/api/v1/intake/webhook/deluno`, authHeaderName: 'X-Webhook-Secret',
      secret: secretB, isEnabled: true, healthCheckUrl: `${PROXY_WEIR}/ready`
    });
    const library = await weirLibrary();
    const links = library.manager_connection_ids;
    await setWeirLibrary(library, { manager_connection_ids: [...new Set([...links, connB.id])] });
    const workflow = async (processor) => stack.deluno.must('PUT', `/api/libraries/${stack.libraryId}/workflow`, {
      importWorkflow: 'refine-before-import', processorConnectionId: processor.id, processorName: processor.name,
      processorOutputPath: '/weir/Ready/Movies', processorTimeoutMinutes: 2880, processorFailureMode: 'block', cleanupMode: 'keep-source', removeEmptySourceFolders: false
    });
    const processorA = (await stack.deluno.must('GET', '/api/integrations/processors/connections')).find((x) => x.id === stack.processorId);
    try {
      await setAutomation(false);
      await workflow(processorB);
      await proxyClear();
      const sent = await sendFilm(stack, 'qbittorrent', 'nt', 'file');
      await until('qBittorrent to finish the film', 180, () => clientFinished(stack, 'qbittorrent', sent.film), 1000);
      // The file is in the watched folder; connection A (the first Deluno, here made by the recorder with its own secret) hands it to Weir first.
      const file = `/downloads/completed/Movies/${sent.film.release}.mkv`;
      const idA = randomUUID();
      const sourceKey = 'deluno';
      const library1 = (await (await fetch(`${WEIR_URL}/api/v1/intake/library-folders`, { headers: { 'X-Webhook-Secret': secret1 } })).json()).libraries.find((l) => l.media_type === 'movie');
      const firstBody = {
        eventType: 'deluno.processor-handoff', handoffId: idA, libraryId: String(library1.manager_library_key ?? 'recorded-library'), mediaType: 'movie', sourcePath: file,
        releaseName: sent.film.release, queueItemId: randomUUID(), callbackPath: '/api/integrations/processors/events', title: sent.film.title, year: sent.film.year
      };
      const first = await fetch(`${WEIR_URL}/api/v1/intake/webhook/${sourceKey}`, { method: 'POST', headers: { 'X-Webhook-Secret': secret1, 'content-type': 'application/json' }, body: JSON.stringify(firstBody) });
      if (!first.ok) throw new Error(`the first hand-off was refused: ${first.status}`);
      await proxyClear();
      // Now Deluno, through connection B, hands over the same file.
      await setAutomation(true);
      const handoff = await until('Deluno to hand the film to Weir', 120, () => handoffOf(stack, sent.film.release), 1000);
      const report = await waitForRow('Weir to report the hand-off that was not taken', 180, (r) => r.listener === 9349 && r.method === 'POST' && r.path.includes('/processors/events') && /not_taken/.test(r.requestBody));
      await sleep(3000);
      const rows = await proxyLog();
      const post = rows.find((r) => r.listener === 9348 && r.method === 'POST' && r.path.includes('/intake/webhook/'));
      const body = JSON.parse(report.requestBody);
      if (body.failureClass !== 'not_taken') addMissing(`weir not_taken: the report was ${body.status} ${body.failureClass ?? ''}`);
      save('deluno', DELUNO_VERSION, 'handoff-second-connection', `Deluno's hand-off through a second processor connection to the same Weir, for a file the first connection's pass already owns (Deluno ${DELUNO_VERSION}, Weir ${WEIR_VERSION}): Weir accepts the send with a 200.`, post ? [exchangeOf(post)] : []);
      save('deluno', DELUNO_VERSION, 'events-failed-not-taken', `Weir ${WEIR_VERSION} reports the second connection's send as not taken: failed, failureClass not_taken, no disposition, sourceRemoved false. Deluno's reply is the real one for a hand-off it sent.`, [exchangeOf(report)]);
      // The send that was not taken, asked about with the second connection's secret, as Deluno's poll asks.
      const idB = JSON.parse(post.requestBody).handoffId;
      const mine = await fetch(`${WEIR_URL}/api/v1/intake/handoffs/${sourceKey}/${idB}`, { headers: { 'X-Webhook-Secret': secretB } });
      save('weir', WEIR_VERSION, 'status-not-taken', "The status poll (with the second connection's secret) of the send Weir did not take because the file's pass belongs to another connection: cancelled, with Weir's words.", [{
        request: { method: 'GET', path: `/api/v1/intake/handoffs/${sourceKey}/${idB}`, headers: { 'x-webhook-secret': secretB } }, reply: { status: mine.status, headers: { 'content-type': mine.headers.get('content-type') ?? '' }, bodyText: await mine.text() }
      }]);
      // The first connection's hand-off, asked about with the second connection's secret: Weir answers 401, the same as a wrong secret.
      const other = await fetch(`${WEIR_URL}/api/v1/intake/handoffs/${sourceKey}/${idA}`, { headers: { 'X-Webhook-Secret': secretB } });
      const otherText = await other.text();
      save('weir', WEIR_VERSION, 'status-other-connections-secret', "The status poll of a hand-off with the secret of a different connection than the one that sent it: 401, the same words as a wrong secret.", [{
        request: { method: 'GET', path: `/api/v1/intake/handoffs/${sourceKey}/${idA}`, headers: { 'x-webhook-secret': secretB } }, reply: { status: other.status, headers: { 'content-type': other.headers.get('content-type') ?? '' }, bodyText: otherText }
      }]);
      await fetch(`${WEIR_URL}/api/v1/intake/handoffs/${sourceKey}/${idA}`, { method: 'DELETE', headers: { 'X-Webhook-Secret': secret1 } });
    } finally {
      await setAutomation(false);
      await workflow(processorA);
      await setWeirLibrary(await weirLibrary(), { manager_connection_ids: links });
      await stack.weir.send('DELETE', `/api/v1/media-managers/connections/${connB.id}`);
      await stack.deluno.call('DELETE', `/api/integrations/processors/connections/${processorB.id}`);
      await setAutomation(true);
    }
  }
};

for (const name of process.argv.slice(2)) {
  if (!steps[name]) { console.error('unknown step: ' + name); process.exit(2); }
  await steps[name]();
}
console.log('done');
process.exit(0);

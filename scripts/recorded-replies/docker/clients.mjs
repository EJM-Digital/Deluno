// Records the download-client states the golden VM could not produce (tests/fixtures/recorded/MISSING.md), from the real clients'
// published images running in Docker (tests/e2e/compose.yaml with a project name of your own).
//
//   node scripts/recorded-replies/docker/clients.mjs qbittorrent|deluge|transmission|sabnzbd [...]
//
// Ports come from REC_*_PORT (lib.mjs). Releases come from the e2e fixture (a real .torrent whose web seed is the fixture, or a real
// NZB posted as articles to its news server), so nothing here is a reply written by hand: every exchange is what the client answered.
import { Client, addSecret, addMissing, local, makeFilm, torrentBytes, replyJson, save, sleep, until } from './lib.mjs';
import { execFileSync } from 'node:child_process';
import { withAnnounce } from './bencode.mjs';

// `docker` as this machine runs it (REC_DOCKER="wsl -e docker" when Docker lives in WSL) and the compose project's name.
const docker = (...args) => { const parts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean); return execFileSync(parts[0], [...parts.slice(1), ...args], { encoding: 'utf8', timeout: 120_000 }); };
const project = process.env.REC_PROJECT ?? 'recdocker';

const QBIT = { username: 'e2e', password: 'e2e-qbittorrent-pass' };
const DELUGE_PASSWORD = 'deluge';
const SAB_KEY = 'e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0';
const CATEGORY = 'recorded-movies';
const inside = (film) => film.url; // the torrent's address as the other containers reach the fixture

// ------------------------------------------------------------------ qBittorrent

async function qbittorrent() {
  addSecret(QBIT.password);
  const q = new Client(local('qbittorrent'));
  const login = await q.send({ method: 'POST', path: '/api/v2/auth/login', form: QBIT });
  if (login.reply.status >= 300 || /fail/i.test(login.reply.bodyText)) throw new Error('qBittorrent would not sign in: ' + login.reply.status);
  const ver = (await q.get('/api/v2/app/version')).reply.bodyText.replace(/^v/, '');
  await q.send({ method: 'POST', path: '/api/v2/app/setPreferences', form: { json: JSON.stringify({ save_path: '/downloads/completed', temp_path_enabled: true, temp_path: '/downloads/incomplete', auto_tmm_enabled: false, dht: false, pex: false, lsd: false }) } });
  await q.send({ method: 'POST', path: '/api/v2/torrents/delete', form: { hashes: 'all', deleteFiles: 'true' } });
  await q.send({ method: 'POST', path: '/api/v2/torrents/createCategory', form: { category: CATEGORY, savePath: '/downloads/completed/Movies' } });

  const info = (hash) => q.get(`/api/v2/torrents/info?hashes=${hash}`);
  const add = async (film, extra = {}) => q.send({ method: 'POST', path: '/api/v2/torrents/add', multipart: { torrents: await torrentBytes(film), category: CATEGORY, ...extra } });
  const finished = async (hash) => until(`qBittorrent to finish ${hash}`, 120, async () => { const j = replyJson(await info(hash)); return j?.[0] && j[0].progress >= 1 && j[0].amount_left === 0 ? j[0] : null; }, 1000);
  const remove = (hash) => q.send({ method: 'POST', path: '/api/v2/torrents/delete', form: { hashes: hash, deleteFiles: 'true' } });

  // setAutoManagement on a finished torrent, off and on again.
  {
    const film = await makeFilm({ title: 'Recorded qBittorrent Auto' });
    const added = await add(film); await finished(film.infohash);
    const off = await q.send({ method: 'POST', path: '/api/v2/torrents/setAutoManagement', form: { hashes: film.infohash, enable: 'false' } });
    const afterOff = await info(film.infohash);
    const on = await q.send({ method: 'POST', path: '/api/v2/torrents/setAutoManagement', form: { hashes: film.infohash, enable: 'true' } });
    const afterOn = await info(film.infohash);
    save('qbittorrent', ver, 'auto-management-set', 'A finished torrent: automatic torrent management switched off (enable=false) and on again, with the torrent as listed after each.', [added, off, afterOff, on, afterOn]);
    await remove(film.infohash);
  }

  // A tracker that is not working: the torrent carries an announce address nothing answers.
  {
    const film = await makeFilm({ title: 'Recorded qBittorrent Tracker' });
    const bytes = withAnnounce(await torrentBytes(film), 'http://127.0.0.1:1/announce');
    const added = await q.send({ method: 'POST', path: '/api/v2/torrents/add', multipart: { torrents: bytes, category: CATEGORY } });
    const trackers = await until('a tracker status of 4', 90, async () => { const e = await q.get(`/api/v2/torrents/trackers?hash=${film.infohash}`); const j = replyJson(e); return j?.some((t) => t.status === 4) ? e : null; }, 2000);
    await finished(film.infohash);
    save('qbittorrent', ver, 'tracker-not-working', 'A torrent whose only tracker (a refused connection) is not working: the tracker list shows status 4 with the reason, while the data came from a web seed and finished.', [added, trackers, await info(film.infohash)]);
    await remove(film.infohash);
  }

  // An add by link: the torrent's address, not the file.
  {
    const film = await makeFilm({ title: 'Recorded qBittorrent Link' });
    const added = await q.send({ method: 'POST', path: '/api/v2/torrents/add', multipart: { urls: inside(film), category: CATEGORY } });
    const listed = await until('the linked torrent to appear', 60, async () => { const e = await info(film.infohash); return replyJson(e)?.length ? e : null; }, 1000);
    await finished(film.infohash);
    save('qbittorrent', ver, 'add-link', 'A torrent added by its link (the indexer\'s address) instead of by file: qBittorrent fetches the file itself.', [added, listed, await info(film.infohash)]);
    await remove(film.infohash);
  }
  // A link that is dead.
  {
    const dead = 'http://fixture:8765/dl/Recorded.Nothing.Here.torrent';
    const added = await q.send({ method: 'POST', path: '/api/v2/torrents/add', multipart: { urls: dead, category: CATEGORY } });
    await sleep(6000);
    const list = await q.get(`/api/v2/torrents/info?category=${CATEGORY}`);
    save('qbittorrent', ver, 'add-link-dead', 'A torrent added by a link the server answers 404 for: what qBittorrent says to the add, and that it lists nothing afterwards.', [added, list]);
  }
}

// ------------------------------------------------------------------ Deluge

async function deluge() {
  const d = new Client(local('deluge'));
  let id = 100;
  const call = (method, params = []) => d.send({ method: 'POST', path: '/json', json: { method, params, id: ++id } });
  const login = await call('auth.login', [DELUGE_PASSWORD]);
  if (replyJson(login)?.result !== true) throw new Error('Deluge would not accept its shipped login');
  if (replyJson(await call('web.connected'))?.result !== true) {
    const hosts = replyJson(await call('web.get_hosts')).result;
    await call('web.connect', [hosts[0][0]]);
  }
  await until('Deluge web connected to its daemon', 60, async () => replyJson(await call('web.connected'))?.result === true);
  await call('core.set_config', [{ download_location: '/downloads/incomplete', move_completed: false, dht: false, lsd: false, utpex: false, upnp: false, natpmp: false }]);
  const enabled = replyJson(await call('core.get_enabled_plugins')).result;
  if (!enabled.includes('Label')) await call('core.enable_plugin', ['Label']);
  const ver = String(replyJson(await call('daemon.get_version'))?.result ?? replyJson(await call('daemon.info'))?.result);
  const fields = ['name', 'state', 'progress', 'download_payload_rate', 'upload_payload_rate', 'eta', 'total_size', 'total_done', 'num_peers', 'num_seeds', 'time_added', 'label', 'message', 'save_path', 'move_completed', 'move_completed_path', 'ratio', 'seeding_time', 'tracker_status', 'trackers', 'private', 'queue', 'stop_at_ratio', 'stop_ratio', 'remove_at_ratio'];
  const list = () => call('web.update_ui', [fields, {}]);
  const finished = async (hash) => until(`Deluge to finish ${hash}`, 120, async () => { const j = replyJson(await list()); const t = j?.result?.torrents?.[hash]; return t && t.progress >= 100 && t.state !== 'Moving' && t.state !== 'Checking' ? t : null; }, 1000);
  const remove = (hash) => call('core.remove_torrent', [hash, true]);
  for (const hash of Object.keys(replyJson(await list())?.result?.torrents ?? {})) await remove(hash);

  // An add by link: core.add_torrent_url.
  {
    const film = await makeFilm({ title: 'Recorded Deluge Link' });
    const added = await call('core.add_torrent_url', [encodeURI(inside(film)), { add_paused: false, move_completed: true, move_completed_path: '/downloads/completed/Movies' }]);
    await finished(film.infohash);
    save('deluge', ver, 'add-url', 'A torrent added by its link with core.add_torrent_url (Deluge fetches the file itself), then the torrent list once it finished.', [added, await list()]);
    await remove(film.infohash);
    const dead = await call('core.add_torrent_url', ['http://fixture:8765/dl/Recorded.Nothing.Here.torrent', { add_paused: false }]);
    await sleep(3000);
    save('deluge', ver, 'add-url-dead', 'core.add_torrent_url for a link the server answers 404 for, then the torrent list (Deluge adds nothing).', [dead, await list()]);
  }
  // A torrent Deluge never had.
  {
    const never = '0123456789abcdef0123456789abcdef01234567';
    save('deluge', ver, 'status-unknown-hash', 'core.get_torrent_status for a hash Deluge never had, asked for the label and for the files.', [
      await call('core.get_torrent_status', [never, ['label']]), await call('core.get_torrent_status', [never, ['files']])]);
  }
  // The Label plugin off. Deluge keeps serving label.* until it restarts, so the plugin is switched off and the container restarted,
  // which is how a Deluge that has never had the plugin on (its shipped state) looks to a client.
  {
    const film = await makeFilm({ title: 'Recorded Deluge No Label' });
    await call('core.add_torrent_file', ['x.torrent', Buffer.from(await torrentBytes(film)).toString('base64'), { add_paused: false }]);
    await finished(film.infohash);
    await call('core.disable_plugin', ['Label']);
    await sleep(2000);
    docker('restart', `${project}-deluge-1`);
    const d2 = new Client(local('deluge'));
    const call2 = (method, params = []) => d2.send({ method: 'POST', path: '/json', json: { method, params, id: ++id } });
    await until('Deluge to come back', 120, async () => replyJson(await call2('auth.login', [DELUGE_PASSWORD]))?.result === true, 2000);
    await until('Deluge web connected', 60, async () => { if (replyJson(await call2('web.connected'))?.result === true) return true; const hosts = replyJson(await call2('web.get_hosts'))?.result; if (hosts) await call2('web.connect', [hosts[0][0]]); return false; }, 2000);
    const exchanges = [await call2('core.get_enabled_plugins'), await call2('label.get_labels'), await call2('label.add', [CATEGORY]), await call2('label.set_torrent', [film.infohash, CATEGORY])];
    save('deluge', ver, 'label-plugin-off', 'Deluge with the Label plugin off (switched off with core.disable_plugin and Deluge restarted): label.get_labels, label.add and label.set_torrent.', exchanges);
    await call2('core.enable_plugin', ['Label']);
    await sleep(2000);
    await call2('core.remove_torrent', [film.infohash, true]);
  }
}

// ------------------------------------------------------------------ Transmission

async function transmission() {
  const t = new Client(local('transmission'));
  let sid = '';
  let tag = 0;
  const rpc = async (method, args = {}) => {
    for (let attempt = 0; attempt < 3; attempt++) {
      const e = await t.send({ method: 'POST', path: '/transmission/rpc', json: { method, arguments: args, tag: ++tag }, headers: { 'X-Transmission-Session-Id': sid }, keep: ['content-type', 'x-transmission-session-id'] });
      if (e.reply.status === 409) { sid = e.reply.headers['x-transmission-session-id']; addSecret(sid); continue; }
      return e;
    }
    throw new Error('Transmission would not take a session id');
  };
  await rpc('session-set', { 'download-dir': '/downloads/completed', 'incomplete-dir': '/downloads/incomplete', 'incomplete-dir-enabled': true, 'dht-enabled': false, 'pex-enabled': false, 'lpd-enabled': false, 'utp-enabled': false });
  const ver = String(replyJson(await rpc('session-get', { fields: ['version'] })).arguments.version).split(' ')[0];
  const fields = ['id', 'hashString', 'name', 'status', 'percentDone', 'rateDownload', 'rateUpload', 'eta', 'totalSize', 'downloadedEver', 'peersConnected', 'peersSendingToUs', 'addedDate', 'activityDate', 'doneDate', 'downloadDir', 'labels', 'error', 'errorString', 'isStalled', 'uploadRatio', 'secondsSeeding', 'isPrivate', 'trackerStats', 'queuePosition', 'seedRatioMode', 'seedRatioLimit', 'seedIdleMode', 'seedIdleLimit'];
  const get = (hash) => rpc('torrent-get', { ids: [hash], fields });
  const finished = (hash) => until(`Transmission to finish ${hash}`, 120, async () => { const j = replyJson(await get(hash)); const x = j?.arguments?.torrents?.[0]; return x && x.percentDone >= 1 ? x : null; }, 1000);

  for (const x of replyJson(await rpc('torrent-get', { fields: ['id'] }))?.arguments?.torrents ?? []) await rpc('torrent-remove', { ids: [x.id], 'delete-local-data': true });
  const film = await makeFilm({ title: 'Recorded Transmission Seed' });
  const added = await rpc('torrent-add', { paused: false, 'download-dir': '/downloads/completed/Movies', metainfo: Buffer.from(await torrentBytes(film)).toString('base64') });
  const early = await get(film.infohash);
  await finished(film.infohash);
  await sleep(1500);
  save('transmission', ver, 'finished-seed-fields', 'A torrent added by file, then torrent-get with every field Deluno asks for now (uploadRatio, secondsSeeding, activityDate, doneDate, seedRatioMode, seedRatioLimit, seedIdleMode, seedIdleLimit) while it was downloading and again once it finished.', [added, early, await get(film.infohash)]);
  save('transmission', ver, 'label-set-refused-comma', 'torrent-set with a label that has a comma in it: Transmission answers 200 with the reason in "result".', [await rpc('torrent-set', { ids: [film.infohash], labels: ['recorded,movies'] })]);
  save('transmission', ver, 'label-set-refused-empty', 'torrent-set with an empty label: Transmission answers 200 with the reason in "result".', [await rpc('torrent-set', { ids: [film.infohash], labels: [''] })]);
  await rpc('torrent-remove', { ids: [film.infohash], 'delete-local-data': true });
}

// ------------------------------------------------------------------ SABnzbd

async function sabnzbd() {
  addSecret(SAB_KEY);
  const s = new Client(local('sabnzbd'));
  const api = (mode, extra = {}) => s.get('/api?' + new URLSearchParams({ mode, output: 'json', apikey: SAB_KEY, ...extra }));
  const ver = replyJson(await api('version')).version;
  const config = replyJson(await api('get_config', { section: 'misc', keyword: 'no_dupes' }));
  const original = String(config?.config?.misc?.no_dupes ?? '0');
  const nzb = async (f) => new Uint8Array(await (await fetch(`${local('fixture')}/dl/${f.url.split('/dl/')[1]}`)).arrayBuffer());
  const addFile = async (f, bytes) => s.send({ method: 'POST', path: '/api?' + new URLSearchParams({ mode: 'addfile', output: 'json', apikey: SAB_KEY, cat: 'movies', nzbname: f.release }), multipart: { name: { bytes, filename: f.release + '.nzb', type: 'application/x-nzb' } } });
  const history = (search) => api('history', { search, limit: '50' });
  const clear = async () => { await api('queue', { name: 'delete', value: 'all', del_files: '1' }); await api('history', { name: 'delete', value: 'all', del_files: '1' }); };
  try {
    for (const [mode, state, note] of [
      ['1', 'duplicate-discarded', 'Duplicate detection set to discard: the same NZB sent twice as a file. The second answer carries a new job id and the job then vanishes: it is in neither the queue nor the history.'],
      ['3', 'duplicate-failed', 'Duplicate detection set to fail: the same NZB sent twice as a file, then the queue and history: the second is there as a failed job with SABnzbd\'s words for it in fail_message.']
    ]) {
      await clear();
      const set = await api('set_config', { section: 'misc', keyword: 'no_dupes', value: mode });
      const f = await makeFilm({ title: `Recorded SABnzbd Duplicate ${mode}`, usenet: true });
      const bytes = await nzb(f);
      const first = await addFile(f, bytes);
      await until('SABnzbd to hold the first job', 30, async () => replyJson(await api('queue'))?.queue?.slots?.length || replyJson(await history(f.release))?.history?.slots?.length);
      const second = await addFile(f, bytes);
      await sleep(4000);
      save('sabnzbd', ver, state, note, [set, first, second, await api('queue'), await history(f.release)]);
    }
  } finally {
    await api('set_config', { section: 'misc', keyword: 'no_dupes', value: original });
    await clear();
  }
}

const jobs = { qbittorrent, deluge, transmission, sabnzbd };
for (const name of process.argv.slice(2)) {
  if (!jobs[name]) { console.error('unknown: ' + name); process.exit(2); }
  await jobs[name]();
}
console.log('done');

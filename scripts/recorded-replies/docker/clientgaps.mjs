// Records download-client states the golden VM and clients.mjs left out (tests/fixtures/recorded/MISSING.md, #1301), from the real
// clients' published images running in Docker. Nothing here is written by hand: every exchange is what the client answered.
//
//   node scripts/recorded-replies/docker/clientgaps.mjs qbittorrent|qbittorrent4|sabnzbd|nzbget [...]
//
// Needs the e2e stack's services (tests/e2e/compose.yaml, a project name of your own) for the client and the fixture. For qbittorrent4 an
// older qBittorrent (the lscr.io/linuxserver/qbittorrent 4.6.x image) on the stack's network, with qBittorrent's seed login:
//   docker run -d --name <project>-qbit4 --network <project>_default -e PUID=1654 -e PGID=1654 -e WEBUI_PORT=8080 \
//     -p 127.0.0.1:${REC_QBIT4_PORT:-41082}:8080 -v <project>_downloads:/downloads -v <a volume holding qBittorrent/qBittorrent.conf from tests/e2e/qbittorrent>:/config \
//     lscr.io/linuxserver/qbittorrent:4.6.7
// Ports come from REC_*_PORT (lib.mjs). The qBittorrent address ban (a few wrong logins) lasts until the container restarts, so the ban is
// recorded last and the container is restarted after it (REC_DOCKER and REC_PROJECT as in clients.mjs).
import { Client, addSecret, local, makeFilm, torrentBytes, replyJson, save, sleep, until } from './lib.mjs';
import { execFileSync } from 'node:child_process';

const docker = (...args) => { const parts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean); return execFileSync(parts[0], [...parts.slice(1), ...args], { encoding: 'utf8', timeout: 120_000 }); };
const project = process.env.REC_PROJECT ?? 'covi';

const QBIT = { username: 'e2e', password: 'e2e-qbittorrent-pass' };
const SAB_KEY = 'e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0';
const NZBGET = { username: 'nzbget', password: 'tegbzn6789' };
const CATEGORY = 'recorded-movies';

// ------------------------------------------------------------------ qBittorrent (5.x and 4.x)

async function qbitSession(base) {
  addSecret(QBIT.password);
  const q = new Client(base);
  const login = await q.send({ method: 'POST', path: '/api/v2/auth/login', form: QBIT });
  if (login.reply.status >= 300 || /fail/i.test(login.reply.bodyText)) throw new Error('qBittorrent would not sign in: ' + login.reply.status + ' ' + login.reply.bodyText);
  const ver = (await q.get('/api/v2/app/version')).reply.bodyText.replace(/^v/, '');
  await q.send({ method: 'POST', path: '/api/v2/app/setPreferences', form: { json: JSON.stringify({ save_path: '/downloads/completed', temp_path_enabled: true, temp_path: '/downloads/incomplete', auto_tmm_enabled: false, dht: false, pex: false, lsd: false, queueing_enabled: true, max_active_downloads: 1, max_active_torrents: 1, max_active_uploads: 1 }) } });
  await q.send({ method: 'POST', path: '/api/v2/torrents/delete', form: { hashes: 'all', deleteFiles: 'true' } });
  await q.send({ method: 'POST', path: '/api/v2/torrents/createCategory', form: { category: CATEGORY, savePath: '/downloads/completed/Movies' } });
  const info = (hashes) => q.get('/api/v2/torrents/info' + (hashes ? `?hashes=${hashes}` : ''));
  const add = async (film, extra = {}) => q.send({ method: 'POST', path: '/api/v2/torrents/add', multipart: { torrents: await torrentBytes(film), category: CATEGORY, ...extra } });
  const remove = () => q.send({ method: 'POST', path: '/api/v2/torrents/delete', form: { hashes: 'all', deleteFiles: 'true' } });
  return { q, ver, info, add, remove };
}

async function qbittorrent() {
  const { q, ver, info, add, remove } = await qbitSession(local('qbittorrent'));

  // Three torrents held in the queue (added stopped), then each move on one of them with the list after it.
  {
    const films = [];
    const adds = [];
    for (const n of [1, 2, 3]) {
      const film = await makeFilm({ title: `Recorded qBittorrent Queue ${n}` });
      films.push(film);
      adds.push(await add(film, { stopped: 'true', paused: 'true' }));
    }
    await until('the three torrents to be held stopped', 60, async () => { const l = replyJson(await info()); return l?.length >= 3 && l.every((t) => /stopped/i.test(t.state)); }, 1000);
    const hash = films[2].infohash;
    const move = (name) => q.send({ method: 'POST', path: `/api/v2/torrents/${name}`, form: { hashes: hash } });
    const exchanges = [...adds, await info()];
    for (const name of ['topPrio', 'increasePrio', 'decreasePrio', 'bottomPrio']) { const moved = await move(name); await sleep(1500); exchanges.push(moved, await info()); }
    save('qbittorrent', ver, 'queue-moved', 'Three torrents added stopped with queueing on, then one moved with topPrio, increasePrio, decreasePrio and bottomPrio, with the torrent list (and each torrent\'s "priority" queue position) after every move.', exchanges);
    await remove();
  }

  // Force start on a torrent held stopped.
  {
    const first = await makeFilm({ title: 'Recorded qBittorrent Force A' });
    const second = await makeFilm({ title: 'Recorded qBittorrent Force B' });
    const a = await add(first, { stopped: 'true', paused: 'true' });
    const b = await add(second, { stopped: 'true', paused: 'true' });
    await until('the two torrents to be held stopped', 60, async () => { const l = replyJson(await info()); return l?.length >= 2 && l.every((t) => /stopped/i.test(t.state)); }, 1000);
    const before = await info();
    const forced = await q.send({ method: 'POST', path: '/api/v2/torrents/setForceStart', form: { hashes: second.infohash, value: 'true' } });
    await sleep(1500);
    save('qbittorrent', ver, 'force-start-set', 'Two torrents added stopped, then setForceStart with value=true on the second, with the list before and after (the second becomes forcedDL and the first stays stopped).', [a, b, before, forced, await info()]);
    await remove();
  }

  // The pause and resume names qBittorrent 4.x used: a 5.x answers 404 for them, as a 4.x answers 404 for stop and start.
  {
    const film = await makeFilm({ title: 'Recorded qBittorrent Old Names' });
    await add(film);
    await until('the torrent to be listed', 60, async () => replyJson(await info(film.infohash))?.length, 1000);
    const pause = await q.send({ method: 'POST', path: '/api/v2/torrents/pause', form: { hashes: film.infohash } });
    const resume = await q.send({ method: 'POST', path: '/api/v2/torrents/resume', form: { hashes: film.infohash } });
    save('qbittorrent', ver, 'old-pause-resume-names', 'POST torrents/pause and torrents/resume (the 4.x names) sent to this newer qBittorrent, which has stop and start instead: it answers 404 Not Found to each.', [pause, resume]);
    await remove();
  }

  // Last: wrong logins until the address is banned, then a right login while banned. The ban lasts until the container restarts.
  {
    const wrong = (i) => { q.cookies.clear(); return q.send({ method: 'POST', path: '/api/v2/auth/login', form: { username: QBIT.username, password: `not-the-password-${i}` } }); };
    const attempts = [];
    for (let i = 1; i <= 6; i++) attempts.push(await wrong(i));
    q.cookies.clear();
    const right = await q.send({ method: 'POST', path: '/api/v2/auth/login', form: QBIT });
    save('qbittorrent', ver, 'login-banned', 'Six sign-ins with a wrong password in a row (the first five answer 401), then a sign-in with the right password: qBittorrent has banned the address after too many failed attempts and answers 403 to both.', [...attempts, right]);
    docker('restart', `${project}-qbittorrent-1`);
    await sleep(8000);
  }
}

async function qbittorrent4() {
  const { q, ver, info, add, remove } = await qbitSession(`http://127.0.0.1:${process.env.REC_QBIT4_PORT ?? 41082}`);
  const film = await makeFilm({ title: 'Recorded qBittorrent Four' });
  const added = await add(film);
  await until('the torrent to be listed', 60, async () => replyJson(await info(film.infohash))?.length, 1000);
  const downloading = await info(film.infohash);
  const stop = await q.send({ method: 'POST', path: '/api/v2/torrents/stop', form: { hashes: film.infohash } });
  const pause = await q.send({ method: 'POST', path: '/api/v2/torrents/pause', form: { hashes: film.infohash } });
  await sleep(1500);
  const paused = await info(film.infohash);
  const pausedAgain = await info(film.infohash);
  const start = await q.send({ method: 'POST', path: '/api/v2/torrents/start', form: { hashes: film.infohash } });
  const resume = await q.send({ method: 'POST', path: '/api/v2/torrents/resume', form: { hashes: film.infohash } });
  await sleep(1500);
  save('qbittorrent', ver, 'stop-start-missing', 'A qBittorrent 4.x: a torrent added and listed downloading, then stop (the 5.x name) answered 404, pause answered 200 and the torrent listed as paused (twice, before and after a resume was asked for); then start (5.x) answered 404, resume answered 200 and the torrent listed again.', [added, downloading, stop, pause, paused, pausedAgain, start, resume, await info(film.infohash)]);
  await remove();
}

// ------------------------------------------------------------------ SABnzbd

async function sabnzbd() {
  addSecret(SAB_KEY);
  const s = new Client(local('sabnzbd'));
  const api = (mode, extra = {}) => s.get('/api?' + new URLSearchParams({ mode, output: 'json', apikey: SAB_KEY, ...extra }));
  const ver = replyJson(await api('version')).version;
  const clear = async () => { await api('queue', { name: 'delete', value: 'all', del_files: '1' }); await api('history', { name: 'delete', value: 'all', del_files: '1' }); };
  const nzb = async (f) => new Uint8Array(await (await fetch(`${local('fixture')}/dl/${f.url.split('/dl/')[1]}`)).arrayBuffer());
  const addFile = async (f) => s.send({ method: 'POST', path: '/api?' + new URLSearchParams({ mode: 'addfile', output: 'json', apikey: SAB_KEY, cat: 'movies', nzbname: f.release }), multipart: { name: { bytes: await nzb(f), filename: f.release + '.nzb', type: 'application/x-nzb' } } });
  await clear();
  try {
    // Paused as a whole, then resumed as a whole.
    const pause = await api('pause');
    const paused = await api('queue');
    const resume = await api('resume');
    const running = await api('queue');
    save('sabnzbd', ver, 'client-paused-resumed', 'SABnzbd paused as a whole (mode=pause), the queue (queue.paused is true), resumed as a whole (mode=resume), and the queue again (queue.paused is false).', [pause, paused, resume, running]);

    // Priority tiers on two jobs held by a paused queue.
    await api('pause');
    const jobs = [];
    const adds = [];
    for (const n of [1, 2]) {
      const f = await makeFilm({ title: `Recorded SABnzbd Priority ${n}`, usenet: true });
      const added = await addFile(f);
      adds.push(added);
      jobs.push(replyJson(added)?.nzo_ids?.[0]);
    }
    const exchanges = [...adds, await api('queue')];
    for (const tier of ['2', '1', '-2', '-1']) exchanges.push(await api('queue', { name: 'priority', value: jobs[1], value2: tier }), await api('queue'));
    save('sabnzbd', ver, 'priority-tiers', 'Two jobs added to a paused queue, then one given the priority tiers Deluno moves it with (value2 2, 1, -2) and -1, mode=queue&name=priority, with the queue after each (the job\'s priority and place).', exchanges);
  } finally {
    await api('resume');
    await clear();
  }
}

// ------------------------------------------------------------------ NZBGet

async function nzbget() {
  addSecret(NZBGET.password);
  const n = new Client(local('nzbget'), { headers: { authorization: 'Basic ' + Buffer.from(`${NZBGET.username}:${NZBGET.password}`).toString('base64') } });
  let seq = 200;
  const rpc = (method, params = []) => n.send({ method: 'POST', path: '/jsonrpc', json: { method, params, id: ++seq } });
  const result = (exchange) => replyJson(exchange)?.result;
  const ver = String(result(await rpc('version'))).replace(/-.*$/, '');
  {
    const wanted = { DestDir: '/downloads/completed', InterDir: '/downloads/incomplete', 'Server1.Active': 'yes', 'Server1.Name': 'fixture', 'Server1.Host': 'fixture', 'Server1.Port': '1119', 'Server1.Username': 'e2e', 'Server1.Password': 'e2e', 'Server1.Connections': '4', 'Server1.Encryption': 'no' };
    const byName = new Map((result(await rpc('config')) ?? []).map((c) => [c.Name, c.Value]));
    if (byName.get('Server1.Host') !== 'fixture') {
      for (const [k, v] of Object.entries(wanted)) byName.set(k, v);
      for (const own of ['Version', 'ConfigFile', 'AppBin', 'AppDir']) byName.delete(own);
      await rpc('saveconfig', [[...byName.entries()].map(([Name, Value]) => ({ Name, Value }))]);
      await rpc('reload');
      await sleep(3000);
      await until('NZBGet to be back after its reload', 120, async () => (result(await rpc('config')) ?? []).find((c) => c.Name === 'Server1.Host')?.Value === 'fixture', 2000);
    }
  }
  const ids = [];
  try {
    // Paused as a whole (download, post-processing and scan), then each released: what Deluno's "resume the whole client" sends.
    const pausedExchanges = [];
    for (const m of ['pausedownload', 'pausepost', 'pausescan']) pausedExchanges.push(await rpc(m));
    const paused = await rpc('status');
    const resumed = [];
    for (const m of ['resumedownload', 'resumepost', 'resumescan']) resumed.push(await rpc(m));
    save('nzbget', ver, 'client-paused-resumed', 'NZBGet paused as a whole (pausedownload, pausepost, pausescan), its status (DownloadPaused, PostPaused and ScanPaused are true), then released with resumedownload, resumepost and resumescan, and its status again.', [...pausedExchanges, paused, ...resumed, await rpc('status')]);

    // Three jobs held paused, then moved.
    const adds = [];
    for (const k of [1, 2, 3]) {
      const f = await makeFilm({ title: `Recorded NZBGet Queue ${k}`, usenet: true });
      const add = await rpc('append', [f.release + '.nzb', f.url, 'movies', 0, false, true, '', 0, 'SCORE', []]);
      adds.push(add);
      ids.push(result(add));
    }
    const edit = (command, offset) => rpc('editqueue', [command, offset === undefined ? '' : String(offset), [ids[2]]]);
    const exchanges = [...adds, await rpc('listgroups')];
    exchanges.push(await edit('GroupMoveTop'), await rpc('listgroups'));
    exchanges.push(await edit('GroupMoveOffset', 1), await rpc('listgroups'));
    exchanges.push(await edit('GroupMoveOffset', -1), await rpc('listgroups'));
    exchanges.push(await edit('GroupMoveBottom'), await rpc('listgroups'));
    save('nzbget', ver, 'queue-moved', 'Three jobs appended paused, then the last one moved with GroupMoveTop, GroupMoveOffset (+1 and -1) and GroupMoveBottom (editqueue), with the groups listed after every move.', exchanges);
  } finally {
    for (const id of ids.filter(Boolean)) { await rpc('editqueue', ['GroupFinalDelete', '', [id]]); await rpc('editqueue', ['HistoryFinalDelete', '', [id]]); }
  }
}

const jobs = { qbittorrent, qbittorrent4, sabnzbd, nzbget };
for (const name of process.argv.slice(2)) {
  if (!jobs[name]) { console.error('unknown: ' + name); process.exit(2); }
  await jobs[name]();
}
console.log('done');

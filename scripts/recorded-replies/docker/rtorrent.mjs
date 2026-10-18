// Records the real rTorrent's XML-RPC replies (tests/fixtures/recorded/rtorrent/<version>/<state>.json) from its published image in Docker (#1237).
//
//   docker compose -p rec2 -f apps.compose.yaml up -d rtorrent rtseed tracker      (a private copy under WSL's disk)
//   node scripts/recorded-replies/docker/rtorrent.mjs
//
// rTorrent is the image crazymax/rtorrent-rutorrent (rTorrent 0.16, its XML-RPC behind nginx). A second one (rtseed) seeds the film the first
// downloads, rate-limited, so a transfer really is in flight; tracker.mjs only introduces them. Every reply is what rTorrent answered to Deluno's
// exact call (the same method names and field lists as RtorrentDownloadClient.cs). The login for the mount is made here, in the container only.
import { createHash, randomBytes } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Client, addSecret, addMissing, save, sleep, until } from './lib.mjs';
import { encode } from './bencode.mjs';

const project = process.env.REC_PROJECT2 ?? 'rec2';
const dockerParts = (process.env.REC_DOCKER ?? 'wsl -e docker').split(' ').filter(Boolean);
const docker = (...args) => execFileSync(dockerParts[0], [...dockerParts.slice(1), ...args], { encoding: 'utf8', timeout: 180_000 });
const URLS = { rtorrent: `http://127.0.0.1:${process.env.REC2_RTORRENT_PORT ?? 38000}`, seed: `http://127.0.0.1:${process.env.REC2_RTSEED_PORT ?? 38001}`, tracker: `http://127.0.0.1:${process.env.REC2_TRACKER_PORT ?? 38080}` };
const ANNOUNCE = 'http://tracker:8080/announce';
const LINK_BASE = 'http://tracker:8080/dl/';
const FIELDS = ['d.hash=', 'd.name=', 'd.is_active=', 'd.is_hash_checking=', 'd.complete=', 'd.bytes_done=', 'd.size_bytes=', 'd.down.rate=', 'd.up.rate=', 'd.ratio=', 'd.message=', 'd.directory=', 'd.peers_connected='];

// ------------------------------------------------------------------ XML-RPC as RtorrentDownloadClient writes it

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const value = (v) => (v && v.base64 ? `<value><base64>${Buffer.from(v.base64).toString('base64')}</base64></value>` : typeof v === 'number' ? `<value><i4>${v}</i4></value>` : `<value><string>${esc(v)}</string></value>`);
const call = (method, params = []) => `<?xml version="1.0"?><methodCall><methodName>${method}</methodName><params>${params.map((p) => `<param>${value(p)}</param>`).join('')}</params></methodCall>`;

/** The rows of a multicall answer, each as its values read as text. */
function rowsOf(exchange) {
  const body = exchange.reply.bodyText ?? '';
  const rows = [];
  const re = /<array><data>((?:<value>.*?<\/value>)+)<\/data><\/array>/gs;
  const outer = body.indexOf('<array><data>');
  if (outer < 0) return rows;
  const inner = body.slice(outer + 13);
  for (const m of inner.matchAll(re)) rows.push([...m[1].matchAll(/<value>(?:<[a-z0-9]+>)?(.*?)(?:<\/[a-z0-9]+>)?<\/value>/gs)].map((x) => x[1]));
  return rows;
}
const faultOf = (e) => /<fault>/.test(e.reply.bodyText ?? '');

function makeClient(base, auth) {
  const c = new Client(base, { keep: ['content-type', 'www-authenticate'], headers: auth ? { authorization: 'Basic ' + Buffer.from(auth).toString('base64') } : {} });
  const rpc = (method, params = []) => c.send({ method: 'POST', path: '/RPC2', text: call(method, params), contentType: 'text/xml' });
  return { c, rpc };
}

// ------------------------------------------------------------------ a torrent made here (no web seed in rTorrent: the second client seeds it)

function makeTorrent(name, files, pieceLength = 262144) {
  // files: [{ path: ['a','b.mkv'], data: Buffer }]  (one file, no folder: name is the file name)
  const multi = files.length > 1 || files[0].path.length > 0 && files[0].folder;
  const all = Buffer.concat(files.map((f) => f.data));
  const pieces = [];
  for (let i = 0; i < all.length; i += pieceLength) pieces.push(createHash('sha1').update(all.subarray(i, i + pieceLength)).digest());
  const info = new Map([['name', name], ['piece length', pieceLength], ['pieces', Buffer.concat(pieces)]]);
  if (multi) info.set('files', files.map((f) => new Map([['length', f.data.length], ['path', f.path]])));
  else info.set('length', all.length);
  const root = new Map([['announce', ANNOUNCE], ['info', info], ['created by', 'recorder'], ['creation date', Math.floor(Date.now() / 1000)]]);
  const bytes = encode(root);
  const infohash = createHash('sha1').update(encode(info)).digest('hex');
  return { name, bytes, infohash, files, multi, size: all.length, link: `${LINK_BASE}${encodeURIComponent(name)}.torrent`, linkName: `${name}.torrent` };
}

async function publish(t) {
  const r = await fetch(`${URLS.tracker}/torrents/${encodeURIComponent(t.linkName.replace(/\.torrent$/, '') + '.torrent')}`, { method: 'POST', body: t.bytes });
  if (!r.ok) throw new Error('tracker would not take the torrent: ' + r.status);
}

/** Puts the film's data into the seeding client and starts it, so it can be downloaded from. */
async function seed(seedRpc, t, { keepFraction = 1 } = {}) {
  const tmp = join(tmpdir(), 'rec2-' + randomBytes(4).toString('hex'));
  mkdirSync(tmp, { recursive: true });
  try {
    if (t.multi) {
      for (const f of t.files) { const p = join(tmp, t.name, ...f.path); mkdirSync(join(p, '..'), { recursive: true }); writeFileSync(p, f.data); }
    } else writeFileSync(join(tmp, t.name), keepFraction < 1 ? Buffer.concat([t.files[0].data.subarray(0, Math.floor(t.files[0].data.length * keepFraction / 262144) * 262144), Buffer.alloc(t.files[0].data.length - Math.floor(t.files[0].data.length * keepFraction / 262144) * 262144)]) : t.files[0].data);
    const wslPath = '/mnt/' + tmp[0].toLowerCase() + tmp.slice(2).replace(/\\/g, '/');
    docker('exec', '-u', 'root', `${project}-rtseed-1`, 'mkdir', '-p', '/downloads/seed');
    docker('cp', `${wslPath}/${t.name}`, `${project}-rtseed-1:/downloads/seed/`);
  } finally { rmSync(tmp, { recursive: true, force: true }); }
  docker('exec', '-u', 'root', `${project}-rtseed-1`, 'chown', '-R', 'rtorrent:rtorrent', '/downloads/seed');
  await seedRpc('load.raw_start', ['', { base64: t.bytes }, 'd.directory.set=/downloads/seed']);
}

const film = (name, megabytes) => makeTorrent(name, [{ path: [], data: randomBytes(megabytes * 1024 * 1024) }]);
const pack = (name, count, megabytes) => makeTorrent(name, Array.from({ length: count }, (_, i) => ({ path: [`${name}.S01E0${i + 1}.mkv`], data: randomBytes(megabytes * 1024 * 1024) })));

// ------------------------------------------------------------------ the run

async function main() {
  // The login for the mount: made here, set in the container only, and removed from every recording by exact value.
  const user = 'rec' + randomBytes(3).toString('hex');
  const password = randomBytes(9).toString('base64url');
  addSecret(user); addSecret(password);
  const inside = (...a) => docker('exec', '-u', 'root', `${project}-rtorrent-1`, 'sh', '-c', a.join(' '));
  inside(`htpasswd -bc /tmp/rpc.htpasswd '${user}' '${password}' 2>/dev/null`);
  inside(`sed -i 's|#auth_basic$|auth_basic \\"rTorrent\\";|; s|#auth_basic_user_file|auth_basic_user_file /tmp/rpc.htpasswd;|' /etc/nginx/conf.d/rpc.conf && nginx -s reload`);
  await sleep(1500);

  const login = `${user}:${password}`;
  const rt = makeClient(URLS.rtorrent, login);
  const seeder = makeClient(URLS.seed);
  const seedRpc = async (m, p) => (await seeder.rpc(m, p));
  const wrong = makeClient(URLS.rtorrent, `${user}:not-the-password`);
  const nobody = makeClient(URLS.rtorrent);

  const version = (await until('rTorrent to answer', 90, async () => { const e = await rt.rpc('system.client_version'); return e.reply.status === 200 ? e : null; }, 2000)).reply.bodyText.match(/<string>([^<]+)<\/string>/)[1];
  console.log('rTorrent ' + version);
  const list = () => rt.rpc('d.multicall2', ['', 'main', ...FIELDS]);
  const hashes = () => rt.rpc('download_list');
  const row = async (hash) => rowsOf(await list()).find((r) => r[0].toUpperCase() === hash.toUpperCase());
  const upper = (t) => t.infohash.toUpperCase();
  const files = (t) => rt.rpc('f.multicall', [upper(t), '', 'f.path=']);
  const clear = async () => { for (const h of rowsOf(await list()).map((r) => r[0])) await rt.rpc('d.erase', [h]); };
  const erase = async (t) => { await rt.rpc('d.erase', [upper(t)]); docker('exec', '-u', 'root', `${project}-rtorrent-1`, 'rm', '-rf', `/downloads/temp/${t.name}`); };

  await seedRpc('throttle.global_up.max_rate.set_kb', ['', 512]);
  await clear();

  // -------------------------------------------------------- version, nothing held, wrong login, not answering
  save('rtorrent', version, 'version', 'system.client_version: the plain version Deluno reads from the connection test.', [await rt.rpc('system.client_version')]);
  save('rtorrent', version, 'idle', 'd.multicall2 with Deluno\'s field list while rTorrent holds nothing, and download_list.', [await list(), await hashes()]);
  save('rtorrent', version, 'login-refused', 'The XML-RPC mount with a wrong password and with no login: nginx answers 401 with a basic-auth challenge.', [await wrong.rpc('system.client_version'), await nobody.rpc('d.multicall2', ['', 'main', ...FIELDS])]);
  save('rtorrent', version, 'not-answering', 'Nothing listening at the address: the connection is refused.', [await makeClient('http://127.0.0.1:1').rpc('system.client_version')]);

  // -------------------------------------------------------- the faults for a hash rTorrent never had
  {
    const zero = '0'.repeat(40);
    const unknown = [];
    for (const [m, p] of [['d.is_active', [zero]], ['d.pause', [zero]], ['d.resume', [zero]], ['d.erase', [zero]], ['d.check_hash', [zero]], ['d.name', [zero]], ['d.custom1', [zero]], ['d.custom1.set', [zero, 'recorded-movies']], ['d.base_path', [zero]], ['d.directory', [zero]], ['f.multicall', [zero, '', 'f.path=']], ['t.multicall', [zero, '', 't.is_enabled=', 't.message=']]])
      unknown.push(await rt.rpc(m, p));
    save('rtorrent', version, 'unknown-hash-fault', 'Every call Deluno makes about one torrent, for a hash rTorrent never had: each answers an XML-RPC fault (HTTP 200 with a <fault>).', unknown);
    save('rtorrent', version, 'removed-twice', 'd.erase of a hash rTorrent no longer holds (the second removal of the same download): a fault, not "done".', [await rt.rpc('d.erase', [zero]), await rt.rpc('d.name', [zero])]);
  }

  // -------------------------------------------------------- refused and dead adds
  {
    const garbage = await rt.rpc('load.raw_start', ['', { base64: Buffer.from('this is not a torrent') }]);
    save('rtorrent', version, 'add-refused', 'load.raw_start with bytes that are not a torrent, then the hashes rTorrent holds.', [await hashes(), garbage, await hashes()]);
    const dead = await rt.rpc('load.start', ['', LINK_BASE + 'Recorded.Nothing.Here.torrent']);
    await sleep(4000);
    save('rtorrent', version, 'add-url-dead', 'load.start of a link the server answers 404 for, then the hashes and the list (rTorrent adds nothing, and says nothing).', [await hashes(), dead, await hashes(), await list()]);
    const magnet = await rt.rpc('load.start', ['', 'magnet:?xt=urn:btih:' + 'ab'.repeat(20) + '&dn=Recorded.Magnet']);
    await sleep(3000);
    const mh = await hashes();
    save('rtorrent', version, 'magnet-no-metadata', 'load.start of a magnet link nobody can supply metadata for: the hashes before and after, and the list row while it waits.', [magnet, mh, await list()]);
    await rt.rpc('d.erase', ['AB'.repeat(20)]);
  }

  // -------------------------------------------------------- one film, from add to finished, with a client really seeding it
  const f = film('Recorded.Rtorrent.Film.2024.1080p.WEB-DL.mkv', 40);
  await publish(f); await seed(seedRpc, f);
  {
    const before = await hashes();
    const added = await rt.rpc('load.raw_start', ['', { base64: f.bytes }]);
    const after = await until('rTorrent to list the film', 30, async () => { const h = await hashes(); return h.reply.bodyText.toUpperCase().includes(upper(f)) ? h : null; }, 500);
    save('rtorrent', version, 'add-file', 'The torrent file sent with load.raw_start: download_list before and after (how Deluno finds the new hash), then the list row.', [before, added, after, await list()]);
    const dup = await rt.rpc('load.raw_start', ['', { base64: f.bytes }]);
    save('rtorrent', version, 'duplicate-add', 'The same torrent file sent again while rTorrent holds it: load.raw_start answers 0 and the hash list does not change.', [await hashes(), dup, await hashes()]);
    const down = await until('the film to download', 90, async () => { const e = await list(); const r = rowsOf(e).find((x) => x[0].toUpperCase() === upper(f)); return r && Number(r[7]) > 0 && r[4] === '0' ? e : null; }, 500);
    const name = (await rt.rpc('d.name', [upper(f)]));
    save('rtorrent', version, 'downloading', 'The list while the film downloads from the other client (a download rate above 0 and peers connected), with d.is_active and d.name.', [down, await rt.rpc('d.is_active', [upper(f)]), name, await files(f), await rt.rpc('t.multicall', [upper(f), '', 't.is_enabled=', 't.message='])]);

    await until('the film to finish', 240, async () => { const r = await row(f.infohash); return r && r[4] === '1' ? r : null; }, 1500);
    await sleep(1500);
    const fin = await list();
    save('rtorrent', version, 'finished-single-file', 'The finished film: the list row (complete 1), d.directory, d.base_path, d.custom1, f.multicall (one file) and t.multicall.', [fin, await rt.rpc('d.directory', [upper(f)]), await rt.rpc('d.base_path', [upper(f)]), await rt.rpc('d.custom1', [upper(f)]), await files(f), await rt.rpc('t.multicall', [upper(f), '', 't.is_enabled=', 't.message='])]);
    save('rtorrent', version, 'settings', 'directory.default and d.free_diskspace (the free space of a torrent\'s folder) as rTorrent answers them.', [await rt.rpc('directory.default'), await rt.rpc('d.free_diskspace', [upper(f)])]);
    const set = await rt.rpc('d.custom1.set', [upper(f), 'recorded-movies']);
    save('rtorrent', version, 'label-set', 'd.custom1.set (rTorrent\'s label field), then d.custom1 reads it back.', [await rt.rpc('d.custom1', [upper(f)]), set, await rt.rpc('d.custom1', [upper(f)])]);
    const recheck = await rt.rpc('d.check_hash', [upper(f)]);
    const during = await list();
    await sleep(3000);
    save('rtorrent', version, 'rechecked', 'd.check_hash of the finished torrent: its answer, the list row while it checks (d.is_hash_checking) and after.', [recheck, during, await list()]);
    const er = await rt.rpc('d.erase', [upper(f)]);
    save('rtorrent', version, 'removed-without-data', 'd.erase of a finished torrent: the answer and the list afterwards. rTorrent leaves the files where they were.', [er, await hashes(), await list()]);
    docker('exec', '-u', 'root', `${project}-rtorrent-1`, 'rm', '-rf', `/downloads/temp/${f.name}`);
  }

  // -------------------------------------------------------- paused and resumed (a torrent of its own: a resumed rTorrent does not find its peer again)
  {
    const pz = film('Recorded.Rtorrent.PauseTest.2024.1080p.WEB-DL.mkv', 40);
    await publish(pz); await seed(seedRpc, pz);
    await rt.rpc('load.raw_start', ['', { base64: pz.bytes }]);
    await until('the download to start', 60, async () => { const r = await row(pz.infohash); return r && Number(r[7]) > 0 ? r : null; }, 500);
    const pause = await rt.rpc('d.pause', [upper(pz)]);
    const active = await rt.rpc('d.is_active', [upper(pz)]);
    await sleep(1500);
    save('rtorrent', version, 'paused', 'd.pause of a downloading torrent, then d.is_active (0) and the list row (not complete).', [pause, active, await list()]);
    const resume = await rt.rpc('d.resume', [upper(pz)]);
    const active2 = await rt.rpc('d.is_active', [upper(pz)]);
    save('rtorrent', version, 'resumed', 'd.resume of the paused torrent, then d.is_active (1) and the list row.', [resume, active2, await list()]);
    await erase(pz);
  }

  // -------------------------------------------------------- added paused (load.raw), by link (load.start), and by link not started (load.normal)
  {
    const g = film('Recorded.Rtorrent.Paused.2024.1080p.WEB-DL.mkv', 4);
    await publish(g); await seed(seedRpc, g);
    const b = await hashes();
    const paused = await rt.rpc('load.raw', ['', { base64: g.bytes }]);
    await sleep(2000);
    save('rtorrent', version, 'add-file-paused', 'load.raw (loaded, not started): the hashes before and after and the list row (not active, nothing downloaded).', [b, paused, await hashes(), await list()]);
    await erase(g);
    const url = await rt.rpc('load.start', ['', g.link]);
    await until('the linked torrent to appear', 60, async () => { const h = await hashes(); return h.reply.bodyText.toUpperCase().includes(upper(g)) ? h : null; }, 1000);
    await until('the linked torrent to finish', 120, async () => { const r = await row(g.infohash); return r && r[4] === '1' ? r : null; }, 1500);
    save('rtorrent', version, 'add-url', 'A torrent added by its link with load.start (rTorrent fetches the file itself), the hashes after and the list once it finished.', [url, await hashes(), await list()]);
    await erase(g);
    const url2 = await rt.rpc('load.normal', ['', g.link]);
    await sleep(3000);
    save('rtorrent', version, 'add-url-paused', 'load.normal (a link, loaded without starting): the answer, the hashes and the list row.', [url2, await hashes(), await list()]);
    await erase(g);
  }

  // -------------------------------------------------------- a folder release, finished, with several files
  {
    const p = pack('Recorded.Rtorrent.Show.S01.1080p.WEB-DL', 3, 3);
    await publish(p); await seed(seedRpc, p);
    await rt.rpc('load.raw_start', ['', { base64: p.bytes }]);
    await until('the pack to finish', 180, async () => { const r = await row(p.infohash); return r && r[4] === '1' ? r : null; }, 1500);
    await sleep(1500);
    save('rtorrent', version, 'finished-folder', 'A release folder of three files, finished: the list row, d.directory, d.base_path and f.multicall (three files).', [await list(), await rt.rpc('d.directory', [upper(p)]), await rt.rpc('d.base_path', [upper(p)]), await files(p)]);
    await erase(p);
  }

  // -------------------------------------------------------- a tracker that refuses the torrent
  {
    const t = film('Recorded.Rtorrent.Refused.2024.1080p.WEB-DL.mkv', 4);
    await publish(t); await seed(seedRpc, t);
    await fetch(`${URLS.tracker}/fail/${t.infohash}`, { method: 'POST' });
    await rt.rpc('load.raw_start', ['', { base64: t.bytes }]);
    const msg = await until('the tracker\'s refusal in d.message', 90, async () => { const r = await row(t.infohash); return r && r[10] ? r : null; }, 2000);
    save('rtorrent', version, 'tracker-error', 'A torrent whose tracker answers "Registered torrents only": the list row carries it in d.message, and t.multicall carries it in t.message.', [await list(), await rt.rpc('t.multicall', [upper(t), '', 't.is_enabled=', 't.message=']), await rt.rpc('d.message', [upper(t)])]);
    await erase(t);
  }

  // -------------------------------------------------------- stalled: the one seeder holds only part of the film, so the download stops there
  {
    const h = film('Recorded.Rtorrent.Stalled.2024.1080p.WEB-DL.mkv', 16);
    await publish(h); await seed(seedRpc, h, { keepFraction: 0.7 });
    await rt.rpc('load.raw_start', ['', { base64: h.bytes }]);
    await until('the download to start', 60, async () => { const r = await row(h.infohash); return r && Number(r[5]) > 0 ? r : null; }, 500);
    await until('the download to stop moving', 90, async () => { const a = await row(h.infohash); await sleep(4000); const b = await row(h.infohash); return a && b && a[5] === b[5] && b[7] === '0' && b[4] === '0' ? b : null; }, 1000);
    const first = await list();
    await sleep(6000);
    save('rtorrent', version, 'stalled', 'The only seeder holds 70% of the film: the download reaches that and stops. The list row twice, 6 seconds apart (bytes done unchanged and below the size, download rate 0, the torrent still active, not complete).', [first, await list()]);
    save('rtorrent', version, 'stalled-trackers', 'The trackers of the stalled torrent asked for the fields rTorrent has (t.url, t.is_enabled, t.latest_event, t.failed_counter, t.success_counter).', [await rt.rpc('t.multicall', [upper(h), '', 't.url=', 't.is_enabled=', 't.latest_event=', 't.failed_counter=', 't.success_counter=']), await rt.rpc('d.is_active', [upper(h)])]);
    await erase(h);
  }

  // -------------------------------------------------------- a torrent nobody serves: queued/stalled with nothing to download
  {
    const n = film('Recorded.Rtorrent.NoPeers.2024.1080p.WEB-DL.mkv', 4);
    await rt.rpc('load.raw_start', ['', { base64: n.bytes }]);
    await sleep(8000);
    save('rtorrent', version, 'no-peers', 'A torrent whose tracker (not published) and peers do not exist: active, nothing done, no peers, no rate.', [await list(), await rt.rpc('t.multicall', [upper(n), '', 't.is_enabled=', 't.message='])]);
    await erase(n);
  }

  await seedRpc('throttle.global_up.max_rate.set_kb', ['', 0]);
  for (const h of rowsOf(await seeder.rpc('d.multicall2', ['', 'main', 'd.hash='])).map((r) => r[0])) await seeder.rpc('d.erase', [h]);
  console.log('done');
}

await main();

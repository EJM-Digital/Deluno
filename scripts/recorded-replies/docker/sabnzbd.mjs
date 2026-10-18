// Records what the real SABnzbd 5.1.3 answers when an add is refused (tests/fixtures/recorded/sabnzbd/5.1.3/add-*.json), from the image the e2e stack
// pins, in Docker (#1237). Deluno adds with `mode=addurl` and reads {"status": false, "error": ...}; a file add (addfile, what Sonarr and Radarr do)
// and a local file add are recorded beside it.
//
//   node scripts/recorded-replies/docker/sabnzbd.mjs        (the `sabnzbd` service of apps.compose.yaml running in the rec2 project)
import { Client, addSecret, replyJson, save, sleep } from './lib.mjs';

const KEY = 'e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0';
const base = `http://127.0.0.1:${process.env.REC2_SABNZBD_PORT ?? 38083}`;
addSecret(KEY);
const s = new Client(base);
const api = (mode, extra = {}, key = KEY) => s.get('/api?' + new URLSearchParams({ mode, output: 'json', apikey: key, ...extra }));
const addFile = (name, bytes, extra = {}) => s.send({ method: 'POST', path: '/api?' + new URLSearchParams({ mode: 'addfile', output: 'json', apikey: KEY, cat: '*', ...extra }), multipart: { name: { bytes, filename: name, type: 'application/x-nzb' } } });

const version = replyJson(await api('version')).version;
const note = (what) => `${what} (SABnzbd ${version} in Docker, the e2e stack's image and first settings file).`;
const queue = () => api('queue');
const history = () => api('history', { limit: '20' });
const clear = async () => { await api('queue', { name: 'delete', value: 'all', del_files: '1' }); await api('history', { name: 'delete', value: 'all', del_files: '1' }); };
await clear();

save('sabnzbd', version, 'add-no-url', note('mode=addurl with no name (no link at all), then with a name that is not a link'), [
  await api('addurl'), await api('addurl', { name: 'this is not a link', nzbname: 'Recorded.Not.A.Link', cat: '*' }), await queue(), await history()]);
save('sabnzbd', version, 'add-wrong-key', note('mode=addurl and mode=addfile with an API key SABnzbd does not know'), [
  await api('addurl', { name: 'http://tracker:8080/dl/x.nzb', nzbname: 'Recorded.Wrong.Key' }, 'not-the-key'), await s.send({ method: 'POST', path: '/api?' + new URLSearchParams({ mode: 'addfile', output: 'json', apikey: 'not-the-key' }), multipart: { name: { bytes: Buffer.from('<nzb/>'), filename: 'x.nzb', type: 'application/x-nzb' } } })]);

// A link nothing serves (404) and a link that answers 200 with something that is not an NZB: SABnzbd takes the job, fetches it and fails it.
{
  const dead = await api('addurl', { name: 'http://tracker:8080/dl/Recorded.Nothing.Here.nzb', nzbname: 'Recorded.Dead.Link', cat: '*' });
  const notNzb = await api('addurl', { name: 'http://tracker:8080/hooks', nzbname: 'Recorded.Not.An.Nzb', cat: '*' });
  await sleep(8000);
  save('sabnzbd', version, 'add-url-dead', note('mode=addurl with a link the server answers 404 for, and with a link that answers 200 with something that is not an NZB: SABnzbd accepts the add, then the queue and history after the fetch failed'), [dead, notNzb, await queue(), await history()]);
}

await clear();
// A file that is not an NZB, an empty file, and a file with no NZB root.
{
  const garbage = await addFile('Recorded.Garbage.nzb', Buffer.from('this is not an nzb'), { nzbname: 'Recorded.Garbage' });
  const empty = await addFile('Recorded.Empty.nzb', Buffer.alloc(0), { nzbname: 'Recorded.Empty' });
  const noRoot = await addFile('Recorded.Wrong.nzb', Buffer.from('<?xml version="1.0"?><html><body>hello</body></html>'), { nzbname: 'Recorded.Wrong.Root' });
  await sleep(4000);
  save('sabnzbd', version, 'add-file-refused', note('mode=addfile with a file that is not an NZB, an empty file and an XML file that is not an NZB: each answer, then the queue and history'), [garbage, empty, noRoot, await queue(), await history()]);
}

// A local path that does not exist.
save('sabnzbd', version, 'add-local-missing', note('mode=addlocalfile with a path SABnzbd does not hold'), [await api('addlocalfile', { name: '/downloads/nothing/Recorded.Missing.nzb', nzbname: 'Recorded.Missing' })]);
await clear();
console.log('done');

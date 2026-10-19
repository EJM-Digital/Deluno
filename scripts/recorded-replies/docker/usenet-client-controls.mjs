// Records what the real SABnzbd 5.1.3 and NZBGet 26.3 answer when Deluno pauses and resumes the whole client (not one download) and when it
// writes a news server into them (tests/fixtures/recorded/sabnzbd/5.1.3/client-*.json, usenet-provider-*.json and the same for nzbget/26.3),
// from the images the e2e stack pins, in Docker (#1237).
//
//   wsl: docker run -d --name rec-sab    -p 127.0.0.1:39083:8080 -e PUID=1000 -e PGID=1000 -v <copy of tests/e2e/sabnzbd/sabnzbd.ini>:/config/sabnzbd.ini lscr.io/linuxserver/sabnzbd@sha256:7a13af91...
//   wsl: docker run -d --name rec-nzbget -p 127.0.0.1:39084:6789 -e PUID=1654 -e PGID=1654 lscr.io/linuxserver/nzbget@sha256:ab18af1d...
//   node scripts/recorded-replies/docker/usenet-client-controls.mjs
//
// The news server given to each client is a name that never resolves (news.example.test): neither client reaches it, they only keep it.
import { Client, addSecret, replyJson, save, sleep } from './lib.mjs';

const KEY = 'e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0';
const PASSWORD = 'provider-pass-for-recording';
const HOST = 'news.example.test';
addSecret(KEY);
addSecret(PASSWORD);

// ---- SABnzbd
{
  const s = new Client(`http://127.0.0.1:${process.env.REC_SABNZBD_PORT ?? 39083}`);
  const api = (mode, extra = {}, key = KEY) => s.get('/api?' + new URLSearchParams({ mode, output: 'json', apikey: key, ...extra }));
  const version = replyJson(await api('version')).version;
  const note = (what) => `${what} (SABnzbd ${version} in Docker, the e2e stack's image and first settings file).`;

  await api('resume');
  save('sabnzbd', version, 'client-paused-and-resumed', note('The whole queue paused (mode=pause) and resumed (mode=resume), the queue read after each'), [
    await api('pause'), await api('queue'), await api('resume'), await api('queue')]);
  save('sabnzbd', version, 'client-resume-wrong-key', note('mode=resume with an API key SABnzbd does not know'), [await api('resume', {}, 'not-the-key')]);

  const servers = () => api('get_config', { section: 'servers' });
  const set = (key = KEY) => api('set_config', {
    section: 'servers', keyword: 'Deluno Usenet provider', displayname: 'Deluno Usenet provider', host: HOST, port: '563', username: 'recorder', password: PASSWORD,
    connections: '8', ssl: '1', enable: '1' }, key);
  const before = await servers();
  const given = await set();
  await sleep(1500);
  const after = await servers();
  save('sabnzbd', version, 'usenet-provider-set', note(`mode=get_config for the servers, then mode=set_config giving it a news server (${HOST}, port 563, SSL, 8 connections), then mode=get_config again: the server is there`), [before, given, after]);
  save('sabnzbd', version, 'usenet-provider-wrong-key', note('mode=set_config with an API key SABnzbd does not know, then mode=get_config with it'), [await set('not-the-key'), await servers().then(() => api('get_config', { section: 'servers' }, 'not-the-key'))]);
}

// ---- NZBGet
{
  const auth = 'Basic ' + Buffer.from('nzbget:tegbzn6789').toString('base64');
  const n = new Client(`http://127.0.0.1:${process.env.REC_NZBGET_PORT ?? 39084}`, { headers: { authorization: auth } });
  addSecret('tegbzn6789');
  let id = 100;
  const rpc = (method, params = []) => n.send({ method: 'POST', path: '/jsonrpc', json: { method, params, id: ++id } });
  const version = replyJson(await rpc('version')).result;
  const note = (what) => `${what} (NZBGet ${version} in Docker, the e2e stack's image and default settings).`;

  await rpc('resumedownload'); await rpc('resumepost'); await rpc('resumescan');
  save('nzbget', version, 'client-paused-and-resumed', note('The whole client paused (pausedownload, pausepost, pausescan) and resumed (resumedownload, resumepost, resumescan)'), [
    await rpc('pausedownload'), await rpc('pausepost'), await rpc('pausescan'), await rpc('resumedownload'), await rpc('resumepost'), await rpc('resumescan')]);

  // The plan Deluno writes (NzbGetServerPlan.Apply): the first server slot that still holds the placeholder host gets the news server.
  const loaded = await rpc('loadconfig');
  const options = replyJson(loaded).result.filter((option) => !['ConfigFile', 'AppBin', 'AppDir', 'Version'].includes(option.Name));
  const slot = Number(/^Server(\d+)\.Host$/i.exec(options.find((option) => /^Server\d+\.Host$/i.test(option.Name) && (!option.Value || option.Value === 'my.newsserver.com'))?.Name ?? 'Server1.Host')[1]);
  const values = {
    [`Server${slot}.Active`]: 'yes', [`Server${slot}.Name`]: 'Deluno Usenet provider', [`Server${slot}.Level`]: '0', [`Server${slot}.Host`]: HOST, [`Server${slot}.Port`]: '563',
    [`Server${slot}.Username`]: 'recorder', [`Server${slot}.Password`]: PASSWORD, [`Server${slot}.Encryption`]: 'yes', [`Server${slot}.Connections`]: '8' };
  const plan = options.map((option) => ({ Name: option.Name, Value: Object.hasOwn(values, option.Name) ? values[option.Name] : option.Value }));
  const saved = await rpc('saveconfig', [plan]);
  const reloaded = await rpc('reload');
  let after;
  for (let attempt = 0; attempt < 30; attempt++) {
    await sleep(1000);
    const read = await rpc('config');
    const found = replyJson(read)?.result?.some((option) => option.Name === `Server${slot}.Host` && option.Value === HOST);
    if (found) { after = read; break; }
  }
  if (!after) throw new Error('NZBGet did not show the news server after its reload.');
  save('nzbget', version, 'usenet-provider-set', note(`loadconfig, then saveconfig with the first free server slot given a news server (${HOST}, port 563, SSL, 8 connections), then reload, then config once NZBGet shows it`), [loaded, saved, reloaded, after]);
}
console.log('done');

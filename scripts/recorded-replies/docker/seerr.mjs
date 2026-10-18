// Records the real Seerr's replies to the calls Deluno makes (tests/fixtures/recorded/seerr/<version>/<state>.json) from its published image in
// Docker (#1237): status and settings, the request and user lists, the movie and TV lookups by TMDb id, a request pending, approved and declined,
// a media record marked available and deleted, and the webhook Seerr itself sends when a request is approved.
//
//   node scripts/recorded-replies/docker/seerr.mjs        (jellyfin, seerr and tracker running in the rec2 project, Jellyfin on a fresh volume)
//
// Seerr is set up the way a person does it: signed in with a Jellyfin administrator (made here), which makes the first Seerr administrator and
// gives the API key. A second, ordinary user (local, no automatic approval) makes the requests, so they begin as pending. The webhook goes to
// the tracker container, which keeps what arrives (tracker.mjs). Real titles are used (TMDb id 45745 for the film, a real series for TV).
import { randomBytes } from 'node:crypto';
import { Client, addSecret, addMissing, replyJson, save, sleep, until } from './lib.mjs';

const SEERR = `http://127.0.0.1:${process.env.REC2_SEERR_PORT ?? 35055}`;
const JELLYFIN = `http://127.0.0.1:${process.env.REC2_JELLYFIN_PORT ?? 38096}`;
const TRACKER = `http://127.0.0.1:${process.env.REC2_TRACKER_PORT ?? 38080}`;
const CLIENT_AUTH = 'Client="Deluno Recorder", Device="Recorder", DeviceId="deluno-recorder-seerr", Version="1.0"';
const FILM = 45745; // Sintel
const SHOW = 1396; // Breaking Bad: a real series Seerr knows from TMDb

async function main() {
  // ---- a Jellyfin administrator for Seerr to sign in with
  const jf = new Client(JELLYFIN);
  await until('Jellyfin', 240, async () => { const e = await jf.get('/System/Info/Public'); return e.reply.status === 200 && replyJson(e)?.Version ? e : null; }, 3000);
  await sleep(8000);
  const adminName = 'rec' + randomBytes(3).toString('hex'); const adminPass = randomBytes(9).toString('base64url');
  addSecret(adminName); addSecret(adminPass);
  if (replyJson(await jf.get('/System/Info/Public')).StartupWizardCompleted === true) throw new Error('Jellyfin is already set up; start it from a fresh volume');
  await jf.send({ method: 'POST', path: '/Startup/Configuration', json: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' } });
  await jf.get('/Startup/User');
  await jf.send({ method: 'POST', path: '/Startup/User', json: { Name: adminName, Password: adminPass } });
  await jf.send({ method: 'POST', path: '/Startup/Complete' });
  await sleep(3000);

  // ---- Seerr before it is set up
  const anon = new Client(SEERR, { keep: ['content-type'] });
  const status = await until('Seerr', 120, async () => { const e = await anon.get('/api/v1/status'); return e.reply.status === 200 ? e : null; }, 3000);
  const version = replyJson(status).version;
  console.log('seerr ' + version);
  const note = (what) => `${what} (Seerr ${version} in Docker, linked to a Jellyfin; titles from TMDb through Seerr's own key).`;
  const publicBefore = await anon.get('/api/v1/settings/public');
  save('seerr', version, 'status', note('/api/v1/status: the public status reply that carries the version'), [status]);
  save('seerr', version, 'not-set-up', note('Before the first sign-in: the public settings say it is not initialised, and the key-protected settings are refused'), [publicBefore, await anon.get('/api/v1/settings/main'), await anon.get('/api/v1/request?filter=all&sort=added&take=100&skip=0')]);

  // ---- setup: the Jellyfin administrator's sign-in makes the first administrator
  const session = new Client(SEERR, { keep: ['content-type'] });
  const setup = await session.send({ method: 'POST', path: '/api/v1/auth/jellyfin', json: { username: adminName, password: adminPass, hostname: 'jellyfin', port: 8096, useSsl: false, urlBase: '', email: 'admin@example.com', serverType: 2 } });
  console.log('  setup: ' + setup.reply.status + ' ' + (setup.reply.bodyText ?? '').slice(0, 200));
  if (setup.reply.status !== 200) { addMissing(`seerr: the Jellyfin sign-in that sets Seerr up answered ${setup.reply.status}`); return; }
  const main = replyJson(await session.get('/api/v1/settings/main'));
  const key = main?.apiKey;
  if (!key) { addMissing('seerr: no API key in /api/v1/settings/main after setup'); return; }
  addSecret(key);
  save('seerr', version, 'setup', note('The first sign-in (a Jellyfin administrator) that initialises Seerr, then the public settings'), [setup, await session.get('/api/v1/settings/public')]);
  // The setup also needs the "initialize" call that marks it finished.
  await session.send({ method: 'POST', path: '/api/v1/settings/initialize' });

  const c = new Client(SEERR, { keep: ['content-type'], headers: { 'x-api-key': key } });
  const get = (path, headers) => c.send({ method: 'GET', path, headers });
  const send = (method, path, json, headers) => c.send({ method, path, json, headers });
  save('seerr', version, 'settings-main', note('The key-protected settings Deluno reads to prove its key (applicationTitle)'), [await get('/api/v1/settings/main')]);
  save('seerr', version, 'wrong-key', note('The settings, the request list and the user list with a key Seerr does not know'), [
    await c.send({ path: '/api/v1/settings/main', headers: { 'x-api-key': 'not-the-key' } }), await c.send({ path: '/api/v1/request?filter=all&sort=added&take=100&skip=0', headers: { 'x-api-key': 'not-the-key' } }), await c.send({ path: '/api/v1/user?sort=displayname&take=100&skip=0', headers: { 'x-api-key': 'not-the-key' } })]);
  save('seerr', version, 'requests-empty', note('The request, user and media lists as Deluno pages them, with no request yet'), [
    await get('/api/v1/request?filter=all&sort=added&take=100&skip=0'), await get('/api/v1/user?sort=displayname&take=100&skip=0'), await get('/api/v1/media?filter=all&sort=added&take=100&skip=0')]);
  save('seerr', version, 'lookup-unknown', note('A movie and a series looked up by a TMDb id with no Seerr record: mediaInfo is absent'), [await get(`/api/v1/movie/${FILM}`), await get(`/api/v1/tv/${SHOW}`), await get('/api/v1/movie/999999999')]);

  // ---- a second, ordinary user who makes the requests
  const person = 'rec' + randomBytes(3).toString('hex');
  const made = await send('POST', '/api/v1/user', { email: `${person}@example.com`, username: person, password: randomBytes(9).toString('base64url') });
  addSecret(person);
  const userId = replyJson(made)?.id;
  if (!userId) { addMissing(`seerr: an ordinary user could not be made (POST /api/v1/user answered ${made.reply.status}: ${(made.reply.bodyText ?? '').slice(0, 200)})`); }
  const asUser = { 'x-api-user': String(userId) };

  // ---- the webhook agent pointed at the tracker container, with Seerr's own default payload
  await fetch(`${TRACKER}/hooks`, { method: 'DELETE' });
  const payload = JSON.stringify({
    notification_type: '{{notification_type}}', event: '{{event}}', subject: '{{subject}}', message: '{{message}}', image: '{{image}}',
    '{{media}}': { media_type: '{{media_type}}', tmdbId: '{{media_tmdbid}}', tvdbId: '{{media_tvdbid}}', status: '{{media_status}}', status4k: '{{media_status4k}}' },
    '{{request}}': { request_id: '{{request_id}}', requestedBy_email: '{{requestedBy_email}}', requestedBy_username: '{{requestedBy_username}}', requestedBy_avatar: '{{requestedBy_avatar}}' },
    '{{extra}}': []
  }, null, 2);
  const webhookSettings = await send('POST', '/api/v1/settings/notifications/webhook', { enabled: true, types: 4094, options: { webhookUrl: 'http://tracker:8080/hook/seerr', authHeader: 'recorded-webhook-secret', jsonPayload: payload } });
  console.log('  webhook settings: ' + webhookSettings.reply.status);
  addSecret('recorded-webhook-secret');
  const hooks = async () => (await (await fetch(`${TRACKER}/hooks`)).json());
  // What Seerr sent, exactly (its headers, its JSON body). The reply is the receiver's plain acknowledgment, not Deluno's: a test posts the request.
  const saveHooks = (state, what, list) => list.length
    ? save('seerr', version, state, note(what + '. The request is what Seerr itself sent to the webhook address; the reply is the receiving container plain ok, not an answer from Deluno'),
      list.map((h) => ({ request: { method: h.method, path: '/api/seerr-servers/{id}/webhook', headers: { 'content-type': h.headers['content-type'] ?? '', authorization: h.headers.authorization ?? '', 'user-agent': h.headers['user-agent'] ?? '' }, bodyText: h.body }, reply: { status: 200, headers: { 'content-type': 'text/plain' }, bodyText: 'ok' } })))
    : addMissing(`seerr ${state}: the webhook never arrived`);
  const test = await send('POST', '/api/v1/settings/notifications/webhook/test', { enabled: true, types: 4094, options: { webhookUrl: 'http://tracker:8080/hook/seerr', authHeader: 'recorded-webhook-secret', jsonPayload: payload } });
  console.log('  webhook test: ' + test.reply.status + ' ' + (test.reply.bodyText ?? '').slice(0, 120));
  await sleep(3000);
  saveHooks('webhook-test', 'The webhook Seerr sends from its own "test" button (a test notification, which Deluno must acknowledge and ignore)', await hooks());
  await fetch(`${TRACKER}/hooks`, { method: 'DELETE' });

  if (userId) {
    // ---- a request pending, then approved, then another declined
    const req = await send('POST', '/api/v1/request', { mediaType: 'movie', mediaId: FILM }, asUser);
    console.log('  request: ' + req.reply.status + ' ' + (req.reply.bodyText ?? '').slice(0, 150));
    const requestId = replyJson(req)?.id;
    await sleep(3000);
    saveHooks('webhook-pending', 'The webhook Seerr sends when an ordinary user requests a film (MEDIA_PENDING), which Deluno must acknowledge and not act on', await hooks());
    await fetch(`${TRACKER}/hooks`, { method: 'DELETE' });
    save('seerr', version, 'request-pending', note('An ordinary user\'s request for a film, then the request list, the user list and the film\'s lookup (now with a mediaInfo)'), [
      req, await get('/api/v1/request?filter=all&sort=added&take=100&skip=0'), await get('/api/v1/user?sort=displayname&take=100&skip=0'), await get(`/api/v1/movie/${FILM}`), await get('/api/v1/media?filter=all&sort=added&take=100&skip=0')]);
    if (requestId) {
      const approve = await send('POST', `/api/v1/request/${requestId}/approve`);
      await sleep(3000);
      saveHooks('webhook-approved', 'The webhook Seerr sends when a request is approved (MEDIA_APPROVED): what Deluno\'s webhook route reads to add the title', await hooks());
      await fetch(`${TRACKER}/hooks`, { method: 'DELETE' });
      const mediaInfo = replyJson(await get(`/api/v1/movie/${FILM}`))?.mediaInfo;
      save('seerr', version, 'request-approved', note('The request approved (POST /api/v1/request/{id}/approve), then the request list and the film\'s lookup'), [approve, await get('/api/v1/request?filter=all&sort=added&take=100&skip=0'), await get(`/api/v1/movie/${FILM}`)]);
      const mediaId = mediaInfo?.id;
      if (mediaId) {
        const available = await send('POST', `/api/v1/media/${mediaId}/available`);
        save('seerr', version, 'media-available', note('The media record marked available (POST /api/v1/media/{id}/available), then the media list'), [available, await get('/api/v1/media?filter=all&sort=added&take=100&skip=0'), await get(`/api/v1/movie/${FILM}`)]);
        const deleted = await send('DELETE', `/api/v1/media/${mediaId}`);
        save('seerr', version, 'media-deleted', note('The media record deleted (DELETE /api/v1/media/{id}) with its requests, then the request and media lists, and the same delete again'), [deleted, await get('/api/v1/request?filter=all&sort=added&take=100&skip=0'), await get('/api/v1/media?filter=all&sort=added&take=100&skip=0'), await send('DELETE', `/api/v1/media/${mediaId}`)]);
      } else addMissing('seerr media-available/media-deleted: the film had no mediaInfo.id after its request');
    }
    // A series request, declined.
    const tvReq = await send('POST', '/api/v1/request', { mediaType: 'tv', mediaId: SHOW, seasons: [1] }, asUser);
    const tvId = replyJson(tvReq)?.id;
    if (tvId) {
      const decline = await send('POST', `/api/v1/request/${tvId}/decline`);
      await sleep(3000);
      saveHooks('webhook-declined', 'The webhook Seerr sends when a request is declined (MEDIA_DECLINED)', await hooks());
      save('seerr', version, 'request-declined-tv', note('A series request, declined, then the request list and the series lookup'), [tvReq, decline, await get('/api/v1/request?filter=all&sort=added&take=100&skip=0'), await get(`/api/v1/tv/${SHOW}`)]);
    }
  }
  // An administrator's own request is approved at once (auto-approval): the webhook then carries MEDIA_AUTO_APPROVED.
  await fetch(`${TRACKER}/hooks`, { method: 'DELETE' });
  const own = await send('POST', '/api/v1/request', { mediaType: 'movie', mediaId: 603 });
  await sleep(3000);
  saveHooks('webhook-auto-approved', 'The webhook Seerr sends for an administrator\'s own request, approved at once (MEDIA_AUTO_APPROVED)', await hooks());
  save('seerr', version, 'request-auto-approved', note('An administrator\'s own request for a film, approved at once, then the request list'), [own, await get('/api/v1/request?filter=all&sort=added&take=100&skip=0')]);
  // A payload that asks for the media id of Seerr ({{media_id}}): Seerr 3.5.0 sends the text unchanged, so a webhook cannot carry it.
  await fetch(`${TRACKER}/hooks`, { method: 'DELETE' });
  const probePayload = JSON.stringify({ notification_type: '{{notification_type}}', event: '{{event}}', '{{media}}': { media_type: '{{media_type}}', tmdbId: '{{media_tmdbid}}', media_id: '{{media_id}}', id: '{{id}}' } });
  await send('POST', '/api/v1/settings/notifications/webhook', { enabled: true, types: 4094, options: { webhookUrl: 'http://tracker:8080/hook/seerr', authHeader: 'recorded-webhook-secret', jsonPayload: probePayload } });
  await send('POST', '/api/v1/request', { mediaType: 'movie', mediaId: 550 });
  await sleep(3000);
  saveHooks('webhook-media-id-not-substituted', 'A webhook payload that asks for the media id of Seerr with the placeholders media_id and id: Seerr leaves the text as it is, so only the TMDb and TVDb ids can reach a receiver', await hooks());
  save('seerr', version, 'not-answering', 'A request sent to a port nothing listens on: the connection is refused.', [await new Client('http://127.0.0.1:1').get('/api/v1/status')]);
}

await main();
console.log('done');

// Records the real replies of the two self-hosted notification receivers Deluno delivers to (tests/fixtures/recorded/<app>/<version>/<state>.json)
// from their published images in Docker (#1296): ntfy (a topic, with and without an access token) and Gotify (an application's message).
//
//   node scripts/recorded-replies/docker/notify.mjs
//
// It starts nothing itself. It expects, on this machine's loopback (Docker in WSL, published ports):
//   ntfy, open:        docker run -d --name cove-ntfy -p 127.0.0.1:38580:80 binwiederhier/ntfy:latest serve
//   ntfy, with a user: docker run -d --name cove-ntfy-auth -p 127.0.0.1:38582:80 -e NTFY_AUTH_FILE=/var/lib/ntfy/user.db
//                        -e NTFY_AUTH_DEFAULT_ACCESS=deny-all binwiederhier/ntfy:latest serve
//   Gotify:            docker run -d --name cove-gotify -p 127.0.0.1:38581:80 -e GOTIFY_DEFAULTUSER_NAME=admin -e GOTIFY_DEFAULTUSER_PASS=admin gotify/server:latest
// The requests are the ones Deluno sends (OutboundNotificationService: BuildNtfyRequest and BuildGotifyRequest): the same method, address, headers and
// body, with the film Sintel as the title. Each reply is what the real app answered; the request headers that matter are kept on the recording so a test
// can hold Deluno's own request against the one the real app accepted. Tokens made here are removed from the recordings by exact value.
import { execSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { Client, addSecret, replyJson, save, until } from './lib.mjs';

const NTFY = `http://127.0.0.1:${process.env.REC_NTFY_PORT ?? 38580}`;
const NTFY_AUTH = `http://127.0.0.1:${process.env.REC_NTFY_AUTH_PORT ?? 38582}`;
const GOTIFY = `http://127.0.0.1:${process.env.REC_GOTIFY_PORT ?? 38581}`;
const wsl = (command) => execSync(`wsl -e bash -lc "${command.replace(/"/g, '\\"')}"`, { encoding: 'utf8' }).trim();

const TITLE = 'Import completed: Sintel (2010)';
const MESSAGE = 'Sintel (2010) was imported from Sintel.2010.WEB.1080p using copy.';

/** Keeps the request headers a test compares (never an Authorization value: a token is a secret). */
function withHeaders(exchange, headers) {
  exchange.request.headers = headers;
  return exchange;
}

async function ntfy() {
  const version = wsl('docker exec cove-ntfy ntfy --version').split(/\s+/).find((part) => /^\d+\.\d+\.\d+/.test(part.replace(/^v/, ''))).replace(/^v/, '');
  console.log('ntfy ' + version);
  const note = (what) => `${what} (ntfy ${version} in Docker).`;
  const open = new Client(NTFY, { keep: ['content-type'] });
  await until('ntfy', 60, async () => (await open.get('/v1/health')).reply.status === 200, 2000);
  const topic = 'deluno-' + randomBytes(3).toString('hex');

  // The request Deluno's BuildNtfyRequest makes: POST the topic address, the message as text/plain, the title in the Title header.
  const plain = (client, path, headers) => client.send({ method: 'POST', path, text: MESSAGE, contentType: 'text/plain; charset=utf-8', headers: { title: TITLE, ...headers } });
  const sent = withHeaders(await plain(open, `/${topic}`), { 'content-type': 'text/plain; charset=utf-8', title: TITLE });
  const held = await open.get(`/${topic}/json?poll=1`);
  save('ntfy', version, 'delivered', note('A message published to a topic the way Deluno sends one (POST, text body, Title header), then the topic read back to show what ntfy kept'), [sent, held]);

  // A title with an accent cannot travel raw in an HTTP header; ntfy reads an RFC 2047 encoded one ("=?UTF-8?B?...?=") and keeps the real characters.
  const accented = 'Import completed: Amélie (2001)';
  const encoded = `=?UTF-8?B?${Buffer.from(accented, 'utf8').toString('base64')}?=`;
  const topic2 = topic + '-accent';
  const sentEncoded = withHeaders(await open.send({ method: 'POST', path: `/${topic2}`, text: MESSAGE, contentType: 'text/plain; charset=utf-8', headers: { title: encoded } }), { 'content-type': 'text/plain; charset=utf-8', title: encoded });
  const heldEncoded = await open.get(`/${topic2}/json?poll=1`);
  save('ntfy', version, 'title-encoded', note('A message whose Title header is the RFC 2047 encoding of a title with an accent, then the topic read back: ntfy keeps the accent'), [sentEncoded, heldEncoded]);

  // ---- a server that wants a login: an access token is what Deluno sends as a Bearer value.
  const auth = new Client(NTFY_AUTH, { keep: ['content-type'] });
  await until('ntfy with a user', 60, async () => (await auth.get('/v1/health')).reply.status === 200, 2000);
  const password = randomBytes(9).toString('base64url');
  addSecret(password);
  wsl(`docker exec -e NTFY_PASSWORD=${password} cove-ntfy-auth ntfy user add --role=admin deluno || true`);
  const tokenLine = wsl('docker exec cove-ntfy-auth ntfy token add deluno');
  const token = /tk_[a-z0-9]+/.exec(tokenLine)?.[0];
  if (!token) throw new Error('ntfy made no access token: ' + tokenLine);
  addSecret(token);
  const topic3 = topic + '-auth';
  const withToken = withHeaders(await plain(auth, `/${topic3}`, { authorization: `Bearer ${token}` }), { 'content-type': 'text/plain; charset=utf-8', title: TITLE });
  const withoutToken = withHeaders(await plain(auth, `/${topic3}`), { 'content-type': 'text/plain; charset=utf-8', title: TITLE });
  const wrongToken = withHeaders(await plain(auth, `/${topic3}`, { authorization: 'Bearer tk_wrongwrongwrongwrongwrongwrong1' }), { 'content-type': 'text/plain; charset=utf-8', title: TITLE });
  save('ntfy', version, 'access-token', note('A server that refuses everyone without a login: a message with a valid access token as a Bearer value (accepted), with none (refused) and with a wrong one (refused)'), [withToken, withoutToken, wrongToken]);
}

async function gotify() {
  const anon = new Client(GOTIFY, { keep: ['content-type'] });
  const info = await until('Gotify', 90, async () => { const e = await anon.get('/version'); return e.reply.status === 200 ? e : null; }, 2000);
  const version = replyJson(info).version;
  console.log('gotify ' + version);
  const note = (what) => `${what} (Gotify ${version} in Docker, the default administrator).`;
  const admin = new Client(GOTIFY, { keep: ['content-type'], headers: { authorization: 'Basic ' + Buffer.from('admin:admin').toString('base64') } });
  const made = await admin.send({ method: 'POST', path: '/application', json: { name: 'Deluno', description: 'Deluno notifications' } });
  const token = replyJson(made)?.token;
  if (!token) throw new Error('Gotify made no application token: ' + made.reply.bodyText);
  addSecret(token);
  // The request Deluno's BuildGotifyRequest makes: POST {server}/message, the X-Gotify-Key header, JSON { title, message, priority }.
  const body = { title: TITLE, message: MESSAGE, priority: 5 };
  const message = (key) => new Client(GOTIFY, { keep: ['content-type'] }).send({ method: 'POST', path: '/message', json: body, headers: key ? { 'x-gotify-key': key } : {} });
  const delivered = withHeaders(await message(token), { 'content-type': 'application/json' });
  save('gotify', version, 'delivered', note('A message pushed with an application\'s token the way Deluno sends one (POST /message, X-Gotify-Key, JSON title, message, priority)'), [delivered]);
  const wrong = withHeaders(await message('A' + randomBytes(12).toString('hex')), { 'content-type': 'application/json' });
  const none = withHeaders(await message(null), { 'content-type': 'application/json' });
  save('gotify', version, 'refused', note('The same message with a key Gotify does not know, and with no key at all: both are refused'), [wrong, none]);
}

await ntfy();
await gotify();

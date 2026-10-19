// Records what a real ntfy server answers when Deluno publishes a notification to it (tests/fixtures/recorded/ntfy/<version>/*.json), from
// the published image, in Docker. Deluno's ntfy provider (OutboundNotificationService.BuildNtfyRequest) posts the message as plain text to
// {server}/{topic} with the title in the Title header, and a bearer token when one is set. Two servers are used: an open one, and one started
// with `NTFY_AUTH_DEFAULT_ACCESS=deny-all` (nobody may publish without an account).
//
//   wsl: docker run -d --name rec-ntfy        -p 127.0.0.1:39080:80 binwiederhier/ntfy serve
//   wsl: docker run -d --name rec-ntfy-locked -p 127.0.0.1:39081:80 -e NTFY_AUTH_FILE=/tmp/auth.db -e NTFY_AUTH_DEFAULT_ACCESS=deny-all binwiederhier/ntfy serve
//   REC_NTFY_VERSION=<ntfy --version of the image> node scripts/recorded-replies/docker/ntfy.mjs
import { Client, save } from './lib.mjs';

const version = process.env.REC_NTFY_VERSION;
if (!version) throw new Error('Set REC_NTFY_VERSION to the version `ntfy --version` prints in the container.');
const open = new Client(`http://127.0.0.1:${process.env.REC_NTFY_PORT ?? 39080}`);
const locked = new Client(`http://127.0.0.1:${process.env.REC_NTFY_LOCKED_PORT ?? 39081}`);

// What Deluno's "Send a test notification" button sends.
const title = 'Deluno Webhook Test';
const message = 'This is a test notification from Deluno. Your webhook is configured correctly.';
const publish = (client, headers = {}) => client.send({ method: 'POST', path: '/deluno-test', text: message, contentType: 'text/plain; charset=utf-8', headers: { title, ...headers } });

save('ntfy', version, 'publish-ok', 'POST /{topic} with the message as plain text and the title in the Title header, to a server anyone may publish to: 200 with the event it stored.', [await publish(open)]);
save('ntfy', version, 'publish-refused', 'The same publish to a server where nobody may publish without an account: 403 forbidden.', [await publish(locked)]);
save('ntfy', version, 'publish-wrong-token', 'The same publish with a bearer token the server does not know, to the server that needs an account: 401 unauthorized.', [await publish(locked, { authorization: 'Bearer tk_notatokenthatexists00000000000' })]);
console.log('done');

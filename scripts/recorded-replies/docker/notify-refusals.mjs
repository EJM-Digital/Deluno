// Records what Telegram and Pushover really answer to a bot token or application token they do not know: the calls
// OutboundNotificationService makes to api.telegram.org and api.pushover.net, tests/fixtures/recorded/<app>/<date>/*.json (#1301).
//
//   node scripts/recorded-replies/docker/notify-refusals.mjs
//
// No account is made and no real token is used: every call carries a made-up token, which is what a wrong one looks like to the service.
import { execFileSync } from 'node:child_process';
import { Client, addSecret, save } from './lib.mjs';

const day = new Date().toISOString().slice(0, 10);
const agent = { 'user-agent': 'Deluno-test-recording', accept: 'application/json' };
const wrongToken = 'wrongtokenrecordedbydelunotests';
const wrongBot = `123456:${wrongToken}`;
addSecret(wrongToken);
addSecret(wrongBot);
const keep = ['content-type', 'retry-after'];

const telegram = new Client('https://api.telegram.org', { headers: agent, keep });
let telegramSend = await telegram.send({ method: 'POST', path: `/bot${wrongBot}/sendMessage`, json: { chat_id: '42', text: 'Import completed: Sintel (2010)' } });
if (telegramSend.reply.status === 0) {
  // Node's own connection to Telegram's address can time out where curl's reaches it: ask the same call through curl.
  const out = execFileSync('curl', ['-s', '-m', '20', '-i', '-X', 'POST', `https://api.telegram.org/bot${wrongBot}/sendMessage`, '-H', 'content-type: application/json', '-d', JSON.stringify({ chat_id: '42', text: 'Import completed: Sintel (2010)' })], { encoding: 'utf8' });
  const split = out.indexOf('\r\n\r\n');
  const head = out.slice(0, split);
  const status = Number(head.match(/^HTTP\/\S+ (\d+)/)[1]);
  const type = head.match(/^content-type: (.*)$/im)?.[1]?.trim();
  telegramSend = { request: telegramSend.request, reply: { status, headers: type ? { 'content-type': type } : {}, bodyText: out.slice(split + 4) } };
}
save('telegram', day, 'wrong-token', 'A message sent through a bot token Telegram does not know: its own refusal.', [telegramSend]);

const pushover = new Client('https://api.pushover.net', { headers: agent, keep });
const pushoverSend = await pushover.send({ method: 'POST', path: '/1/messages.json', form: { token: wrongToken, user: wrongToken, title: 'Import completed', message: 'Sintel (2010)' } });
save('pushover', day, 'wrong-token', 'A message sent with an application token and user key Pushover does not know: its own refusal.', [pushoverSend]);

console.log('done', telegramSend.reply.status, pushoverSend.reply.status);

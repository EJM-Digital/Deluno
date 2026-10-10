import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { tidyRecording, exposurePatterns, scrubText, scrubValue, scrubRecording, findLeaks, scrubbedValue } from './scrub.mjs';

// Built from pieces so this file itself carries no private detail.
const lan = '10' + '.1.1.' + '51';
const nas = 'storage' + '-city';
const rig = 'WIN-MJCOV' + 'DR5C88';
const pc = 'SPARE' + '-PC';
const labPassword = 'Deluno-Lab-' + '2026';

test('the maintainer network patterns are the ones check-public-exposure.ps1 carries', () => {
  const ps1 = readFileSync(new URL('../check-public-exposure.ps1', import.meta.url), 'utf8');
  const literals = [...ps1.matchAll(/\(\s*'([^']*)'\s*\+\s*'([^']*)'\s*\)/g)].map((m) => m[1] + m[2]);
  assert.ok(literals.length >= 5, 'found the exposure script patterns');
  for (const literal of literals) {
    const covered = exposurePatterns.some((p) => p.regex.source.startsWith(literal));
    assert.ok(covered, `scrub.mjs has the exposure pattern ${literal}`);
  }
});

test('addresses, names, passwords and e-mail addresses are replaced', () => {
  const text = `host ${lan}, share ${nas}, box ${rig}, pc ${pc}, pw ${labPassword}, mail me@corp.org, ok noreply@example.com`;
  const out = scrubText(text);
  assert.deepEqual(findLeaks(out), []);
  assert.match(out, /192\.168\.1\.10/);
  assert.match(out, /noreply@example\.com/);
  assert.ok(!out.includes('corp.org'));
});

test('other private addresses go, loopback and the placeholder stay', () => {
  const out = scrubText('a 192.168.7.8 b 172.20.1.2 c 10.9.8.7 d 127.0.0.1 e 192.168.1.10 f 8.8.8.8');
  assert.equal(out, 'a 192.168.1.10 b 192.168.1.10 c 192.168.1.10 d 127.0.0.1 e 192.168.1.10 f 8.8.8.8');
});

test('secret values are removed by key name wherever they sit', () => {
  const out = scrubValue({ apikey: 'abc123', nested: { Password: 'hunter2', list: [{ token: 't' }] }, name: 'keep me', headers: { 'Set-Cookie': 'SID=abc; path=/' } });
  assert.equal(out.apikey, scrubbedValue);
  assert.equal(out.nested.Password, scrubbedValue);
  assert.equal(out.nested.list[0].token, scrubbedValue);
  assert.equal(out.headers['Set-Cookie'], scrubbedValue);
  assert.equal(out.name, 'keep me');
});

test('secrets in a query string, form body or cookie text are removed', () => {
  const out = scrubText('/api?mode=queue&apikey=deadbeef&output=json and username=bob&password=xyzzy SID=zzz; Bearer abcdefghijkl');
  assert.ok(!/deadbeef|bob|xyzzy|zzz;|abcdefghijkl/.test(out), out);
  assert.match(out, /mode=queue/);
  assert.match(out, /output=json/);
});

test('profile folders and network shares are anonymised', () => {
  assert.equal(scrubText('C:\\Users\\james\\Downloads'), 'C:\\Users\\<user>\\Downloads');
  assert.equal(scrubText('\\\\mynas\\media\\films'), '\\\\<nas>\\media\\films');
  assert.equal(scrubText('/home/james/data'), '/home/<user>/data');
  assert.equal(scrubText('C:\\\\Users\\\\james\\\\x'), 'C:\\\\Users\\\\<user>\\\\x');
});

test('a name passed in is removed, and JSON text in a body is scrubbed by key', () => {
  const recording = {
    app: 'x', version: '1', state: 's',
    exchanges: [{ request: { method: 'POST', path: '/rpc', bodyText: '{"password":"p","host":"VMBOX01"}' }, reply: { status: 200, body: { path: 'D:\\VMBOX01\\x' } } }]
  };
  const out = scrubRecording(recording, ['VMBOX01']);
  const text = JSON.stringify(out);
  assert.ok(!text.includes('VMBOX01'));
  assert.ok(!text.includes('\\"p\\"'));
  assert.deepEqual(findLeaks(text, ['VMBOX01']), []);
});

test('scrubbing twice changes nothing, and a scrubbed text has no leaks', () => {
  const once = scrubText(`${lan} apikey=abc C:\\Users\\bob\\x \\\\box\\share`);
  assert.equal(scrubText(once), once);
  assert.deepEqual(findLeaks(once), []);
});

test('a form body that starts with a secret is scrubbed too', () => {
  const out = scrubText('username=admin&password=hunter22');
  assert.ok(!/admin|hunter22/.test(out), out);
});

test('JSON that arrived as text is stored as JSON, except where a number would not survive', () => {
  const tidy = tidyRecording({ exchanges: [{ request: { method: 'GET', path: '/' }, reply: { status: 200, bodyText: '{"a":1}' } },
    { request: { method: 'GET', path: '/' }, reply: { status: 200, bodyText: '{"size":12345678901234567890}' } },
    { request: { method: 'GET', path: '/' }, reply: { status: 200, bodyText: 'Ok.' } }] });
  assert.deepEqual(tidy.exchanges[0].reply.body, { a: 1 });
  assert.equal(tidy.exchanges[0].reply.bodyText, undefined);
  assert.equal(tidy.exchanges[1].reply.bodyText, '{"size":12345678901234567890}');
  assert.equal(tidy.exchanges[2].reply.bodyText, 'Ok.');
});

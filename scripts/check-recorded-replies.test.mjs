import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { check, innerReplies, literals } from './check-recorded-replies.mjs';

function repo(files, allow) {
  const root = mkdtempSync(join(tmpdir(), 'recorded-rule-'));
  for (const [name, text] of Object.entries(files)) {
    const full = join(root, name);
    mkdirSync(join(full, '..'), { recursive: true });
    writeFileSync(full, text);
  }
  if (allow) writeFileSync(join(root, 'tests', 'recorded-reply-allow-list.json'), JSON.stringify({ entries: allow }));
  return root;
}

const inline = (json) => `class T { void M() { var x = ${json}; } }`;

test('each kind of C# string is read, and each app\'s signature is caught', () => {
  const cases = [
    ['regular', '"{\\"nzo_id\\":\\"SABnzbd_nzo_1\\"}"', 'SABnzbd'],
    ['verbatim', '@"{ ""NZBID"": 3 }"', 'NZBGet'],
    ['raw', '"""{ "hash": "abc", "state": "downloading" }"""', 'qBittorrent'],
    ['interpolated', '$"{{\\"percentDone\\": {value}}}"', 'Transmission'],
    ['deluge', '"{\\"result\\": {\\"total_done\\": 1}}"', 'Deluge'],
    ['weir', '"""{ "code": "handoff_ended" }"""', 'Weir']
  ];
  for (const [name, literal, app] of cases) {
    const found = innerReplies(inline(literal));
    assert.equal(found[0]?.app, app, `${name} literal is caught as ${app}`);
  }
});

test('code that is not a reply is left alone', () => {
  assert.deepEqual(innerReplies(inline('"hash"')), []);
  assert.deepEqual(innerReplies('// "nzo_id" in a comment\nclass T { char c = \'"\'; string s = "plain"; }'), []);
  assert.deepEqual(innerReplies(inline('RecordedReplies.Load("sabnzbd", "5.1.3", "queued")')), []);
  assert.equal([...literals('var a = "one"; var b = @"two ""x"" two";')].length, 2);
});

test('a test that builds a reply inline fails, naming the file and where to load it from', () => {
  const root = repo({ 'tests/A.Tests/ReplyTests.cs': inline('"{\\"nzo_id\\":\\"x\\"}"') });
  try {
    const { problems } = check(root);
    assert.equal(problems.length, 1);
    assert.match(problems[0], /ReplyTests\.cs:1: builds SABnzbd's reply inline/);
    assert.match(problems[0], /tests\/fixtures\/recorded/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('a test that loads a recording passes, and the recordings folder is not scanned', () => {
  const root = repo({
    'tests/A.Tests/ReplyTests.cs': inline('RecordedReplies.Load("sabnzbd", "5.1.3", "queued")'),
    'tests/fixtures/RecordedReplies.cs': inline('"{\\"nzo_id\\":\\"x\\"}"')
  });
  try { assert.deepEqual(check(root).problems, []); } finally { rmSync(root, { recursive: true, force: true }); }
});

test('an allow-listed file passes; a deliberate entry needs a reason; a stale entry fails', () => {
  const files = { 'tests/A.Tests/Truncated.cs': inline('"{\\"nzo_id\\": "'), 'tests/A.Tests/Clean.cs': inline('"nothing"') };
  let root = repo(files, [{ file: 'tests/A.Tests/Truncated.cs', kind: 'deliberate', reason: 'A reply cut off mid-way, which no real SABnzbd sends.' }]);
  try { assert.deepEqual(check(root).problems, []); } finally { rmSync(root, { recursive: true, force: true }); }

  root = repo(files, [{ file: 'tests/A.Tests/Truncated.cs', kind: 'deliberate', reason: '' }]);
  try { assert.match(check(root).problems.join('\n'), /gives no reason/); } finally { rmSync(root, { recursive: true, force: true }); }

  root = repo(files, [
    { file: 'tests/A.Tests/Truncated.cs', kind: 'pending', reason: 'later' },
    { file: 'tests/A.Tests/Clean.cs', kind: 'pending', reason: 'later' },
    { file: 'tests/A.Tests/Gone.cs', kind: 'pending', reason: 'later' }
  ]);
  try {
    const text = check(root).problems.join('\n');
    assert.match(text, /Clean\.cs no longer builds a reply inline/);
    assert.match(text, /Gone\.cs no longer exists/);
    assert.doesNotMatch(text, /Truncated/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the repository passes its own rule', () => {
  const { problems } = check(new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
  assert.deepEqual(problems, []);
});

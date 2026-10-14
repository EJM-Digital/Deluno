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

test('a recording no test replays fails, naming it, unless it is listed as having no behaviour in Deluno', () => {
  const recording = JSON.stringify({ app: 'qbittorrent', version: '5.2.1', state: 'resumed', exchanges: [{ request: { method: 'GET', path: '/x' }, reply: { status: 200 } }] });
  const files = {
    'tests/fixtures/recorded/qbittorrent/5.2.1/resumed.json': recording,
    'tests/fixtures/recorded/qbittorrent/5.1.0/resumed.json': recording,
    'tests/A.Tests/Unrelated.cs': inline('"resumed"')
  };
  let root = repo(files);
  try {
    const text = check(root).problems.join('\n');
    assert.match(text, /the recording "resumed" is replayed by no test/);
    assert.equal(text.match(/the recording "resumed"/g).length, 1, 'a state two versions hold is judged once');
  } finally { rmSync(root, { recursive: true, force: true }); }

  // A test that loads it by name replays it; so does a file that loads that app's recordings and lists the state.
  for (const text of [inline('RecordedReplies.Qbittorrent("resumed")'), inline('RecordedReplies.Qbittorrent(state); [InlineData("resumed")]')]) {
    root = repo({ ...files, 'tests/A.Tests/Unrelated.cs': text });
    try { assert.deepEqual(check(root).problems, []); } finally { rmSync(root, { recursive: true, force: true }); }
  }

  root = repo(files, []);
  writeFileSync(join(root, 'tests', 'recorded-reply-allow-list.json'), JSON.stringify({ entries: [], noBehaviour: [{ app: 'qbittorrent', state: 'resumed', reason: 'Deluno never makes this call, so there is nothing to replay.' }] }));
  try { assert.deepEqual(check(root).problems, []); } finally { rmSync(root, { recursive: true, force: true }); }

  root = repo(files, []);
  writeFileSync(join(root, 'tests', 'recorded-reply-allow-list.json'), JSON.stringify({ entries: [], noBehaviour: [{ app: 'qbittorrent', state: 'gone', reason: 'Deluno never makes this call, so there is nothing to replay.' }] }));
  try { assert.match(check(root).problems.join('\n'), /noBehaviour qbittorrent\/gone is not a recording/); } finally { rmSync(root, { recursive: true, force: true }); }
});

test('a state quoted in a contract test beside the helper that loads the app counts as replayed', () => {
  const recording = JSON.stringify({ app: 'weir', version: '1.0.0', state: 'status-working', exchanges: [{ request: { method: 'GET', path: '/x' }, reply: { status: 200 } }] });
  const files = {
    'tests/fixtures/recorded/weir/1.0.0/status-working.json': recording,
    'tests/A.Tests/Contract/Helper.cs': inline('RecordedReplies.Weir(state)'),
    'tests/A.Tests/Contract/Behaviour.cs': inline('Rec("status-working")')
  };
  let root = repo(files);
  try { assert.deepEqual(check(root).problems, []); } finally { rmSync(root, { recursive: true, force: true }); }
  delete files['tests/A.Tests/Contract/Behaviour.cs'];
  root = repo(files);
  try { assert.match(check(root).problems.join('\n'), /"status-working" is replayed by no test/); } finally { rmSync(root, { recursive: true, force: true }); }
});

test('a state only an older version holds is still judged, because Deluno records its own side of the Weir talk in more than one version folder', () => {
  const recording = JSON.stringify({ app: 'deluno', version: '1.0.0', state: 'events-old', exchanges: [{ request: { method: 'POST', path: '/x' }, reply: { status: 200 } }] });
  const root = repo({
    'tests/fixtures/recorded/deluno/1.0.0/events-old.json': recording,
    'tests/fixtures/recorded/deluno/1.0.1/reply-new.json': recording,
    'tests/A.Tests/Unrelated.cs': inline('RecordedReplies.Deluno("reply-new")')
  });
  try {
    const text = check(root).problems.join('\n');
    assert.match(text, /the recording "events-old" is replayed by no test/);
    assert.doesNotMatch(text, /reply-new/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

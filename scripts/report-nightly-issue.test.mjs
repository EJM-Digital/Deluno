// Run: node --test scripts/report-nightly-issue.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { reportNightlyIssue } from './report-nightly-issue.mjs';

const TITLE = 'A suite differed between nightly runs';

/** A fake GitHub: serves the given open issues (100 per page) and records every write. */
function fakeGitHub(issues) {
  const writes = [];
  const fetchImpl = async (url, init = {}) => {
    const parsed = new URL(url);
    if ((init.method ?? 'GET') === 'GET') {
      const page = Number(parsed.searchParams.get('page'));
      return { ok: true, status: 200, json: async () => issues.slice((page - 1) * 100, page * 100) };
    }
    writes.push({ path: parsed.pathname, body: JSON.parse(init.body) });
    return { ok: true, status: 201, json: async () => ({ number: 2000 }) };
  };
  return { fetchImpl, writes };
}

const base = { repo: 'o/r', token: 't', title: TITLE, body: 'text', runUrl: 'https://example/runs/9', label: 'testing: coverage' };

test('an open issue with the same title gets a comment with the new run link and no second issue is opened', async () => {
  const { fetchImpl, writes } = fakeGitHub([{ number: 5, title: 'Something else' }, { number: 7, title: TITLE }]);
  const result = await reportNightlyIssue({ ...base, fetchImpl });
  assert.deepEqual(result, { action: 'commented', number: 7 });
  assert.equal(writes.length, 1);
  assert.equal(writes[0].path, '/repos/o/r/issues/7/comments');
  assert.match(writes[0].body.body, /https:\/\/example\/runs\/9/);
});

test('with no open issue of that title a new one is opened with the label and body', async () => {
  const { fetchImpl, writes } = fakeGitHub([{ number: 5, title: 'Something else' }, { number: 6, title: TITLE, pull_request: {} }]);
  const result = await reportNightlyIssue({ ...base, fetchImpl });
  assert.deepEqual(result, { action: 'opened', number: 2000 });
  assert.equal(writes[0].path, '/repos/o/r/issues');
  assert.deepEqual(writes[0].body, { title: TITLE, body: 'text', labels: ['testing: coverage'] });
});

test('the issue is found on a later page of open issues', async () => {
  const issues = Array.from({ length: 150 }, (_, i) => ({ number: i + 1, title: `Other ${i}` }));
  issues[120] = { number: 121, title: TITLE };
  const { fetchImpl, writes } = fakeGitHub(issues);
  const result = await reportNightlyIssue({ ...base, fetchImpl });
  assert.deepEqual(result, { action: 'commented', number: 121 });
  assert.equal(writes.length, 1);
});

test('a refusal from GitHub is an error, not a silent skip', async () => {
  const fetchImpl = async () => ({ ok: false, status: 502, json: async () => ({}) });
  await assert.rejects(reportNightlyIssue({ ...base, fetchImpl }), /502/);
});

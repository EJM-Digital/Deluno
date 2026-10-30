// The TypeScript SDK (sdk/typescript/src/index.ts) against a stand-in fetch: what it asks for, with which headers, and how it
// reports a refusal. Run with `node --test sdk/typescript/tests/delunoclient.test.mjs` (Node runs the .ts source directly).
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { DelunoClient, DelunoApiError } from '../src/index.ts';

function recorder(reply = () => new Response('{}', { status: 200 })) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url, method: init.method, headers: init.headers, body: init.body });
    return reply(url, init);
  };
  return { calls, fetch };
}

const clientOn = (baseUrl, fetch) => new DelunoClient({ baseUrl, apiKey: 'deluno_example_key', fetch });

test('the base URL loses a trailing slash, the call goes under /api/v1 and carries the key and the Accept header', async () => {
  const { calls, fetch } = recorder();
  await clientOn('http://deluno.example:5099/', fetch).automationSummary();
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'http://deluno.example:5099/api/v1/automation/summary');
  assert.equal(calls[0].method, 'GET');
  assert.equal(calls[0].headers['X-Api-Key'], 'deluno_example_key');
  assert.equal(calls[0].headers.Accept, 'application/json');
  assert.equal(calls[0].body, undefined);
});

test('a call without a body sends no Content-Type and a call with a body sends JSON with it', async () => {
  const { calls, fetch } = recorder();
  const client = clientOn('http://deluno.example:5099', fetch);
  await client.automationSummary();
  await client.createBackup('before-update');
  assert.equal(calls[0].headers['Content-Type'], undefined);
  assert.equal(calls[1].method, 'POST');
  assert.equal(calls[1].url, 'http://deluno.example:5099/api/v1/backups');
  assert.equal(calls[1].headers['Content-Type'], 'application/json');
  assert.deepEqual(JSON.parse(calls[1].body), { reason: 'before-update' });
});

test('a refusal throws DelunoApiError with the status and the parsed body, and the message is the body message', async () => {
  const { fetch } = recorder(() => new Response(JSON.stringify({ error: 'forbidden', message: 'This key cannot do that.', required: 'system' }), { status: 403 }));
  await assert.rejects(clientOn('http://deluno.example', fetch).automationSummary(), (error) => {
    assert.ok(error instanceof DelunoApiError);
    assert.equal(error.name, 'DelunoApiError');
    assert.equal(error.status, 403);
    assert.equal(error.message, 'This key cannot do that.');
    assert.equal(error.body.required, 'system');
    return true;
  });
});

test('a refusal with only an error code uses the code as the message', async () => {
  const { fetch } = recorder(() => new Response(JSON.stringify({ error: 'not_found' }), { status: 404 }));
  await assert.rejects(clientOn('http://deluno.example', fetch).automationSummary(), (error) => error.status === 404 && error.message === 'not_found');
});

test('a refusal with an empty body says the request failed with its status, and a plain-text refusal keeps its text', async () => {
  const empty = recorder(() => new Response('', { status: 500 }));
  await assert.rejects(clientOn('http://deluno.example', empty.fetch).automationSummary(), (error) => {
    return error instanceof DelunoApiError && error.status === 500 && error.body === undefined && error.message === 'Deluno API request failed (500).';
  });
  const text = recorder(() => new Response('upstream is down', { status: 502 }));
  await assert.rejects(clientOn('http://deluno.example', text.fetch).automationSummary(), (error) => error.status === 502 && error.message === 'upstream is down');
});

test('bulkAddCatalogue sends the idempotency key as a header equal to the one in the body', async () => {
  const { calls, fetch } = recorder();
  await clientOn('http://deluno.example', fetch).bulkAddCatalogue({ idempotencyKey: 'run-2026-10-15', items: [] });
  assert.equal(calls[0].headers['Idempotency-Key'], 'run-2026-10-15');
  assert.equal(JSON.parse(calls[0].body).idempotencyKey, calls[0].headers['Idempotency-Key']);
});

test('syncSeriesEpisodes sends the idempotency key as a header equal to the one in the body', async () => {
  const { calls, fetch } = recorder();
  await clientOn('http://deluno.example', fetch).syncSeriesEpisodes('series-1', { idempotencyKey: 'episodes-1', episodes: [] });
  assert.equal(calls[0].headers['Idempotency-Key'], 'episodes-1');
  assert.equal(JSON.parse(calls[0].body).idempotencyKey, 'episodes-1');
});

test('a call with no idempotency key sends no Idempotency-Key header', async () => {
  const { calls, fetch } = recorder();
  await clientOn('http://deluno.example', fetch).bulkAddCatalogue({ items: [] });
  assert.equal('Idempotency-Key' in calls[0].headers, false);
});

test('automationSummary asks GET /api/v1/automation/summary and returns the parsed reply', async () => {
  const { calls, fetch } = recorder(() => new Response(JSON.stringify({ readiness: { status: 'ready' } }), { status: 200 }));
  const summary = await clientOn('http://deluno.example', fetch).automationSummary();
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/automation/summary');
  assert.equal(summary.readiness.status, 'ready');
});

test('bulkAddCatalogue posts the add-a-title call to /automation/catalogue/bulk with the request as its body', async () => {
  const { calls, fetch } = recorder();
  const request = { items: [{ title: 'Example Film', year: 2024, mediaType: 'movie' }] };
  await clientOn('http://deluno.example', fetch).bulkAddCatalogue(request);
  assert.equal(calls[0].method, 'POST');
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/automation/catalogue/bulk');
  assert.deepEqual(JSON.parse(calls[0].body), request);
});

test('syncSeriesEpisodes posts to the show\'s episodes/bulk with the id encoded', async () => {
  const { calls, fetch } = recorder();
  await clientOn('http://deluno.example', fetch).syncSeriesEpisodes('a b/c', { episodes: [] });
  assert.equal(calls[0].method, 'POST');
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/automation/series/a%20b%2Fc/episodes/bulk');
});

test('searchLibrary posts to /libraries/{id}/search-now with no body', async () => {
  const { calls, fetch } = recorder();
  await clientOn('http://deluno.example', fetch).searchLibrary('lib-1');
  assert.equal(calls[0].method, 'POST');
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/libraries/lib-1/search-now');
  assert.equal(calls[0].body, undefined);
});

// ---- every other call the SDK makes, by the route it asks for (coverage register, engines; Refs #1296) ----------------------------------

const answer = (body, status = 200) => () => new Response(JSON.stringify(body), { status });
const urlOf = (call) => new URL(call.url);

test('readiness asks /health/ready, and a server that is not ready makes the SDK throw with the body it sent', async () => {
  const ready = recorder(answer({ ready: true, status: 'ready' }));
  assert.deepEqual(await clientOn('http://deluno.example', ready.fetch).readiness(), { ready: true, status: 'ready' });
  assert.equal(ready.calls[0].url, 'http://deluno.example/api/v1/health/ready');

  const notReady = recorder(answer({ ready: false, status: 'not_ready', checks: [{ name: 'schema:migrations', status: 'not_ready' }] }, 503));
  await assert.rejects(clientOn('http://deluno.example', notReady.fetch).readiness(), (error) => {
    return error instanceof DelunoApiError && error.status === 503 && error.body.status === 'not_ready' && error.body.checks.length === 1;
  });
});

test('scopeTemplates asks GET /api-keys/scope-templates and returns the list as sent', async () => {
  const { calls, fetch } = recorder(answer([{ id: 'read-only', name: 'Read only', scopes: ['read'] }]));
  const templates = await clientOn('http://deluno.example', fetch).scopeTemplates();
  assert.equal(calls[0].method, 'GET');
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/api-keys/scope-templates');
  assert.equal(templates[0].id, 'read-only');
});

test('listNotificationWebhookDeliveries builds the query from the options it is given and nothing else', async () => {
  const { calls, fetch } = recorder(answer([]));
  const client = clientOn('http://deluno.example', fetch);
  await client.listNotificationWebhookDeliveries();
  await client.listNotificationWebhookDeliveries({ status: 'dead-letter', webhookId: 'hook 1', take: 0 });
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/notification-webhooks/deliveries');
  assert.equal(urlOf(calls[1]).pathname, '/api/v1/notification-webhooks/deliveries');
  assert.deepEqual(Object.fromEntries(urlOf(calls[1]).searchParams), { status: 'dead-letter', webhookId: 'hook 1', take: '0' });
});

test('replayNotificationWebhookDelivery posts with the id encoded, and an unknown id is a 404 DelunoApiError', async () => {
  const { calls, fetch } = recorder(answer({ error: 'not_found' }, 404));
  await assert.rejects(clientOn('http://deluno.example', fetch).replayNotificationWebhookDelivery('a b/c'), (error) => error instanceof DelunoApiError && error.status === 404);
  assert.equal(calls[0].method, 'POST');
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/notification-webhooks/deliveries/a%20b%2Fc/replay');
  assert.equal(calls[0].body, undefined);
});

test('listDownloadDispatches sends only the options that are set, and passes the import-time bounds on as they are', async () => {
  const { calls, fetch } = recorder(answer({ items: [] }));
  const client = clientOn('http://deluno.example', fetch);
  await client.listDownloadDispatches();
  await client.listDownloadDispatches({ clientId: 'qbit', mediaType: 'movie', minImportTime: '2026-10-01T00:00:00Z', maxImportTime: '2026-10-02T00:00:00Z', pageSize: 25, grabStatus: undefined });
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/download-dispatches');
  assert.deepEqual(Object.fromEntries(urlOf(calls[1]).searchParams), {
    clientId: 'qbit', mediaType: 'movie', minImportTime: '2026-10-01T00:00:00Z', maxImportTime: '2026-10-02T00:00:00Z', pageSize: '25'
  });
});

test('getDownloadDispatch asks /download-dispatches/{id} with the id encoded, and an unknown id is a 404 with the server\'s error', async () => {
  const found = recorder(answer({ dispatch: { id: 'd1' }, timeline: [] }));
  const reply = await clientOn('http://deluno.example', found.fetch).getDownloadDispatch('d 1');
  assert.equal(found.calls[0].url, 'http://deluno.example/api/v1/download-dispatches/d%201');
  assert.equal(reply.dispatch.id, 'd1');
  const missing = recorder(answer({ error: 'not_found' }, 404));
  await assert.rejects(clientOn('http://deluno.example', missing.fetch).getDownloadDispatch('nope'), (error) => error.status === 404 && error.message === 'not_found');
});

test('listImportResolutions sends only the options that are set and leaves the status to the server\'s own default', async () => {
  const { calls, fetch } = recorder(answer({ items: [] }));
  const client = clientOn('http://deluno.example', fetch);
  await client.listImportResolutions();
  await client.listImportResolutions({ status: 'failed', libraryId: 'lib-1', importedAfter: '2026-10-01', pageToken: 'next' });
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/import-resolutions');
  assert.deepEqual(Object.fromEntries(urlOf(calls[1]).searchParams), { status: 'failed', libraryId: 'lib-1', importedAfter: '2026-10-01', pageToken: 'next' });
});

test('saveReleasePreferencePlan and previewReleasePreference post their argument as the body to their own routes', async () => {
  const { calls, fetch } = recorder(answer({ ok: true }));
  const client = clientOn('http://deluno.example', fetch);
  const plan = { id: 'plan-1', mediaType: 'movies' };
  const request = { planId: 'plan-1', candidate: { releaseName: 'Example.2024.1080p' } };
  await client.saveReleasePreferencePlan(plan);
  await client.previewReleasePreference(request);
  assert.deepEqual([calls[0].method, calls[0].url], ['POST', 'http://deluno.example/api/v1/release-preferences/plans']);
  assert.deepEqual(JSON.parse(calls[0].body), plan);
  assert.deepEqual([calls[1].method, calls[1].url], ['POST', 'http://deluno.example/api/v1/release-preferences/preview']);
  assert.deepEqual(JSON.parse(calls[1].body), request);
});

test('listJobs, listActivity and listDecisions ask for a page of 50 unless told otherwise, and carry the page token when there is one', async () => {
  const { calls, fetch } = recorder(answer({ items: [] }));
  const client = clientOn('http://deluno.example', fetch);
  await client.listJobs();
  await client.listActivity(10, 'token-a');
  await client.listDecisions(200);
  assert.equal(calls[0].url, 'http://deluno.example/api/v1/jobs?pageSize=50');
  assert.equal(calls[1].url, 'http://deluno.example/api/v1/activity?pageSize=10&pageToken=token-a');
  assert.equal(calls[2].url, 'http://deluno.example/api/v1/decisions?pageSize=200');
});

test('listBackups gets /backups and createBackup posts the reason, "automation" when none is given', async () => {
  const { calls, fetch } = recorder(answer([]));
  const client = clientOn('http://deluno.example', fetch);
  await client.listBackups();
  await client.createBackup();
  await client.createBackup('before-update');
  assert.deepEqual([calls[0].method, calls[0].url], ['GET', 'http://deluno.example/api/v1/backups']);
  assert.deepEqual(JSON.parse(calls[1].body), { reason: 'automation' });
  assert.deepEqual(JSON.parse(calls[2].body), { reason: 'before-update' });
});

test('the existing-media import calls start, read, pause and resume under /libraries/{id}/import-existing with the id encoded', async () => {
  const { calls, fetch } = recorder(answer({}));
  const client = clientOn('http://deluno.example', fetch);
  await client.startExistingLibraryImport('lib 1');
  await client.importProgress('lib 1');
  await client.pauseExistingLibraryImport('lib 1');
  await client.resumeExistingLibraryImport('lib 1');
  const base = 'http://deluno.example/api/v1/libraries/lib%201/import-existing';
  assert.deepEqual(calls.map((call) => [call.method, call.url]), [['POST', base], ['GET', base], ['POST', `${base}/pause`], ['POST', `${base}/resume`]]);
});

test('setGlobalAutomationEnabled puts {isEnabled} to /settings/automation', async () => {
  const { calls, fetch } = recorder(answer({}));
  const client = clientOn('http://deluno.example', fetch);
  await client.setGlobalAutomationEnabled(false);
  await client.setGlobalAutomationEnabled(true);
  assert.deepEqual([calls[0].method, calls[0].url], ['PUT', 'http://deluno.example/api/v1/settings/automation']);
  assert.deepEqual(JSON.parse(calls[0].body), { isEnabled: false });
  assert.deepEqual(JSON.parse(calls[1].body), { isEnabled: true });
});

test('approveIntakePreview posts the body to the source\'s approve-preview route with the id encoded', async () => {
  const { calls, fetch } = recorder(answer({}));
  await clientOn('http://deluno.example', fetch).approveIntakePreview('src/1', { entries: [], searchAfterAdd: true });
  assert.deepEqual([calls[0].method, calls[0].url], ['POST', 'http://deluno.example/api/v1/intake-sources/src%2F1/approve-preview']);
  assert.deepEqual(JSON.parse(calls[0].body), { entries: [], searchAfterAdd: true });
});

test('a validation problem keeps its title and its errors on the error, whatever message it carries', async () => {
  const problem = { title: 'One or more validation errors occurred.', status: 400, errors: { name: ['Give this key a name.'] } };
  const { fetch } = recorder(answer(problem, 400));
  await assert.rejects(clientOn('http://deluno.example', fetch).createBackup(), (error) => {
    return error instanceof DelunoApiError && error.status === 400 && error.body.errors.name[0] === 'Give this key a name.' && typeof error.message === 'string' && error.message.length > 0;
  });
});

test('the SDK source compiles under strict tsc, the check typecheck:sdk runs in CI', () => {
  const sdkRoot = fileURLToPath(new URL('..', import.meta.url));
  const tsconfig = JSON.parse(readFileSync(`${sdkRoot}tsconfig.json`, 'utf8'));
  assert.equal(tsconfig.compilerOptions.strict, true);
  const tsc = fileURLToPath(new URL('../../../node_modules/typescript/bin/tsc', import.meta.url));
  const run = spawnSync(process.execPath, [tsc, '--noEmit', '-p', `${sdkRoot}tsconfig.json`], { encoding: 'utf8' });
  assert.equal(run.status, 0, `tsc reported:
${run.stdout}${run.stderr}`);
});

test('the client uses the fetch it is given, and globalThis.fetch when none is given', async () => {
  const realFetch = globalThis.fetch;
  const seen = [];
  globalThis.fetch = async (url) => { seen.push(url); return new Response('{"ok":true}', { status: 200 }); };
  try {
    const reply = await new DelunoClient({ baseUrl: 'http://deluno.example', apiKey: 'deluno_example_key' }).automationSummary();
    assert.deepEqual(reply, { ok: true });
    assert.deepEqual(seen, ['http://deluno.example/api/v1/automation/summary']);
    const { calls, fetch } = recorder();
    await clientOn('http://deluno.example', fetch).automationSummary();
    assert.equal(calls.length, 1);
    assert.equal(seen.length, 1, 'the injected fetch took the call, globalThis.fetch did not');
  } finally {
    globalThis.fetch = realFetch;
  }
});

test('a non-JSON success body is wrapped as {message}, an empty success body returns undefined and a non-JSON error body still throws DelunoApiError', async () => {
  const wrapped = await clientOn('http://deluno.example', recorder(() => new Response('all good', { status: 200 })).fetch).automationSummary();
  assert.deepEqual(wrapped, { message: 'all good' });
  const empty = await clientOn('http://deluno.example', recorder(() => new Response('', { status: 200 })).fetch).automationSummary();
  assert.equal(empty, undefined);
  await assert.rejects(
    clientOn('http://deluno.example', recorder(() => new Response('<html>bad gateway</html>', { status: 502 })).fetch).automationSummary(),
    (error) => error instanceof DelunoApiError && error.status === 502 && error.body.message === '<html>bad gateway</html>');
});

test('a network failure propagates unchanged and is not turned into a DelunoApiError', async () => {
  const failure = new TypeError('fetch failed');
  await assert.rejects(
    clientOn('http://deluno.example', async () => { throw failure; }).automationSummary(),
    (error) => error === failure && !(error instanceof DelunoApiError));
});

test('listReleasePreferencePlans and getReleasePreferencePlan ask the plan routes with the media type, plan id and version encoded', async () => {
  const { calls, fetch } = recorder(() => new Response('[]', { status: 200 }));
  const client = clientOn('http://deluno.example', fetch);
  await client.listReleasePreferencePlans();
  await client.listReleasePreferencePlans('tv shows');
  await client.getReleasePreferencePlan('quality-profile/abc');
  await client.getReleasePreferencePlan('quality-profile/abc', 'v 1/2');
  assert.deepEqual(calls.map((call) => call.url), [
    'http://deluno.example/api/v1/release-preferences/plans',
    'http://deluno.example/api/v1/release-preferences/plans?mediaType=tv%20shows',
    'http://deluno.example/api/v1/release-preferences/plans/quality-profile%2Fabc',
    'http://deluno.example/api/v1/release-preferences/plans/quality-profile%2Fabc?version=v%201%2F2'
  ]);
  assert.ok(calls.every((call) => call.method === 'GET'));
});

test('the client has the methods the docs list, none for the queue or the other tools endpoints, and the docs claim no queue method', () => {
  const methods = Object.getOwnPropertyNames(DelunoClient.prototype);
  for (const name of ['readiness', 'scopeTemplates', 'automationSummary', 'listDownloadDispatches', 'getDownloadDispatch', 'listImportResolutions', 'bulkAddCatalogue', 'syncSeriesEpisodes', 'listJobs', 'listActivity', 'listDecisions', 'listBackups', 'createBackup', 'searchLibrary', 'startExistingLibraryImport', 'importProgress']) {
    assert.ok(methods.includes(name), `${name} is on the client`);
  }
  assert.equal(methods.filter((name) => /queue|diskspace|wanted|history|stats|command/i.test(name)).length, 0);
  const docs = readFileSync(fileURLToPath(new URL('../../../docs/automation-integrations.md', import.meta.url)), 'utf8');
  const sdkSentence = docs.slice(docs.indexOf('## Supported SDK'), docs.indexOf('```ts'));
  assert.doesNotMatch(sdkSentence, /\bqueue\b/);
});

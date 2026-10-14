// The TypeScript SDK (sdk/typescript/src/index.ts) against a stand-in fetch: what it asks for, with which headers, and how it
// reports a refusal. Run with `node --test sdk/typescript/tests/delunoclient.test.mjs` (Node runs the .ts source directly).
import test from 'node:test';
import assert from 'node:assert/strict';
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

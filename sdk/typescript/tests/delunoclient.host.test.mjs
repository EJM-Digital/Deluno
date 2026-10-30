// The TypeScript SDK (sdk/typescript/src/index.ts) against a real Deluno.Host: the shipped server is started on a disposable data folder
// (the same settings the browser smoke suite uses), a key is made from a scope template, and every DelunoClient method is called.
// Needs the Release build of the host: `dotnet build Deluno.slnx -c Release`. Run with `node --test sdk/typescript/tests/delunoclient.host.test.mjs`.
import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DelunoClient, DelunoApiError } from '../src/index.ts';

const repoRoot = resolve(fileURLToPath(new URL('../../..', import.meta.url)));
const hostDll = join(repoRoot, 'src', 'Deluno.Host', 'bin', 'Release', 'net10.0', 'Deluno.Host.dll');

let host;
let dataRoot;
let baseUrl;
let client;
let libraryId;
let sessionToken;
const hostLog = [];

const freePort = () => new Promise((done, fail) => {
  const probe = createServer();
  probe.once('error', fail);
  probe.listen(0, '127.0.0.1', () => { const { port } = probe.address(); probe.close(() => done(port)); });
});

async function waitFor(check, what, milliseconds = 120000) {
  const started = Date.now();
  for (;;) {
    try { const value = await check(); if (value) return value; } catch { /* the host is still starting */ }
    if (Date.now() - started > milliseconds) throw new Error(`${what} did not happen in time. Host output:\n${hostLog.join('')}`);
    await new Promise((later) => setTimeout(later, 250)); // reason: polling the starting host for its health answer, bounded by the limit above
  }
}

async function post(path, body, headers = {}) {
  return fetch(`${baseUrl}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body) });
}

before(async () => {
  assert.ok(existsSync(hostDll), `build the host first (dotnet build Deluno.slnx -c Release): ${hostDll}`);
  dataRoot = mkdtempSync(join(tmpdir(), 'deluno-sdk-host-'));
  const port = await freePort();
  baseUrl = `http://127.0.0.1:${port}`;
  host = spawn('dotnet', [hostDll], {
    cwd: join(repoRoot, 'src', 'Deluno.Host'),
    env: {
      ...process.env,
      Storage__DataRoot: dataRoot,
      Server__Port: String(port),
      Security__Login__PermitLimit: '100000',
      Security__Api__PermitLimit: '1000000',
      Filesystem__NativeFolderPicker: 'false'
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true
  });
  host.stdout.on('data', (chunk) => hostLog.push(String(chunk)));
  host.stderr.on('data', (chunk) => hostLog.push(String(chunk)));
  await waitFor(async () => (await fetch(`${baseUrl}/health`)).ok, 'the host answering /health');

  const password = 'Deluno-Sdk-Test-2026!';
  assert.ok((await post('/api/auth/bootstrap', { username: 'owner', displayName: 'Owner', password })).ok, 'bootstrap');
  const login = await post('/api/auth/login', { username: 'owner', password });
  assert.ok(login.ok, 'login');
  const session = (await login.json()).accessToken;
  sessionToken = session;
  const made = await post('/api/api-keys/', { name: 'SDK test', scopes: 'full-local' }, { Authorization: `Bearer ${session}`, 'X-Current-Password': password });
  assert.ok(made.ok, `the key from the full-local template: ${made.status} ${await made.clone().text()}`);
  client = new DelunoClient({ baseUrl, apiKey: (await made.json()).apiKey });

  const folder = join(dataRoot, 'Films');
  mkdirSync(folder, { recursive: true });
  const library = await post('/api/libraries', { name: 'Films', mediaType: 'movies', rootPath: folder }, { Authorization: `Bearer ${session}` });
  assert.ok(library.ok, `a library: ${library.status}`);
  libraryId = (await library.json()).id;
});

after(async () => {
  if (host && host.exitCode === null) {
    const stopped = new Promise((done) => host.once('exit', done));
    host.kill();
    await stopped;
  }
  if (dataRoot) rmSync(dataRoot, { recursive: true, force: true });
});

const asRefusal = async (call) => {
  try { await call(); } catch (failure) { return failure; }
  return assert.fail('the server should have refused this call');
};

test('the scope templates, readiness and the automation summary come back in the shapes the SDK types promise', async () => {
  const templates = await client.scopeTemplates();
  assert.ok(Array.isArray(templates) && templates.length >= 5);
  for (const template of templates) {
    for (const member of ['id', 'name', 'description']) assert.equal(typeof template[member], 'string', `${template.id}.${member}`);
    assert.ok(Array.isArray(template.scopes));
  }
  assert.ok(templates.some((template) => template.id === 'full-local'));
  assert.ok(await client.readiness());
  const summary = await client.automationSummary();
  assert.equal(typeof summary, 'object');
  for (const member of ['queue', 'imports', 'attention']) assert.ok(member in summary, `the summary carries ${member}`);
});

test('every list the SDK reads answers an empty list, or a page, on a fresh install', async () => {
  assert.ok(Array.isArray(await client.listNotificationWebhookDeliveries()));
  assert.ok(Array.isArray(await client.listNotificationWebhookDeliveries({ status: 'dead-letter', take: 5 })));
  const dispatches = await client.listDownloadDispatches({ pageSize: 5 });
  assert.ok(Array.isArray(dispatches.items));
  const resolutions = await client.listImportResolutions({ pageSize: 5 });
  assert.ok(Array.isArray(resolutions.items));
  for (const read of [() => client.listJobs(5), () => client.listActivity(5), () => client.listDecisions(5)]) {
    const page = await read();
    assert.ok(Array.isArray(page.items));
  }
  assert.ok(Array.isArray(await client.listBackups()));
  assert.ok(Array.isArray(await client.listReleasePreferencePlans()));
});

test('a download dispatch, a webhook delivery and an intake source that do not exist are refused as a typed error with their status', async () => {
  const dispatch = await asRefusal(() => client.getDownloadDispatch('nothing-here'));
  assert.ok(dispatch instanceof DelunoApiError);
  assert.equal(dispatch.status, 404);
  const delivery = await asRefusal(() => client.replayNotificationWebhookDelivery('nothing-here'));
  assert.ok(delivery instanceof DelunoApiError);
  assert.equal(delivery.status, 404);
  const source = await asRefusal(() => client.approveIntakePreview('nothing-here', {}));
  assert.ok(source instanceof DelunoApiError);
  assert.ok([400, 404].includes(source.status), `status ${source.status}`);
  const plan = await asRefusal(() => client.getReleasePreferencePlan('no-such-plan'));
  assert.equal(plan.status, 404);
});

test('settings, backups and the library actions the SDK drives work against the real routes', async () => {
  const answers = {};
  answers.automationOff = await client.setGlobalAutomationEnabled(false);
  answers.automationOn = await client.setGlobalAutomationEnabled(true);
  answers.backup = await client.createBackup('sdk-test');
  answers.import = await client.startExistingLibraryImport(libraryId);
  answers.progress = await client.importProgress(libraryId);
  answers.pause = await client.pauseExistingLibraryImport(libraryId);
  answers.resume = await client.resumeExistingLibraryImport(libraryId);
  answers.search = await client.searchLibrary(libraryId);
  assert.equal(answers.automationOff.autoStartJobs, false);
  assert.equal(answers.automationOn.autoStartJobs, true);
  assert.equal(answers.backup.backup.reason, 'sdk-test');
  assert.ok((await client.listBackups()).some((item) => item.id === answers.backup.backup.id));
  assert.equal(answers.import.run.libraryId, libraryId);
  assert.equal(answers.import.run.status, 'queued');
  assert.equal(answers.progress.run.id, answers.import.run.id);
  assert.equal(answers.pause.run.status, 'paused');
  assert.equal(answers.resume.run.status, 'running');
  assert.ok(answers.search === undefined || typeof answers.search === 'object', 'search-now answers an empty or JSON body');
});

test('a bulk add runs as a dry run and for real, and the episodes call refuses a show that does not exist', async () => {
  const request = { dryRun: true, idempotencyKey: 'sdk-bulk-1', items: [{ clientItemId: 'a', mediaType: 'movies', title: 'Sintel', releaseYear: 2010, libraryId }] };
  const planned = await client.bulkAddCatalogue(request);
  assert.equal(planned.dryRun, true);
  assert.equal(planned.total, 1);
  const created = await client.bulkAddCatalogue({ ...request, dryRun: false, idempotencyKey: 'sdk-bulk-2' });
  assert.equal(created.createdCount, 1);
  assert.equal(created.items[0].status, 'created');
  assert.ok(created.items[0].entityId);
  const refusal = await asRefusal(() => client.syncSeriesEpisodes('no-such-series', { dryRun: true, episodes: [] }));
  assert.ok(refusal instanceof DelunoApiError);
});

test('a release-preference plan is saved, listed, read, and a release is previewed against it through the SDK', async () => {
  const plan = {
    id: 'sdk-plan', version: '1', mediaType: 'movies',
    families: [{
      id: 'quality', dimension: 'quality', order: 0, intent: 'ranked', targetLevelId: 'web-2160p', upgradeDriving: true,
      levels: [{ id: 'web-1080p', rank: 1, traitIds: ['quality.web-1080p'] }, { id: 'web-2160p', rank: 2, traitIds: ['quality.web-2160p'] }]
    }]
  };
  const saved = await client.saveReleasePreferencePlan(plan);
  assert.equal(saved.plan.id, 'sdk-plan');
  assert.equal(typeof saved.planHash, 'string');
  const listed = await client.listReleasePreferencePlans('movies');
  assert.ok(listed.some((item) => item.plan.id === 'sdk-plan'));
  const read = await client.getReleasePreferencePlan('sdk-plan', '1');
  assert.equal(read.planHash, saved.planHash);
  const preview = await client.previewReleasePreference({ planId: 'sdk-plan', planVersion: '1', releaseName: 'Sintel.2010.2160p.WEB-DL.x265-GRP' });
  assert.equal(typeof preview, 'object');
});

// Tests of the coverage-register check (scripts/check-coverage-register.mjs) and of the inventory it reads (scripts/coverage/inventory.mjs).
// Run: node --test scripts/check-coverage-register.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import {
  evaluate, generatedSection, isGap, mapInventory, missingProofs, requiredFor, rowLevel, scenarioViolations, sleepViolations, totals, validateShape
} from './check-coverage-register.mjs';
import { TestIndex } from './coverage/testindex.mjs';
import { migrationItems, routeItems, trayItems, webRouteItems } from './coverage/inventory.mjs';

function makeRepo(files) {
  const root = mkdtempSync(join(tmpdir(), 'coverage-register-'));
  for (const [rel, text] of Object.entries(files)) {
    const full = join(root, rel);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, text);
  }
  return root;
}

const row = (over = {}) => ({
  id: 'api.movies.get-movies', area: 'api', behaviour: 'GET /api/movies lists the movies', kind: 'API route', severity: 'medium', touches: 'none', required: 'inproc',
  proofs: [], ...over
});
const register = (rows) => ({ version: 1, areas: { api: { name: 'API' } }, rows });

const CS_TEST = `
public class MovieListTests
{
    [Fact]
    public async Task Listing_movies_answers_ok() { }

    [Fact]
    public async Task A_recorded_reply_is_read()
    {
        var handler = RecordedReplies.Qbittorrent("paused");
    }
}
`;
const SPEC = `import { test } from '@playwright/test';\ntest('the movies page opens', async () => {});\n`;

test('a row reaches its level only with a proof of that level', () => {
  const none = row();
  const unit = row({ proofs: [{ type: 'vitest', file: 'a.test.ts', name: 'x' }] });
  const high = row({ severity: 'high', touches: 'other-app', required: 'real', proofs: [{ type: 'backend', file: 'A.cs', name: 'x' }] });
  const highReal = row({ severity: 'high', touches: 'other-app', required: 'real', proofs: [{ type: 'recording-replay', file: 'A.cs', name: 'x' }] });
  assert.equal(rowLevel(none), 0);
  assert.equal(isGap(none), true);
  assert.equal(isGap(unit), false);
  assert.equal(isGap(high), true, 'a high row is not met by an in-process test');
  assert.equal(isGap(highReal), false);
  assert.equal(isGap(row({ severity: 'high', touches: 'other-app', required: 'real', proofs: [{ type: 'scenario', file: 'G.ps1', name: 'film.qbittorrent' }] })), false);
});

test('the register shape refuses a high row that asks for less than real proof, and duplicate ids', () => {
  assert.deepEqual(validateShape(register([row()])), []);
  assert.match(validateShape(register([row({ severity: 'high', touches: 'other-app', required: 'inproc' })])).join('\n'), /required must be "real"/);
  assert.match(validateShape(register([row(), row()])).join('\n'), /id used twice/);
  assert.match(validateShape(register([row({ kind: 'nonsense' })])).join('\n'), /kind/);
  assert.match(validateShape(register([row({ area: 'nowhere' })])).join('\n'), /area/);
  assert.match(validateShape(register([row({ proofs: [{ type: 'magic', file: 'a', name: 'b' }] })])).join('\n'), /proof type/);
});

test('(a) an inventory item with no row fails, and a row covering an item that is gone fails', () => {
  const inventory = [{ kind: 'route', key: 'route:GET /api/movies' }, { kind: 'route', key: 'route:GET /api/new' }];
  const rows = [row({ covers: ['route:GET /api/movies', 'route:GET /api/gone'] })];
  const { unmapped, stale } = mapInventory(register(rows), inventory);
  assert.deepEqual(unmapped.map((i) => i.key), ['route:GET /api/new']);
  assert.deepEqual(stale.map((s) => s.key), ['route:GET /api/gone']);
});

test('(b) a proof must name a file and a test that exist, of the type it claims', () => {
  const root = makeRepo({
    'tests/Deluno.Persistence.Tests/MovieListTests.cs': CS_TEST,
    'apps/web/tests/smoke/movies.spec.ts': SPEC,
    'scripts/golden-path/suite/vm/scenarios/G02-Films.ps1': "function Invoke-GroupFilms { 'film.qbittorrent' }\n"
  });
  try {
    const index = new TestIndex(root);
    const cs = 'tests/Deluno.Persistence.Tests/MovieListTests.cs';
    const good = [
      { type: 'backend', file: cs, name: 'Listing_movies_answers_ok' },
      { type: 'backend', file: cs, name: 'MovieListTests' },
      { type: 'backend', file: cs, name: '*' },
      { type: 'recording-replay', file: cs, name: 'A_recorded_reply_is_read' },
      { type: 'smoke', file: 'apps/web/tests/smoke/movies.spec.ts', name: 'the movies page opens' },
      { type: 'scenario', file: 'scripts/golden-path/suite/vm/scenarios/G02-Films.ps1', name: 'film.qbittorrent' }
    ];
    assert.deepEqual(missingProofs(register([row({ proofs: good })]), index, ['film.qbittorrent']), []);

    const bad = [
      { type: 'backend', file: 'tests/Deluno.Persistence.Tests/Gone.cs', name: 'Listing_movies_answers_ok' },
      { type: 'backend', file: cs, name: 'A_test_that_was_renamed' },
      { type: 'recording-replay', file: cs, name: 'Listing_movies_answers_ok' },
      { type: 'vitest', file: cs, name: 'Listing_movies_answers_ok' },
      { type: 'smoke', file: 'apps/web/tests/smoke/movies.spec.ts', name: 'a title that is not in the spec' }
    ];
    const problems = missingProofs(register([row({ proofs: bad })]), index, null);
    assert.equal(problems.length, bad.length);
    assert.match(problems[0].why, /does not exist/);
    assert.match(problems[1].why, /no test, group or class/);
    assert.match(problems[2].why, /does not load a recorded real reply/);
    assert.match(problems[3].why, /vitest proof must be/);
    assert.match(problems[4].why, /no test, group or class/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a partial proof is checked too', () => {
  const root = makeRepo({ 'tests/X.Tests/ATests.cs': CS_TEST });
  try {
    const index = new TestIndex(root);
    const problems = missingProofs(register([row({ partial: [{ type: 'backend', file: 'tests/X.Tests/ATests.cs', name: 'Gone_test' }] })]), index, null);
    assert.equal(problems.length, 1);
    assert.equal(problems[0].label, 'partial proof');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('totals count gaps by severity, best proof and area', () => {
  const rows = [
    row({ id: 'a1', proofs: [{ type: 'backend', file: 'A.cs', name: 'x' }] }),
    row({ id: 'a2' }),
    row({ id: 'a3', severity: 'high', touches: 'other-app', required: 'real', proofs: [{ type: 'backend', file: 'A.cs', name: 'x' }] }),
    row({ id: 'a4', severity: 'high', touches: 'other-app', required: 'real', proofs: [{ type: 'scenario', file: 'G.ps1', name: 'x' }] })
  ];
  const t = totals(register(rows));
  assert.equal(t.all.rows, 4);
  assert.equal(t.all.gaps, 2);
  assert.equal(t.highGaps, 1);
  assert.deepEqual(t.byLevel, { none: 1, inproc: 2, real: 1 });
  assert.equal(t.byArea.api.highGaps, 1);
  assert.match(generatedSection(register(rows), [], t), /\*\*4\*\* behaviours/);
});

test('a fixed sleep in a test needs a reason on its line', () => {
  const root = makeRepo({
    'tests/X.Tests/SleepTests.cs': [
      'await Task.Delay(500);',
      'await Task.Delay(500); // reason: the fake client answers on a timer',
      '// await Task.Delay(500);'
    ].join('\n'),
    'apps/web/tests/smoke/a.spec.ts': "await page.waitForTimeout(100);\nawait page.waitForTimeout(100); // reason: the animation has no event\n",
    'scripts/golden-path/suite/vm/scenarios/G01.ps1': "Start-Sleep -Seconds 2\nStart-Sleep -Seconds 2 # reason: the tray has no event\n"
  });
  try {
    const found = sleepViolations(new TestIndex(root));
    assert.deepEqual(found.map((f) => `${f.file}:${f.line}`).sort(), [
      'apps/web/tests/smoke/a.spec.ts:1', 'scripts/golden-path/suite/vm/scenarios/G01.ps1:1', 'tests/X.Tests/SleepTests.cs:1'
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a scenario is runnable alone only under its own plain id and a defined group function', () => {
  const root = makeRepo({ 'scripts/golden-path/suite/vm/scenarios/G02-Films.ps1': 'function Invoke-GroupFilms { }\n' });
  try {
    const index = new TestIndex(root);
    const ok = [{ id: 'film.qbittorrent', group: 'films', fn: 'Invoke-GroupFilms' }, { id: 'update.over-running-previous', group: 'update', fn: '' }];
    assert.deepEqual(scenarioViolations(ok, index), []);
    const bad = [
      { id: 'film.qbittorrent', group: 'films', fn: 'Invoke-GroupFilms' },
      { id: 'film.qbittorrent', group: 'films', fn: 'Invoke-GroupFilms' },
      { id: 'Film Two', group: 'films', fn: 'Invoke-GroupFilms' },
      { id: 'film.three', group: 'films', fn: 'Invoke-GroupMissing' }
    ];
    assert.deepEqual(scenarioViolations(bad, index).map((v) => v.why.split(' ')[0]), ['id', 'id', 'group']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('evaluate ties the parts together', () => {
  const root = makeRepo({ 'tests/X.Tests/MovieListTests.cs': CS_TEST });
  try {
    const index = new TestIndex(root);
    const inventory = [{ kind: 'route', key: 'route:GET /api/movies' }];
    const rows = [row({ covers: ['route:GET /api/movies'], proofs: [{ type: 'backend', file: 'tests/X.Tests/MovieListTests.cs', name: 'Listing_movies_answers_ok' }] })];
    const result = evaluate({ register: register(rows), inventory, index, plan: [], root });
    assert.deepEqual([result.shape, result.unmapped, result.stale, result.missing], [[], [], [], []]);
    assert.equal(result.totals.gaps.length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('inventory reads routes through nested route groups', () => {
  const root = makeRepo({
    'src/Deluno.Movies/MoviesEndpoints.cs': `
public static class MoviesEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var movies = endpoints.MapGroup("/api/movies").RequireAuthorization("Write");
        movies.MapGet("/", List);
        movies.MapGet("/{id:guid}", Get);
        var files = movies.MapGroup("/{id}/files");
        files.MapDelete("/{fileId}", Delete);
        endpoints.MapPost("/api/health/run", Run).AllowAnonymous();
    }
}`
  });
  try {
    assert.deepEqual(routeItems(root).map((r) => r.key).sort(), [
      'route:DELETE /api/movies/{id}/files/{fileId}',
      'route:GET /api/movies',
      'route:GET /api/movies/{id}',
      'route:POST /api/health/run'
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('inventory reads web routes, redirects, migrations and tray actions', () => {
  const root = makeRepo({
    'apps/web/src/router.tsx': `
const router = createBrowserRouter([
  { path: "/login", element: <Login /> },
  {
    path: "/",
    element: <Layout />,
    children: [
      { index: true, element: <Home /> },
      { path: "movies", children: [ { index: true, element: <Movies /> }, { path: "wanted", element: <Navigate to="/movies?filter=missing" replace /> } ] },
      { path: "movies/:id", element: <Movie /> },
      { path: "*", element: <Navigate to="/" replace /> }
    ]
  }
]);`,
    'src/Deluno.Movies/Migrations/V0001Initial.cs': 'class V0001Initial {}',
    'src/Deluno.Movies/Migrations/V0002AddTags.cs': 'class V0002AddTags {}',
    'src/Deluno.Infrastructure/Storage/Migrations/SqliteSqlMigration.cs': 'class SqliteSqlMigration {}',
    'apps/windows-tray/TrayModel.cs': 'public enum TrayMenuId { Separator, Open, PauseResume, Quit }'
  });
  try {
    assert.deepEqual(webRouteItems(root).map((r) => r.key), ['web:/', 'web:/login', 'web:/movies', 'web:/movies/:id', 'web:/movies/wanted']);
    assert.deepEqual(migrationItems(root).map((m) => m.key), ['migration:movies/V0001', 'migration:movies/V0002']);
    assert.deepEqual(trayItems(root).map((t) => t.key), ['tray:Open', 'tray:PauseResume', 'tray:Quit']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the required level follows severity and what the row touches', () => {
  assert.equal(requiredFor(row({ severity: 'high', touches: 'other-app' })), 'real');
  assert.equal(requiredFor(row({ severity: 'high', touches: 'disk' })), 'real');
  assert.equal(requiredFor(row({ severity: 'high', touches: 'install' })), 'real');
  assert.equal(requiredFor(row({ severity: 'high', touches: 'none' })), 'inproc');
  assert.equal(requiredFor(row({ severity: 'medium', touches: 'other-app' })), 'inproc');
  assert.match(validateShape(register([row({ touches: 'moon' })])).join('\n'), /touches/);
  assert.match(validateShape(register([row({ issue: 'x' })])).join('\n'), /issue/);
  assert.deepEqual(validateShape(register([row({ issue: 1301 })])), []);
});

const STUBBED_SPEC = `
import { test } from '@playwright/test';
async function stubFiles(page) { await page.route('**/api/files', (r) => r.fulfill({ json: [] })); }
test('opens the real page', async ({ page }) => { await page.goto('/movies'); });
test('shows the stubbed files', async ({ page }) => { await stubFiles(page); await page.goto('/movies/1'); });
test('stubs inline', async ({ page }) => { await page.route('**/api/x', (r) => r.abort()); });
`;
const HOOKED_SPEC = `
import { test } from '@playwright/test';
test.beforeEach(async ({ page }) => { await page.route('**/api/y', (r) => r.fulfill({ json: {} })); });
test('every test here is stubbed', async ({ page }) => {});
`;

test('a smoke proof is real for a screen behaviour only when its API calls are not stubbed', () => {
  const root = makeRepo({ 'apps/web/tests/smoke/a.spec.ts': STUBBED_SPEC, 'apps/web/tests/smoke/b.spec.ts': HOOKED_SPEC });
  try {
    const index = new TestIndex(root);
    const a = 'apps/web/tests/smoke/a.spec.ts';
    assert.deepEqual(index.stubInfo(a, 'opens the real page'), { stubbed: false, decided: true });
    assert.deepEqual(index.stubInfo(a, 'shows the stubbed files'), { stubbed: true, decided: true });
    assert.deepEqual(index.stubInfo(a, 'stubs inline'), { stubbed: true, decided: true });
    assert.deepEqual(index.stubInfo('apps/web/tests/smoke/b.spec.ts', 'every test here is stubbed'), { stubbed: true, decided: true });
    assert.deepEqual(index.stubInfo(a, '*'), { stubbed: true, decided: false }, 'what cannot be decided counts as stubbed');
    assert.deepEqual(index.stubInfo(a, 'a title that is not there'), { stubbed: true, decided: false });

    const screen = (name, over = {}) => row({ area: 'library', kind: 'screen action', severity: 'high', touches: 'other-app', required: 'real', proofs: [{ type: 'smoke', file: a, name }], ...over });
    assert.equal(isGap(screen('opens the real page'), index), false);
    assert.equal(isGap(screen('shows the stubbed files'), index), true);
    assert.equal(isGap(screen('opens the real page', { area: 'api' }), index), true, 'only a screen behaviour may reach real through a smoke spec');
    assert.equal(isGap(screen('opens the real page'), null), true, 'without the index every smoke proof counts as stubbed');

    const t = totals(register([screen('opens the real page'), screen('shows the stubbed files', { id: 'x2' }), screen('*', { id: 'x3' })]).rows ? { ...register([]), rows: [screen('opens the real page'), screen('shows the stubbed files', { id: 'x2' }), screen('*', { id: 'x3' })] } : null, index);
    assert.deepEqual(t.smoke, { proofs: 3, unstubbed: 1, stubbed: 1, undecided: 1 });
    assert.deepEqual(t.highByTouches['other-app'], { rows: 3, gaps: 2 });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the generated section names the issues rows point at', () => {
  const rows = [row({ id: 'i1', issue: 1301 }), row({ id: 'i2', issue: 1301, proofs: [{ type: 'backend', file: 'A.cs', name: 'x' }] })];
  const reg = register(rows);
  const text = generatedSection(reg, [], totals(reg, null));
  assert.match(text, /#1301 \| 2 \| 1 \| 0/);
  assert.match(generatedSection(register([row()]), [], totals(register([row()]), null)), /No row names an issue yet/);
});

test('only an unmapped item or a missing proof fails the default run; the rest fails only under --strict', () => {
  const root = makeRepo({
    'tests/X.Tests/MovieListTests.cs': CS_TEST,
    'src/Deluno.Movies/MoviesEndpoints.cs': 'class E { void Map(IEndpointRouteBuilder endpoints) { endpoints.MapGet("/api/movies", List); } }'
  });
  const registerFile = join(root, 'docs/testing/coverage-register.json');
  const script = join(dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), 'check-coverage-register.mjs');
  const run = (rows, args = []) => {
    mkdirSync(dirname(registerFile), { recursive: true });
    writeFileSync(registerFile, JSON.stringify(register(rows)));
    return spawnSync(process.execPath, [script, '--root', root, ...args], { encoding: 'utf8' });
  };
  const good = { type: 'backend', file: 'tests/X.Tests/MovieListTests.cs', name: 'Listing_movies_answers_ok' };
  try {
    // A gap, a duplicate id, a stale cover and a proof of the wrong type: reported, not failed.
    const soft = run([
      row({ covers: ['route:GET /api/movies'] }),
      row({ covers: ['route:GET /api/gone'], proofs: [{ ...good, type: 'recording-replay' }] }),
      row({ id: 'api.other', severity: 'high', touches: 'other-app', required: 'real', proofs: [good] })
    ]);
    assert.equal(soft.status, 0, soft.stderr + soft.stdout);
    assert.equal(run([row({ covers: ['route:GET /api/movies'] })], ['--strict']).status, 1, '--strict fails on a gap');
    // (a) an unmapped item fails.
    assert.equal(run([row()]).status, 1);
    // (b) a proof that does not exist fails.
    const missing = run([row({ covers: ['route:GET /api/movies'], proofs: [{ ...good, name: 'A_test_that_was_renamed_away' }] })]);
    assert.equal(missing.status, 1);
    assert.match(missing.stderr, /\(b\)/);
    assert.equal(run([row({ covers: ['route:GET /api/movies'], proofs: [good] })]).status, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

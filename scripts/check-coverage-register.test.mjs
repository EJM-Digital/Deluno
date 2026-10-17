// Tests of the coverage-register check (scripts/check-coverage-register.mjs) and of the inventory it reads (scripts/coverage/inventory.mjs).
// Run: node --test scripts/check-coverage-register.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import {
  baseGapCount, evaluate, gapIncreaseShape, gapRatchet, jobSummary, statisticsReport, isGap, mapInventory, missingProofs, requiredFor, rowLevel, scenarioViolations, sleepViolations, staleIssueRows, totals, unownedGapRows, validateShape
} from './check-coverage-register.mjs';
import { TestIndex } from './coverage/testindex.mjs';
import { migrationItems, routeItems, trayItems, uiItems, webRouteItems } from './coverage/inventory.mjs';

function makeRepo(files) {
  const root = mkdtempSync(join(tmpdir(), 'coverage-register-'));
  for (const [rel, text] of Object.entries(files)) {
    const full = join(root, rel);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, text);
  }
  return root;
}

/** A fixture gap names an issue, as the register requires; a row with proofs is left as written. */
const owned = (r) => (r.proofs.length || r.issue != null ? r : { ...r, issue: 1284 });
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

test('a row marked incomplete is a gap whatever its proofs reach', () => {
  const unit = [{ type: 'vitest', file: 'a.test.ts', name: 'x' }];
  assert.equal(isGap(row({ proofs: unit })), false);
  assert.equal(isGap(row({ proofs: unit, incomplete: true })), true);
  assert.equal(rowLevel(row({ proofs: unit, incomplete: true })), 1, 'the level itself is unchanged');
  assert.deepEqual(validateShape(register([row({ proofs: unit, incomplete: true })])), []);
  for (const bad of [false, 'yes', 1]) assert.match(validateShape(register([row({ incomplete: bad })])).join(' '), /incomplete may only be true/);
});

test('a stale issue (no gap) and an unowned gap (no issue) are listed by row id', () => {
  const unit = [{ type: 'vitest', file: 'a.test.ts', name: 'x' }];
  const rows = [
    row({ id: 'a.stale', proofs: unit, issue: 1284 }),
    row({ id: 'a.owned-gap', issue: 1284 }),
    row({ id: 'a.unowned-gap' }),
    row({ id: 'a.unowned-incomplete', proofs: unit, incomplete: true }),
    row({ id: 'a.owned-incomplete', proofs: unit, incomplete: true, issue: 1284 }),
    row({ id: 'a.clean', proofs: unit })
  ];
  assert.deepEqual(staleIssueRows(register(rows)), ['a.stale']);
  assert.deepEqual(unownedGapRows(register(rows)), ['a.unowned-gap', 'a.unowned-incomplete']);
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
  assert.match(statisticsReport(register(rows), [], t), /\*\*4\*\* behaviours/);
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
    assert.deepEqual([result.staleIssues, result.unownedGaps], [[], []]);
    const flagged = evaluate({ register: register([{ ...rows[0], issue: 1284 }, row({ id: 'api.no-owner' })]), inventory, index, plan: [], root });
    assert.deepEqual([flagged.staleIssues, flagged.unownedGaps], [['api.movies.get-movies'], ['api.no-owner']]);
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

test('the statistics report names the issues rows point at', () => {
  const rows = [row({ id: 'i1', issue: 1301 }), row({ id: 'i2', issue: 1301, proofs: [{ type: 'backend', file: 'A.cs', name: 'x' }] })];
  const reg = register(rows);
  const text = statisticsReport(reg, [], totals(reg, null));
  assert.match(text, /#1301 \| 2 \| 1 \| 0/);
  assert.match(statisticsReport(register([row()]), [], totals(register([row()]), null)), /No row names an issue yet/);
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
    writeFileSync(registerFile, JSON.stringify(register(rows.map(owned))));
    return spawnSync(process.execPath, [script, '--root', root, ...args], { encoding: 'utf8' });
  };
  const good = { type: 'backend', file: 'tests/X.Tests/MovieListTests.cs', name: 'Listing_movies_answers_ok' };
  try {
    // A gap, a duplicate id, a stale cover and a proof of the wrong type: reported, not failed.
    const soft = run([
      row({ covers: ['route:GET /api/movies'] }),
      row({ covers: ['route:GET /api/gone'], proofs: [{ ...good, type: 'recording-replay' }] }),
      row({ id: 'api.other', severity: 'high', touches: 'other-app', required: 'real', proofs: [good], issue: 1284 })
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
    // (e) a stale issue on a row that is no gap fails; (f) a gap with no issue fails.
    const stale = run([row({ covers: ['route:GET /api/movies'], proofs: [good], issue: 1284 })]);
    assert.equal(stale.status, 1);
    assert.match(stale.stderr, /\(e\) 1 row\(s\) are not gaps but still name an issue[\s\S]*api\.movies\.get-movies/);
    const unowned = run([row({ covers: ['route:GET /api/movies'], proofs: [good], incomplete: true })]);
    assert.equal(unowned.status, 1);
    assert.match(unowned.stderr, /\(f\) 1 gap\(s\) name no issue[\s\S]*api\.movies\.get-movies/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------------------------- real-fs and vm (15 Oct 2026)

const REAL_FS_CS = `
public class ImportOnDiskTests
{
    [Fact]
    public async Task A_hard_link_is_made_on_a_real_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.mkv"), "x");
        Assert.True(File.Exists(Path.Combine(root, "a.mkv")));
    }

    [Fact]
    public async Task A_fake_file_system_is_not_real()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fs = new ScriptedStateFileSystem();
        File.Exists(root);
    }

    [Fact]
    public async Task An_in_memory_database_is_not_real()
    {
        var root = Path.GetTempPath();
        await using var connection = new SqliteConnection("Data Source=:memory:");
    }

    [Fact]
    public async Task Nothing_is_created()
    {
        File.Exists("/some/path");
    }

    [Fact]
    public async Task A_folder_is_made_but_nothing_is_done_with_it()
    {
        var root = Path.GetTempPath();
    }
}
`;
const REAL_FS_SETUP_CS = `
public class SetupCreatesTheFolderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("deluno").FullName;

    [Fact]
    public void A_file_is_written() { File.WriteAllText(Path.Combine(root, "a"), "x"); }

    public void Dispose() { Directory.Delete(root, true); }
}
`;
const REAL_FS_FILE = 'tests/X.Tests/RealFsTests.cs';
const SCENARIO_PROOF = { type: 'scenario', file: 'scripts/golden-path/G.ps1', name: 'install-first-run' };

test('real-fs is decided statically: a real temp folder or database file, real work on it, and no fake', () => {
  const root = makeRepo({ [REAL_FS_FILE]: REAL_FS_CS, 'tests/X.Tests/SetupTests.cs': REAL_FS_SETUP_CS });
  try {
    const index = new TestIndex(root);
    assert.equal(index.realFs(REAL_FS_FILE, 'A_hard_link_is_made_on_a_real_folder').real, true);
    assert.match(index.realFs(REAL_FS_FILE, 'A_fake_file_system_is_not_real').why, /fake or in-memory/);
    assert.match(index.realFs(REAL_FS_FILE, 'An_in_memory_database_is_not_real').why, /fake or in-memory/);
    assert.match(index.realFs(REAL_FS_FILE, 'Nothing_is_created').why, /creates no real temp folder/);
    assert.match(index.realFs(REAL_FS_FILE, 'A_folder_is_made_but_nothing_is_done_with_it').why, /no file or database work/);
    assert.equal(index.realFs(REAL_FS_FILE, 'A_test_that_is_not_there').real, false);
    // the class setup counts for its tests
    assert.equal(index.realFs('tests/X.Tests/SetupTests.cs', 'A_file_is_written').real, true);
    // a class name, a fragment and "*" are judged over every test they cover, and every one must qualify
    assert.equal(index.realFs(REAL_FS_FILE, 'ImportOnDiskTests').real, false);
    assert.equal(index.realFs(REAL_FS_FILE, '*').real, false);
    assert.equal(index.realFs('tests/X.Tests/SetupTests.cs', '*').real, true);
    assert.equal(index.realFs('tests/X.Tests/SetupTests.cs', 'SetupCreatesTheFolderTests').real, true);
    // only C# backend tests can be real-fs
    assert.equal(index.realFs('apps/web/tests/x.spec.ts', 'x').real, false);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a real-fs claim the checker cannot verify is refused: reported as a type problem and counted as inproc', () => {
  const root = makeRepo({ [REAL_FS_FILE]: REAL_FS_CS });
  try {
    const index = new TestIndex(root);
    const claim = (name) => ({ type: 'real-fs', file: REAL_FS_FILE, name });
    const disk = (over) => row({ id: 'd1', severity: 'high', touches: 'disk', required: 'real', ...over });
    const good = disk({ proofs: [claim('A_hard_link_is_made_on_a_real_folder')] });
    const refused = disk({ id: 'd2', proofs: [claim('A_fake_file_system_is_not_real')] });
    assert.equal(rowLevel(good, index), 2);
    assert.equal(isGap(good, index), false);
    assert.equal(rowLevel(refused, index), 1, 'a refused claim stays inproc');
    assert.equal(isGap(refused, index), true);
    assert.equal(rowLevel(good), 1, 'without an index nothing can be verified');
    const problems = missingProofs(register([good, refused]), index, null);
    assert.equal(problems.length, 1);
    assert.equal(problems[0].kind, 'type');
    assert.match(problems[0].why, /real-fs refused/);
    const t = totals(register([good, refused]), index);
    assert.equal(t.realFs.claims, 2);
    assert.equal(t.realFs.counted, 1);
    assert.equal(t.realFs.refused, 1);
    assert.equal(t.highGaps, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('real-fs satisfies real only for disk and install rows', () => {
  const root = makeRepo({ [REAL_FS_FILE]: REAL_FS_CS });
  try {
    const index = new TestIndex(root);
    const proofs = [{ type: 'real-fs', file: REAL_FS_FILE, name: 'A_hard_link_is_made_on_a_real_folder' }];
    for (const touches of ['disk', 'install']) assert.equal(isGap(row({ severity: 'high', touches, required: 'real', proofs }), index), false, touches);
    // another app needs the other app's recorded reply or a scenario, and a row that touches nothing is already met by inproc
    assert.equal(isGap(row({ severity: 'high', touches: 'other-app', required: 'real', proofs }), index), true);
    assert.equal(rowLevel(row({ severity: 'high', touches: 'none', required: 'inproc', proofs }), index), 1);
    const t = totals(register([row({ severity: 'high', touches: 'other-app', required: 'real', proofs })]), index);
    assert.equal(t.realFs.noEffect, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a vm row needs a scenario: real-fs and a recording stay inproc', () => {
  const root = makeRepo({ [REAL_FS_FILE]: REAL_FS_CS, 'tests/X.Tests/ATests.cs': CS_TEST });
  try {
    const index = new TestIndex(root);
    const vm = (proofs) => row({ severity: 'high', touches: 'install', required: 'real', vm: true, proofs });
    const realFs = { type: 'real-fs', file: REAL_FS_FILE, name: 'A_hard_link_is_made_on_a_real_folder' };
    const replay = { type: 'recording-replay', file: 'tests/X.Tests/ATests.cs', name: 'A_recorded_reply_is_read' };
    assert.equal(isGap(vm([realFs]), index), true);
    assert.equal(isGap(vm([replay]), index), true);
    assert.equal(isGap(vm([realFs, replay]), index), true);
    assert.equal(isGap(vm([realFs, SCENARIO_PROOF]), index), false);
    // the same proof on the same row without vm does reach real
    assert.equal(isGap(row({ severity: 'high', touches: 'install', required: 'real', proofs: [realFs] }), index), false);
    const t = totals(register([vm([realFs]), vm([SCENARIO_PROOF])]), index);
    assert.equal(t.realFs.vmRows, 2);
    assert.equal(t.realFs.vmRowGaps, 1);
    assert.equal(t.realFs.noEffect, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

const E2E_SPEC = ['import { test } from "@playwright/test";', 'test(`film.${kind}: a film through ${kind} is grabbed`, async () => {});', ''].join('\n');
test('an e2e proof is real for every touches kind, satisfies a vm row, and must exist under tests/e2e', () => {
  const root = makeRepo({ 'tests/e2e/films.spec.ts': E2E_SPEC, 'tests/e2e/playwright.config.ts': 'export default {};\n', 'tests/e2e-windows/tray.spec.ts': "test('tray: goes amber', async () => {});\n" });
  try {
    const index = new TestIndex(root);
    const e2e = { type: 'e2e', file: 'tests/e2e/films.spec.ts', name: 'film.${kind}: a film through ${kind} is grabbed' };
    for (const touches of ['other-app', 'disk', 'install', 'none']) {
      assert.equal(isGap(row({ severity: 'high', touches, required: touches === 'none' ? 'inproc' : 'real', proofs: [e2e] }), index), false, touches);
    }
    assert.equal(isGap(row({ severity: 'high', touches: 'disk', required: 'real', vm: true, proofs: [e2e] }), index), false);
    const win = { type: 'windows-e2e', file: 'tests/e2e-windows/tray.spec.ts', name: 'tray: goes amber' };
    assert.equal(isGap(row({ severity: 'high', touches: 'install', required: 'real', vm: true, proofs: [win] }), index), false);
    assert.deepEqual(missingProofs(register([row({ proofs: [e2e, win] })]), index, new Set()), []);
    const bad = [
      { ...e2e, name: 'film.nothing: not a test in the file' },
      { ...e2e, file: 'tests/e2e/gone.spec.ts' },
      { ...e2e, file: 'tests/e2e-windows/tray.spec.ts', name: 'tray: goes amber' },
      { ...win, file: 'tests/e2e/films.spec.ts' }
    ];
    assert.deepEqual(missingProofs(register(bad.map((p, i) => row({ id: `b${i}`, proofs: [p] }))), index, new Set()).map((m) => m.kind), ['missing', 'missing', 'type', 'type']);
    // the playwright config is not a test file
    assert.equal(index.has('tests/e2e/playwright.config.ts'), false);
    assert.equal(validateShape(register([row({ proofs: [e2e, win] })])).length, 0, 'e2e and windows-e2e are known proof types');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('vm is only true, and only on a row that touches disk or install', () => {
  assert.deepEqual(validateShape(register([row({ severity: 'high', touches: 'install', required: 'real', vm: true })])), []);
  assert.deepEqual(validateShape(register([row({ severity: 'high', touches: 'disk', required: 'real', vm: true })])), []);
  assert.match(validateShape(register([row({ vm: true })])).join('\n'), /vm is for a row that touches disk or install/);
  assert.match(validateShape(register([row({ severity: 'high', touches: 'other-app', required: 'real', vm: true })])).join('\n'), /vm is for a row that touches disk or install/);
  assert.match(validateShape(register([row({ severity: 'high', touches: 'disk', required: 'real', vm: false })])).join('\n'), /vm may only be true/);
  assert.deepEqual(validateShape(register([row({ proofs: [{ type: 'real-fs', file: 'a.cs', name: 'x' }] })])), [], 'real-fs is a known proof type');
});

test('account names the outside service a gap waits for, and only on a gap that names an issue', () => {
  assert.deepEqual(validateShape(register([row({ issue: 1343, account: 'Trakt' })])), []);
  assert.match(validateShape(register([row({ issue: 1343, account: '' })])).join(' '), /account must name the outside service/);
  assert.match(validateShape(register([row({ account: 'Trakt' })])).join(' '), /account is only for a gap that names an issue/);
});

test('the statistics report reports the real-fs proofs and the vm rows', () => {
  const rows = [row({ id: 'v1', severity: 'high', touches: 'install', required: 'real', vm: true })];
  const text = statisticsReport(register(rows), [], totals(register(rows)));
  assert.match(text, /Real-fs proofs: \*\*0\*\* claimed/);
  assert.match(text, /\*\*1\*\* rows are "vm"/);
});

// ---------------------------------------------------------------------------------------------- container-smoke (15 Oct 2026; the same type and rule as Weir's register)

const WORKFLOW = '.github/workflows/ci-packaging.yml';
const WORKFLOW_STEPS =
  'jobs:\n  docker-smoke:\n    steps:\n' +
  '      - name: Start Deluno Docker candidate\n        run: docker run -d --name deluno -p 18080:8080 "$IMAGE"\n' +
  '      - name: Start from the compose file\n        run: docker compose -f compose.yaml -p proof up -d\n' +
  '      - name: Trivy scan of the Docker candidate\n        run: trivy image deluno:candidate\n' +
  '      - name: Validate Compose contract\n        run: docker compose config --quiet\n' +
  '      - name: Cleanup\n        run: docker rm -f deluno\n';
const dockerRow = (name, over = {}) => row({ id: 'k1', area: 'api', severity: 'high', touches: 'install', required: 'real', proofs: [{ type: 'container-smoke', file: WORKFLOW, name }], ...over });

test('container-smoke: a step that starts the container is real for an install row; a step that only builds or scans is refused', () => {
  const root = makeRepo({ [WORKFLOW]: WORKFLOW_STEPS });
  try {
    const index = new TestIndex(root);
    for (const step of ['Start Deluno Docker candidate', 'Start from the compose file']) {
      assert.equal(index.containerSmoke(WORKFLOW, step).real, true, step);
      assert.equal(isGap(dockerRow(step), index), false, step);
    }
    for (const step of ['Trivy scan of the Docker candidate', 'Validate Compose contract', 'Cleanup']) {
      const verdict = index.containerSmoke(WORKFLOW, step);
      assert.equal(verdict.real, false, step);
      assert.match(verdict.why, /does not start the container image/);
      assert.equal(rowLevel(dockerRow(step), index), 1, 'a refused step stays inproc');
    }
    assert.match(index.containerSmoke(WORKFLOW, 'A step nobody wrote').why, /no step named/);
    assert.equal(rowLevel(dockerRow('Start Deluno Docker candidate')), 1, 'without an index nothing can be verified');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('container-smoke: a script is judged as a whole, and must be a container script or a workflow', () => {
  const script = 'scripts/test-docker-thing.sh';
  const root = makeRepo({ [script]: 'docker run --detach --name x "$1"\n', 'scripts/check-docker-thing.sh': 'echo nothing started\n', [WORKFLOW]: WORKFLOW_STEPS });
  try {
    const index = new TestIndex(root);
    assert.equal(index.containerSmoke(script, '*').real, true);
    assert.equal(index.containerSmoke('scripts/check-docker-thing.sh', '*').real, false);
    const reg = register([
      row({ id: 'k1', severity: 'high', touches: 'install', required: 'real', proofs: [{ type: 'container-smoke', file: script, name: '*' }] }),
      row({ id: 'k2', severity: 'high', touches: 'install', required: 'real', proofs: [{ type: 'container-smoke', file: 'tests/X.Tests/RealFsTests.cs', name: 'x' }] }),
      row({ id: 'k3', severity: 'high', touches: 'install', required: 'real', proofs: [{ type: 'container-smoke', file: WORKFLOW, name: 'A step nobody wrote' }] })
    ]);
    const problems = missingProofs(reg, index, null);
    assert.match(problems.map((p) => p.why).join('\n'), /does not exist/, 'a cs file not in the repo is missing');
    assert.match(problems.find((p) => p.row === 'k3').why, /no step named "A step nobody wrote"/);
    const cs = makeRepo({ 'tests/X.Tests/RealFsTests.cs': REAL_FS_CS });
    try {
      assert.match(missingProofs(reg, new TestIndex(cs), null).find((p) => p.row === 'k2').why, /container-smoke proof must be a packaging workflow or a container script/);
    } finally {
      rmSync(cs, { recursive: true, force: true });
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('container-smoke does not count for a vm row or a row that touches no disk or install, and the totals count it', () => {
  const root = makeRepo({ [WORKFLOW]: WORKFLOW_STEPS });
  try {
    const index = new TestIndex(root);
    const start = 'Start Deluno Docker candidate';
    assert.equal(rowLevel(dockerRow(start, { vm: true }), index), 1);
    assert.equal(rowLevel(dockerRow(start, { touches: 'other-app' }), index), 1);
    assert.equal(isGap(dockerRow(start, { vm: true }), index), true);
    const reg = register([dockerRow(start), dockerRow('Trivy scan of the Docker candidate', { id: 'k2' }), dockerRow(start, { id: 'k3', touches: 'other-app' })]);
    const t = totals(reg, index);
    assert.deepEqual({ ...t.container }, { claims: 3, counted: 1, refused: 1, noEffect: 1 });
    assert.deepEqual(validateShape(reg), [], 'container-smoke is a known proof type');
    assert.match(statisticsReport(reg, [], t), /Container-smoke proofs: \*\*3\*\* claimed; \*\*1\*\* verified/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// ----------------------------------------------------------------------------------- the gap ratchet against the base branch (15 Oct 2026)

test('the shape refuses gapCeiling and a malformed gapIncrease, and accepts a reasoned one', () => {
  const reg = (extra) => ({ ...register([row()]), ...extra });
  assert.deepEqual(validateShape(reg({})), []);
  assert.ok(validateShape(reg({ gapCeiling: 5201 })).some((p) => /gapCeiling/.test(p)), 'a stale gapCeiling is refused, not read');
  assert.deepEqual(validateShape(reg({ gapIncrease: { from: 3, to: 5, reason: 'two rows wait for a screen' } })), []);
  for (const bad of [null, {}, { from: 3, to: 3, reason: 'x' }, { from: 5, to: 3, reason: 'x' }, { from: 3, to: 5 }, { from: 3, to: 5, reason: '  ' }, { from: '3', to: 5, reason: 'x' }, { from: -1, to: 5, reason: 'x' }]) {
    assert.equal(validateShape(reg({ gapIncrease: bad })).length, 1, JSON.stringify(bad));
    assert.equal(gapIncreaseShape(bad).length, 1);
  }
});

test('the ratchet: fewer or equal gaps pass, more fail unless a gapIncrease matches that rise exactly, and no base passes with a note', () => {
  const reg = (gapIncrease) => ({ ...register([]), ...(gapIncrease ? { gapIncrease } : {}) });
  assert.deepEqual(gapRatchet(reg(), 4, 5), { problems: [], notes: [] });
  assert.deepEqual(gapRatchet(reg(), 5, 5), { problems: [], notes: [] });
  const more = gapRatchet(reg(), 6, 5);
  assert.equal(more.problems.length, 1);
  assert.match(more.problems[0], /6 gaps, more than the 5/);
  assert.match(more.problems[0], /"from": 5, "to": 6/, 'it names the entry to write');
  const reason = 'a screen cannot be tested yet';
  const allowed = gapRatchet(reg({ from: 5, to: 6, reason }), 6, 5);
  assert.deepEqual(allowed.problems, []);
  assert.match(allowed.notes[0], new RegExp(`5 to 6: ${reason}`));
  for (const entry of [{ from: 4, to: 6, reason }, { from: 5, to: 7, reason }, { from: 5, to: 6, reason: ' ' }]) {
    assert.equal(gapRatchet(reg(entry), 6, 5).problems.length, 1, JSON.stringify(entry));
  }
  assert.match(gapRatchet(reg({ from: 5, to: 7, reason }), 6, 5).problems[0], /says 5 to 7, which is not this rise/);
  // A leftover entry changes nothing when the gaps did not rise.
  assert.deepEqual(gapRatchet(reg({ from: 5, to: 6, reason }), 5, 5), { problems: [], notes: [] });
  for (const none of [null, undefined]) assert.match(gapRatchet(reg(), 99, none).notes[0], /No register on the base branch/);
  assert.deepEqual(gapRatchet(reg(), 99, null).problems, []);
});

test('the base register is counted from git with this checkout tests; no ref, no register or a broken one is null', () => {
  const root = makeRepo({ 'tests/X.Tests/MovieListTests.cs': CS_TEST });
  const git = (...args) => spawnSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.com', '-c', 'commit.gpgsign=false', ...args], { cwd: root, encoding: 'utf8' });
  try {
    git('init', '-q');
    const good = { type: 'backend', file: 'tests/X.Tests/MovieListTests.cs', name: 'Listing_movies_answers_ok' };
    const commit = (text, message) => {
      mkdirSync(join(root, 'docs/testing'), { recursive: true });
      writeFileSync(join(root, 'docs/testing/coverage-register.json'), text);
      git('add', '-A');
      assert.equal(git('commit', '-q', '-m', message).status, 0);
    };
    commit('not json', 'broken');
    assert.equal(baseGapCount(root, 'HEAD', new TestIndex(root)), null);
    commit(JSON.stringify({ version: 1 }), 'no rows');
    assert.equal(baseGapCount(root, 'HEAD', new TestIndex(root)), null);
    commit(JSON.stringify({ ...register([row({ id: 'a' }), row({ id: 'b', proofs: [good] }), row({ id: 'c' })]), gapCeiling: 99 }), 'two gaps');
    assert.equal(baseGapCount(root, 'HEAD', new TestIndex(root)), 2, 'a gapCeiling left in the base is ignored');
    assert.equal(baseGapCount(root, 'no-such-ref', new TestIndex(root)), null);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('--base fails a rise in the gaps, allows a reasoned one, and --report writes the statistics without touching the docs', () => {
  const root = makeRepo({
    'tests/X.Tests/MovieListTests.cs': CS_TEST,
    'src/Deluno.Movies/MoviesEndpoints.cs': 'class E { void Map(IEndpointRouteBuilder endpoints) { endpoints.MapGet("/api/movies", List); } }',
    'docs/testing/COVERAGE.md': 'hand-written\n'
  });
  const registerFile = join(root, 'docs/testing/coverage-register.json');
  const script = join(dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), 'check-coverage-register.mjs');
  const git = (...args) => spawnSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.com', '-c', 'commit.gpgsign=false', ...args], { cwd: root, encoding: 'utf8' });
  const write = (rows, extra = {}) => {
    mkdirSync(dirname(registerFile), { recursive: true });
    writeFileSync(registerFile, JSON.stringify({ ...register(rows.map(owned)), ...extra }));
  };
  const run = (args, env = {}) => spawnSync(process.execPath, [script, '--root', root, ...args], { encoding: 'utf8', env: { ...process.env, GITHUB_STEP_SUMMARY: '', ...env } });
  const cover = row({ covers: ['route:GET /api/movies'] });
  const three = [cover, row({ id: 'extra.one' }), row({ id: 'extra.two' })];
  try {
    git('init', '-q');
    write([cover, row({ id: 'extra.one' })]);
    git('add', '-A');
    assert.equal(git('commit', '-q', '-m', 'base: two gaps').status, 0);

    assert.equal(run(['--base', 'HEAD']).status, 0, 'the same gaps pass');
    write([cover]);
    assert.match(run(['--base', 'HEAD']).stdout, /\(d\) 1 gaps, no more than the 2 at HEAD/);

    write(three);
    const more = run(['--base', 'HEAD']);
    assert.equal(more.status, 1);
    assert.match(more.stderr, /\(d\) the gaps/);
    assert.match(more.stderr, /3 gaps, more than the 2/);
    assert.equal(run([]).status, 0, 'without --base nothing is held to a count');
    assert.match(run([]).stdout, /no --base given/);

    write(three, { gapIncrease: { from: 2, to: 3, reason: 'a screen is not testable yet' } });
    const allowed = run(['--base', 'HEAD']);
    assert.equal(allowed.status, 0, allowed.stderr + allowed.stdout);
    assert.match(allowed.stdout, /The gaps rise from 2 to 3: a screen is not testable yet/);
    write(three, { gapIncrease: { from: 2, to: 4, reason: 'wrong rise' } });
    assert.equal(run(['--base', 'HEAD']).status, 1);

    // A ref with no register to compare with passes with a note.
    write(three);
    assert.match(run(['--base', 'no-such-ref']).stdout, /no register at no-such-ref/);
    assert.equal(run(['--base', 'no-such-ref']).status, 0);

    // --report writes the statistics; the docs are left alone.
    const reported = run(['--report']);
    assert.equal(reported.status, 0, reported.stderr);
    const text = readFileSync(join(root, 'artifacts/coverage-register.md'), 'utf8');
    assert.match(text, /behaviours\. \*\*\d+\*\* reach their required proof level/);
    assert.match(text, /### By area/);
    run(['--report', 'out/custom.md']);
    assert.match(readFileSync(join(root, 'out/custom.md'), 'utf8'), /### By severity/);
    assert.equal(run(['--write-doc']).status, 0, 'the removed flag does nothing');
    assert.equal(readFileSync(join(root, 'docs/testing/COVERAGE.md'), 'utf8'), 'hand-written\n');

    // The CI job summary gets the statistics and the notes.
    write(three, { gapIncrease: { from: 2, to: 3, reason: 'a screen is not testable yet' } });
    const summary = join(root, 'summary.md');
    run(['--base', 'HEAD'], { GITHUB_STEP_SUMMARY: summary });
    const summaryText = readFileSync(summary, 'utf8');
    assert.match(summaryText, /### Coverage register/);
    assert.match(summaryText, /- The gaps rise from 2 to 3: a screen is not testable yet/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the job summary shows the statistics, then each note', () => {
  const reg = register([row()]);
  const text = jobSummary(reg, [], totals(reg, null), ['first note', 'second note']);
  assert.match(text, /^### Coverage register/);
  assert.match(text, /- first note\n\n- second note\n$/);
});

test('every menu, drawer and dialog component is a surface, and each named control inside it is an action', () => {
  const root = makeRepo({
    'apps/web/src/components/app/job-drawer.tsx': [
      'export function JobDrawer() {',
      '  return (<Drawer open title="Job">',
      '    <Button onClick={() => void retryJob(id)}>Retry <span>now</span></Button>',
      '    <Button aria-label="Close the job" onClick={close} />',
      '    <Switch aria-label={"Keep seeding"} onCheckedChange={(v) => setKeep(v)} />',
      '    <Button onClick={(event) => handleCancel(event)}>{label}</Button>',
      '    <Input placeholder="Search jobs" onChange={(e) => setQuery(e.target.value)} />',
      '  </Drawer>);',
      '}'
    ].join('\n'),
    'apps/web/src/components/app/plain-card.tsx': 'export const Card = () => <Button>Never listed</Button>;',
    'apps/web/src/components/app/job-drawer.test.tsx': '<Drawer><Button>Test file is ignored</Button></Drawer>'
  });
  try {
    const items = uiItems(root);
    assert.deepEqual(items.filter((i) => i.kind === 'uisurface').map((i) => i.key), ['ui:components/app/job-drawer.tsx']);
    assert.deepEqual(items.filter((i) => i.kind === 'uiaction').map((i) => i.key.split('#')[1]), [
      'Button:Retry now', 'Button:Close the job', 'Switch:Keep seeding', 'Button:onClick:handleCancel', 'Input:Search jobs'
    ]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

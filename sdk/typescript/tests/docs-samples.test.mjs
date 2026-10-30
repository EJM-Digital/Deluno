// The TypeScript code samples in docs/automation-integrations.md must compile against the SDK they show (sdk/typescript/src).
// Run with `node --test sdk/typescript/tests/docs-samples.test.mjs`.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..');
const require = createRequire(import.meta.url);
const ts = require('typescript');

const doc = readFileSync(path.join(repoRoot, 'docs', 'automation-integrations.md'), 'utf8');
const samples = [...doc.matchAll(/```ts\r?\n([\s\S]*?)```/g)].map((match) => match[1]);

test('the document has at least one TypeScript sample, so this test is not passing on nothing', () => {
  assert.ok(samples.length >= 1);
});

test('every TypeScript sample in docs/automation-integrations.md type-checks against the SDK', () => {
  const folder = mkdtempSync(path.join(tmpdir(), 'deluno-doc-sample-'));
  try {
    const files = samples.map((sample, index) => {
      // The compiler reports file names with forward slashes, whatever the platform.
      const file = path.join(folder, `sample${index}.ts`).split(path.sep).join('/');
      writeFileSync(file, sample);
      return file;
    });
    const program = ts.createProgram(files, {
      target: ts.ScriptTarget.ES2022,
      lib: ['lib.dom.d.ts', 'lib.es2022.d.ts'],
      module: ts.ModuleKind.ESNext,
      moduleResolution: ts.ModuleResolutionKind.Bundler,
      strict: true,
      noEmit: true,
      skipLibCheck: true,
      types: ['node'],
      typeRoots: [path.join(repoRoot, 'node_modules', '@types')],
      baseUrl: folder.split(path.sep).join('/'),
      paths: { '@deluno/api-client': [path.join(repoRoot, 'sdk', 'typescript', 'src', 'index.ts')] }
    });
    const problems = ts.getPreEmitDiagnostics(program)
      .filter((diagnostic) => diagnostic.file && files.includes(diagnostic.file.fileName))
      .map((diagnostic) => `${path.basename(diagnostic.file.fileName)}: ${ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n')}`);
    assert.deepEqual(problems, []);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

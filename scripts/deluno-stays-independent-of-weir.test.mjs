// node --test scripts/deluno-stays-independent-of-weir.test.mjs
// Deluno stays independent of Weir (coverage register, platform-install): nothing in Deluno's hand-off, report or connection
// code is Weir-specific, so a generic webhook processor works the same. The code lines (comments and SQL comments aside) of the
// files below name no Weir, bar the one place that picks the release feed for the processor Deluno's own installer sets up.
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const at = (...parts) => join(root, ...parts);

const folders = [
  at('src', 'Deluno.Integrations', 'Processors'),
  at('src', 'Deluno.Platform', 'Processing'),
];
const platformFiles = readdirSync(at('src', 'Deluno.Platform', 'Contracts')).filter((f) => /^Processor.*\.cs$/.test(f)).map((f) => at('src', 'Deluno.Platform', 'Contracts', f));
const files = [
  ...folders.flatMap((folder) => readdirSync(folder, { recursive: true }).filter((f) => String(f).endsWith('.cs')).map((f) => join(folder, String(f)))),
  ...platformFiles,
  at('src', 'Deluno.Platform', 'Data', 'SqliteProcessorRepository.cs'),
  at('src', 'Deluno.Platform', 'ProcessorHandoffCancellation.cs'),
  at('src', 'Deluno.Platform', 'ProcessorOutcomeDelivery.cs'),
];

/** Lines that are code: not a comment, and not inside a block comment or a SQL comment line. */
export const codeLines = (text) => {
  const found = [];
  let inBlock = false;
  text.split(/\r?\n/).forEach((line, index) => {
    let code = line;
    if (inBlock) {
      const end = code.indexOf('*/');
      if (end < 0) return;
      inBlock = false;
      code = code.slice(end + 2);
    }
    code = code.replace(/\/\*.*?\*\//g, '');
    if (code.includes('/*')) { inBlock = true; code = code.slice(0, code.indexOf('/*')); }
    code = code.replace(/\/\/.*$/, '').replace(/^\s*--.*$/, '');
    if (code.trim()) found.push({ number: index + 1, code });
  });
  return found;
};

const allowed = new Map([
  // The one place that matches a processor to a release feed: only the one Deluno's own installer sets up (the intake route with a /ready health address).
  ['src/Deluno.Platform/Contracts/ProcessorConnectionItem.cs', [/ConnectionProjects\.Weir/]],
]);

test('the hand-off, report and connection code names no Weir except where a release feed is picked', () => {
  assert.ok(files.length > 20, `expected the whole processor code, found ${files.length} files`);
  const named = [];
  for (const file of files) {
    const short = relative(root, file).split(sep).join('/');
    for (const { number, code } of codeLines(readFileSync(file, 'utf8'))) {
      if (!/weir/i.test(code)) continue;
      if ((allowed.get(short) ?? []).some((pattern) => pattern.test(code))) continue;
      named.push(`${short}:${number}: ${code.trim()}`);
    }
  }
  assert.deepEqual(named, []);
});

test('the guard sees a Weir written into code, and ignores one in a comment', () => {
  const sample = ['// Weir in a line comment', '/* Weir in a block', '   still Weir */', 'var name = "Weir";', 'var x = 1; // Weir after code', '-- Weir in SQL'].join('\n');
  assert.deepEqual(codeLines(sample).map((l) => l.number), [4, 5]);
  assert.deepEqual(codeLines(sample).filter((l) => /weir/i.test(l.code)).map((l) => l.number), [4]);
});

test('an address is read from the generic intake shape, not from a processor name', () => {
  const source = readFileSync(at('src', 'Deluno.Platform', 'Contracts', 'ProcessorConnectionItem.cs'), 'utf8');
  assert.match(source, /IntakePrefix = "\/api\/v1\/intake\/webhook\/"/);
});

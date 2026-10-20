// Narrows a project's mutation run to the test classes that mention its types (issue 1269).
//
// Stryker.NET runs the whole test suite once before it mutates anything and again to capture which test touches which mutant. The
// suites hold about 12 000 tests, and on 19 October those two runs took three hours for a 1 300-line project. A test class that
// never names a type of the project cannot kill one of its mutants in a way a test of the project would not, so the run is limited
// to the classes that do. A project that most of the suite mentions (Contracts, Infrastructure) keeps the whole suite.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

const SKIP = new Set(["bin", "obj", "node_modules", "fixtures", "StrykerOutput"]);
const TYPE = /\b(?:class|record|interface|struct|enum)\s+(?:struct\s+|class\s+)?([A-Z]\w{3,})/g;
const HAS_TEST = /\[(?:\w+\.)?(?:Fact|Theory)\b/;
const TEST_CLASS = /\bclass\s+(\w+)/g;

function* csFiles(dir) {
  if (!existsSync(dir)) return;
  for (const name of readdirSync(dir)) {
    if (SKIP.has(name)) continue;
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) yield* csFiles(full);
    else if (name.endsWith(".cs")) yield full;
  }
}

/** The type names a production project declares. */
export function typeNames(root, projectDir) {
  const names = new Set();
  for (const file of csFiles(path.join(root, projectDir))) {
    for (const m of readFileSync(file, "utf8").matchAll(TYPE)) names.add(m[1]);
  }
  return names;
}

/** { name, text } for every class that holds a test: text is the whole file, which is what names the types the tests use. */
export function testClasses(root) {
  const out = [];
  for (const file of csFiles(path.join(root, "tests"))) {
    const text = readFileSync(file, "utf8");
    if (!HAS_TEST.test(text)) continue;
    const starts = [...text.matchAll(TEST_CLASS)];
    starts.forEach((m, i) => {
      const body = text.slice(m.index, i + 1 < starts.length ? starts[i + 1].index : text.length);
      if (HAS_TEST.test(body)) out.push({ name: m[1], text });
    });
  }
  return out;
}

/**
 * The vstest filter limited to the test classes that name one of the project's types, or null when more than `cap` classes do
 * (the project is used by most of the suite) or when none does.
 */
export function filterFor(names, classes, cap = 300) {
  if (names.size === 0) return null;
  const pattern = new RegExp(String.raw`\b(?:${[...names].join("|")})\b`);
  const hits = [...new Set(classes.filter((c) => pattern.test(c.text)).map((c) => c.name))].sort();
  if (hits.length === 0 || hits.length > cap) return null;
  return hits.map((name) => `FullyQualifiedName~${name}`).join("|");
}

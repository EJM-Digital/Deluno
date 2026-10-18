import test from "node:test";
import assert from "node:assert/strict";
import { compare, parseList } from "./check-persistence-parts.mjs";
import { PARTS, filterFor } from "./persistence-parts.mjs";

test("a clean split has no problems", () => {
  assert.deepEqual(compare(["a", "b", "c"], [["a"], ["b", "c"]]), []);
});
test("a test in no part, in two parts, or invented is reported", () => {
  const problems = compare(["a", "b"], [["a", "x"], ["a"]]);
  assert.equal(problems.length, 3);
  assert.match(problems.join("\n"), /"a" runs in part 1 and part 2/);
  assert.match(problems.join("\n"), /"b" runs in no part/);
  assert.match(problems.join("\n"), /"x" is in part 1 but not in the whole/);
});
test("an empty whole list proves nothing", () => {
  assert.equal(compare([], [[], []]).length, 1);
});
test("the list is read after the header", () => {
  assert.deepEqual(parseList("Build ok\nThe following Tests are available:\n    A.B\n    C.D\n"), ["A.B", "C.D"]);
  assert.throws(() => parseList("nothing"));
});
test("Api is split by the first letter of the class, and the last Api part is every other class", () => {
  assert.equal(filterFor(1), "FullyQualifiedName~Deluno.Persistence.Tests.Api.A");
  assert.match(filterFor(2), /Api\.B\|.*Api\.F$/);
  const rest = filterFor(4);
  assert.match(rest, /^FullyQualifiedName~Deluno\.Persistence\.Tests\.Api\.&/);
  for (const letter of ["A", "B", "F", "G", "Q"]) assert.match(rest, new RegExp(`!~Deluno\.Persistence\.Tests\.Api\.${letter}(&|$)`));
  assert.doesNotMatch(rest, /!~Deluno\.Persistence\.Tests\.Api\.R/);
});
test("no letter is named by two Api parts, and each Api part names some", () => {
  const letters = PARTS.flatMap((p) => p.classLetters ?? []);
  assert.equal(new Set(letters).size, letters.length);
  for (const p of PARTS.filter((x) => x.namespaces?.[0] === "Api" && x.classLetters !== null)) assert.ok(p.classLetters.length > 0);
});
test("the last part is every namespace the others do not name", () => {
  const rest = filterFor(PARTS.length);
  for (const n of ["Api", "Integrations", "Platform", "Quality", "Jobs", "Security"]) assert.match(rest, new RegExp(`!~Deluno\.Persistence\.Tests\.${n}\.`));
  assert.equal((rest.match(/Api\./g) ?? []).length, 1);
  assert.throws(() => filterFor(0));
  assert.throws(() => filterFor(PARTS.length + 1));
});

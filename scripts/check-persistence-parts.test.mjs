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
test("the last part is every namespace the others do not name", () => {
  assert.equal(filterFor(1), "FullyQualifiedName~Deluno.Persistence.Tests.Api.");
  const rest = filterFor(PARTS.length);
  for (const n of ["Api", "Integrations", "Platform", "Quality", "Jobs", "Security"]) assert.match(rest, new RegExp(`!~Deluno\.Persistence\.Tests\.${n}\.`));
  assert.throws(() => filterFor(0));
  assert.throws(() => filterFor(PARTS.length + 1));
});

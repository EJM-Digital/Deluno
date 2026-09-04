import { describe, expect, it } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const sourceRoot = path.resolve(here, "..");

/**
 * Buttons that can be pressed.
 *
 * The notifications bell shipped with an `aria-label`, an icon, a red dot that
 * appeared whenever a job had failed — and no `onClick`. It rendered perfectly,
 * type-checked, and photographed fine. It was only wrong when a person pressed
 * it, which is why 304 passing tests and a coverage inventory of every route and
 * screen all missed it: neither counts controls.
 *
 * A button with an alert badge that swallows the click is worse than a disabled
 * one, so this holds the whole app to the rule rather than that one button.
 */
describe("interactive controls", () => {
  const tsxFiles = collectTsxFiles(sourceRoot);

  it("has files to check", () => {
    expect(tsxFiles.length).toBeGreaterThan(50);
  });

  it("gives every labelled button a way to be pressed", () => {
    const dead: string[] = [];

    for (const file of tsxFiles) {
      const source = fs.readFileSync(file, "utf8");
      for (const match of source.matchAll(/<button\b(?:(?!<\/button>|<button\b)[\s\S])*?>/g)) {
        const tag = match[0];
        if (!tag.includes("aria-label")) continue;

        // A submit button is pressed by its form; a spread may carry handlers
        // injected by a caller; and a Radix `asChild` trigger clones its own
        // handler onto the child, so the button really is pressable even though
        // nothing on the tag says so.
        const precedingMarkup = source.slice(Math.max(0, match.index - 200), match.index);
        const wired =
          /on(?:Click|PointerDown|MouseDown|KeyDown)/.test(tag) ||
          /type=["']submit["']/.test(tag) ||
          tag.includes("{...") ||
          /Trigger\s+asChild\s*>\s*$/.test(precedingMarkup);
        if (wired) continue;

        const line = source.slice(0, match.index).split("\n").length;
        const label = /aria-label=["{]([^"}]+)/.exec(tag)?.[1] ?? "unlabelled";
        dead.push(`${path.relative(sourceRoot, file).replace(/\\/g, "/")}:${line} — "${label}"`);
      }
    }

    expect(dead, `these buttons carry a label and cannot be pressed:\n${dead.join("\n")}`).toEqual([]);
  });
});

function collectTsxFiles(directory: string): string[] {
  const found: string[] = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "node_modules") continue;
      found.push(...collectTsxFiles(full));
    } else if (entry.name.endsWith(".tsx") && !entry.name.endsWith(".test.tsx")) {
      found.push(full);
    }
  }
  return found;
}

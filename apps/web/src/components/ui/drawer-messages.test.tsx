import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { DrawerFooter, type DrawerSaveState } from "./drawer";

/**
 * Every message a drawer sets is shown, whatever its state (#1338). The metadata drawer set "3 matches found" and the movie
 * edit drawer set "Could not load libraries and quality profiles." in the clean state, where the footer drew only a note.
 * The footer still hides a leftover save message in the clean state (#1151), so a drawer that speaks while clean says so
 * as its note.
 */
describe("a drawer footer shows what a drawer says", () => {
  it.each<DrawerSaveState>(["saved", "error"])("a message in the %s state", (state) => {
    render(<DrawerFooter state={state} message="3 matches found" saveLabel="Save" onCancel={() => undefined} />);

    expect(screen.getByRole("status")).toHaveTextContent("3 matches found");
  });

  it("a note in the clean state", () => {
    render(<DrawerFooter state="clean" note="3 matches found" saveLabel="Save" onCancel={() => undefined} />);

    expect(screen.getByRole("status")).toHaveTextContent("3 matches found");
  });
});

/**
 * Source scan: a component that keeps a message in state and has a drawer footer must read that message somewhere (the footer's
 * message=, or its own text on the screen). A message that is only ever set, never read, is shown in no state (#1338).
 */
describe("no drawer sets a message that nothing reads", () => {
  const root = join(__dirname, "..", "..");
  const sources: string[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else if (/\.tsx$/.test(name) && !/\.test\./.test(name)) sources.push(full);
    }
  };
  walk(root);

  it("reads every message a drawer keeps", () => {
    const drawers = sources.filter((file) => {
      const text = readFileSync(file, "utf8");
      return /const \[message, setMessage\] = useState/.test(text) && text.includes("<DrawerFooter");
    });
    expect(drawers.length).toBeGreaterThan(0);
    for (const file of drawers) {
      const reads = readFileSync(file, "utf8")
        .replace(/const \[message, setMessage\] = useState.*/g, "")
        .replace(/setMessage\(/g, "");
      expect(reads, `${file} sets a message that nothing on the screen reads`).toMatch(/\bmessage\b(?!\s*[:?]?\s*string)/);
    }
  });

  it("hands the metadata drawer's and the film edit drawer's message to their footers, saved or not", () => {
    for (const name of ["app/media-metadata-drawer.tsx", "app/movie-edit-drawer.tsx"]) {
      const text = readFileSync(join(root, "components", name), "utf8");
      expect(text).toMatch(/<DrawerFooter[\s\S]*?message=\{message\}/);
      expect(text, `${name} must show a message set while nothing is edited`).toMatch(/note=\{state === "clean" \? message : null\}/);
    }
  });
});

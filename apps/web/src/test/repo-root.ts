import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

/**
 * The repository root, found by walking up from `from` to the folder that holds Deluno.slnx. Tests that read backend
 * files (the shipped guide package, the contracts) use it instead of counting "../" levels, because Stryker copies
 * the web app into apps/web/.stryker-tmp/sandbox-*, which is deeper than the real folder.
 */
export function repoRoot(from: string = process.cwd()): string {
  let folder = resolve(from);
  for (;;) {
    if (existsSync(join(folder, "Deluno.slnx"))) return folder;
    const parent = dirname(folder);
    if (parent === folder) throw new Error(`No Deluno.slnx above ${from}`);
    folder = parent;
  }
}

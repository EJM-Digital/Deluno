// The image name a Deluno release was published under. ghcr does not forward a moved name, so releases up to
// lastFormerDelunoImageVersion stay at formerDelunoImage forever and every later one is at the current repository's
// lower-cased name. Everything that pulls a previous release's image asks here (the same rule as
// ProjectRepositories.DelunoImageForVersion in C#); both read their values from repository.json / that class.
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

export function readRepositories(file = path.join(root, "repository.json")) {
  return JSON.parse(readFileSync(file, "utf8"));
}

/** Orders versions like 1.0.0-rc.30.34 numerically; a release without a suffix is newer than its candidates. */
export function compareVersions(left, right) {
  const parse = (value) => {
    const text = value.trim().replace(/^v/i, "").split("+")[0];
    const dash = text.indexOf("-");
    const nums = (part) => part.split(/[.-]/).map((piece) => (/^\d+$/.test(piece) ? Number(piece) : -1));
    return { core: nums(dash < 0 ? text : text.slice(0, dash)), pre: dash < 0 ? null : nums(text.slice(dash + 1)) };
  };
  const parts = (a, b) => {
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      const x = a[i] ?? 0;
      const y = b[i] ?? 0;
      if (x !== y) return x < y ? -1 : 1;
    }
    return 0;
  };
  const l = parse(left);
  const r = parse(right);
  const core = parts(l.core, r.core);
  if (core !== 0) return core;
  if (l.pre === null && r.pre === null) return 0;
  if (l.pre === null) return 1;
  if (r.pre === null) return -1;
  return parts(l.pre, r.pre);
}

export function delunoImageForVersion(version, repositories = readRepositories()) {
  return compareVersions(version, repositories.lastFormerDelunoImageVersion) <= 0
    ? repositories.formerDelunoImage
    : `ghcr.io/${repositories.deluno.toLowerCase()}`;
}

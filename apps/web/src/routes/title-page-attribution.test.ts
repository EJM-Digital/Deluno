import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// The detail pages are too heavy to mount in a unit test (the rest of their tests read exports), so
// this checks the source places the credits where #1108 says.
const read = (name: string) => readFileSync(resolve(process.cwd(), "src/routes", name), "utf8");

describe("title pages credit TMDb (#1108)", () => {
  it("a film's page shows the short TMDb credit", () => {
    const source = read("movie-detail-page.tsx");
    expect(source).toContain('import { TmdbAttribution } from "../components/app/tmdb-attribution"');
    expect(source).toMatch(/<TmdbAttribution className=/);
  });

  it("a show's page credits both sources in one short line, or TMDb alone when the show has no TheTVDB id", () => {
    const source = read("show-detail-page.tsx");
    expect(source).toContain('import { TmdbAndTvdbAttribution, TmdbAttribution } from "../components/app/tmdb-attribution"');
    expect(source).toMatch(/metaText\("TvDbId"\)\s*\?\s*<TmdbAndTvdbAttribution[^>]*\/>\s*:\s*<TmdbAttribution/);
  });
});

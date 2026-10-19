import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// The guide package Deluno ships, read from the file the build embeds, and the page's own source: nothing is stubbed.
// vitest runs from apps/web.
const repo = resolve(process.cwd(), "../..");
const shipped = JSON.parse(readFileSync(resolve(repo, "src/Deluno.Quality/Guides/trash-guide-package.json"), "utf8")) as {
  bundles: { id: string; mediaType: string; includes: { trashId: string }[] }[];
  customFormats: { trashId: string }[];
};
const pageSource = readFileSync(resolve(repo, "apps/web/src/routes/setup-guide-page.tsx"), "utf8");
const goalBlock = pageSource.slice(pageSource.indexOf("const FORMAT_GOALS = {"), pageSource.indexOf("/** The only format goals a saved draft can carry"));
const goalBundleIds = [...goalBlock.matchAll(/bundleId: "([^"]+)"/g)].map((match) => match[1]);

describe("the release preferences the guide offers are all in the guide package Deluno ships", () => {
  it("offers six release preferences", () => {
    expect(goalBundleIds).toEqual(["starter-streaming-1080p", "balanced-1080p", "premium-4k-streaming", "storage-saver", "anime-balanced", "archive-4k-remux"]);
  });

  it.each(goalBundleIds)("%s is a bundle in the shipped package, and every rule it names is in the package", (bundleId) => {
    const bundle = shipped.bundles.find((candidate) => candidate.id === bundleId);
    expect(bundle, `the shipped guide package has no bundle ${bundleId}`).toBeTruthy();
    expect(bundle!.includes.length).toBeGreaterThan(0);
    const known = new Set(shipped.customFormats.map((format) => format.trashId));
    const missing = bundle!.includes.filter((entry) => !known.has(entry.trashId)).map((entry) => entry.trashId);
    expect(missing).toEqual([]);
  });

  it("anime is a TV-only bundle and archive a movies-only one, which is why the other media type gets Balanced", () => {
    expect(shipped.bundles.find((bundle) => bundle.id === "anime-balanced")?.mediaType).toBe("tv");
    expect(shipped.bundles.find((bundle) => bundle.id === "archive-4k-remux")?.mediaType).toBe("movies");
  });
});

import { expect, test } from "@playwright/test";
import { clientFinished, filesUnder, makeTvPack, setUpStack, sha256Of, stage, until, type Stack } from "./stack.ts";
import { containerLog } from "./resend.ts";

/**
 * Group `seasonpack` (scripts/golden-path/suite/vm/scenarios/G11-SeasonPack.ps1, #1188 follow-up): a season pack that replaces episodes whose
 * old files sit outside every library. When a season-pack replacement finds that the file it replaced matches no library, Deluno leaves
 * that old file where it is (the pack path moves it aside while it places the new files, and leaves it at the aside name) instead of
 * deleting it for good, and says why. A person's media only ever leaves through the recycle bin, and the bin needs a library to put the
 * file under (ImportPipelineService.ExecuteTvPackAsync).
 *
 * The fixture is built the way a person could end up there, with Deluno's own screens' calls:
 *   1. a TV library of its own and a show in it, whose five episodes come from a metadata broker (the fixture, docs/metadata-broker-contract.md;
 *      Deluno refuses to invent an episode a catalogue does not list);
 *   2. season 1, episodes 1 and 2, imported from single files, so the show owns two files in that library;
 *   3. the library's folder is changed to another folder (Settings > Libraries), so the two files now sit outside every TV library;
 *   4. Deluno's own season search ("Search season", POST /api/series/{id}/seasons/1/search) finds a season pack of the five episodes (1080p, the
 *      two it has are 720p), grabs it, qBittorrent downloads it from the fixture, and Deluno imports it as a replacement of the two episodes it
 *      owns with the replacement targets the dispatch recorded.
 * The old files are hashed before step 4. Afterwards they must still be on disk, byte for byte, and Deluno's log must say they were left
 * because no library matched them and that nothing was deleted. The new files are placed under the library's new folder.
 */
test.describe.configure({ mode: "serial" });

let stack: Stack;

test.beforeAll(async () => {
  test.setTimeout(10 * 60_000);
  stack = await setUpStack();
});

interface Show { title: string; year: number; seriesId: string; libraryId: string }

/** Queues an import of a single file for a show, as the import screen does. */
async function queueTvImport(sourcePath: string, show: Show) {
  const body = {
    preview: { sourcePath, mediaType: "tv", title: show.title, year: show.year, seriesId: show.seriesId, libraryId: show.libraryId },
    transferMode: "copy", overwrite: false, allowCopyFallback: true
  };
  // 425 is "still being written": Deluno waits for a file to sit still before it queues the import, as a person pressing Import again would.
  let reply = await stack.deluno.call("POST", "/api/filesystem/import/jobs", body);
  for (let attempt = 0; attempt < 24 && reply.status === 425; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 5000));
    reply = await stack.deluno.call("POST", "/api/filesystem/import/jobs", body);
  }
  return reply;
}

test("seasonpack.no-library: A season-pack replacement whose old files match no library leaves them in place, byte for byte, and says why", async () => {
  test.setTimeout(25 * 60_000);
  const token = Date.now().toString(36);
  const title = `Gppack${token}`;
  const year = 2020;
  const root = `/pack/${title}`;
  const libRoot = `${root}/library-before`;
  const movedRoot = `${root}/library-after`;
  const oldSrc = `${root}/old`;
  const tvDownloads = "/downloads/completed/TV";
  let libId: string | undefined;
  let seriesId: string | undefined;
  try {
    // ---------------------------------------------------------- 1. a TV library and a show in it
    const episodeFile = (ep: number) => `${title}.S01E${String(ep).padStart(2, "0")}.720p.WEB-DL.x264-E2E.mkv`;
    await stage({ dirs: [libRoot, movedRoot, oldSrc], copies: [1, 2].map((ep) => ({ to: `${oldSrc}/${episodeFile(ep)}`, from: "small" as const })) });
    // qBittorrent files a TV download under the library's downloads folder, as the movies category does for movies.
    const categories = await stack.qbittorrent.get<Record<string, unknown>>("/api/v2/torrents/categories");
    if (!categories["deluno-tv"]) await stack.qbittorrent.post("/api/v2/torrents/createCategory", { category: "deluno-tv", savePath: tvDownloads });
    // The fixture's episodes are two minutes long, far under any real episode's size, so the profile's size rules are set to no limit (0 and 0),
    // as a person sets them in Settings > Profiles for a library of short clips.
    const qualities = ["WEB 720p", "Bluray 720p", "WEB 1080p", "Bluray 1080p"];
    const tvProfile = await stack.deluno.must<any>("POST", "/api/quality-profiles", {
      name: `Short clips - E2E TV ${token}`, mediaType: "tv", cutoffQuality: "WEB 1080p", allowedQualities: qualities.join(","),
      customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false,
      sizeRules: qualities.map((quality) => ({ quality, minGb: 0, maxGb: 0, minMb: 0, maxMb: 0 }))
    });
    const lib = await stack.deluno.must<any>("POST", "/api/libraries", {
      name: `Pack walk ${token}`, mediaType: "tv", purpose: "Main library", rootPath: libRoot, downloadsPath: tvDownloads,
      qualityProfileId: tvProfile.id, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false,
      removeEmptySourceFolders: false, searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10
    });
    libId = lib.id;
    await stack.deluno.must("PUT", `/api/libraries/${libId}/routing`, {
      sources: [{ indexerId: stack.indexerId, priority: 1, requiredTags: "", excludedTags: "" }],
      downloadClients: [{ downloadClientId: stack.clientIds.qbittorrent, priority: 1 }]
    });
    const series = await stack.deluno.must<any>("POST", "/api/series/", { title, startYear: year, monitored: true, libraryId: libId });
    seriesId = series.id;
    await stack.deluno.must("PATCH", "/api/settings", { metadataProviderMode: "broker", metadataBrokerUrl: "http://fixture:8765" });
    await stack.deluno.must("POST", `/api/series/${seriesId}/metadata/refresh`);
    const catalogued = await stack.deluno.must<any>("GET", `/api/series/${seriesId}/inventory`);
    expect(((catalogued.episodes ?? []) as any[]).length, "The metadata broker gave the show its five episodes").toBe(5);
    const show: Show = { title, year, seriesId: seriesId!, libraryId: libId! };

    // ---------------------------------------------------------- 2. two episodes imported, each from a single file
    for (const ep of [1, 2]) {
      const queued = await queueTvImport(`${oldSrc}/${episodeFile(ep)}`, show);
      if (queued.status < 200 || queued.status >= 300) throw new Error(`Queuing the import of S01E${ep} answered ${queued.status}. ${queued.text.slice(0, 400)}`);
    }
    const owned = await until("the show to own S01E01 and S01E02 in its library", 360, async () => {
      const inventory = await stack.deluno.call("GET", `/api/series/${seriesId}/inventory`);
      const withFile = ((inventory.body?.episodes ?? []) as any[]).filter((e) => e.hasFile && e.seasonNumber === 1 && [1, 2].includes(e.episodeNumber));
      return withFile.length === 2 ? withFile : undefined;
    }, 3000).catch(() => undefined);
    expect(owned !== undefined, "The show owns S01E01 and S01E02 in its library before the pack (the fixture)").toBe(true);
    const oldFiles = (await filesUnder("pack", libRoot)).filter((f) => f.path.endsWith(".mkv")).sort((a, b) => a.path.localeCompare(b.path));
    expect(oldFiles.length, `Two episode files are on disk under the library's folder: ${oldFiles.map((f) => f.path).join("; ")}`).toBe(2);
    const before = new Map<string, string>();
    for (const f of oldFiles) before.set(f.path, (await sha256Of(f.path))!.sha256);

    // ---------------------------------------------------------- 3. the library's folder changes: the old files match no library
    const put = await stack.deluno.call("PUT", `/api/libraries/${libId}`, { name: `Pack walk ${token}`, rootPath: movedRoot, downloadsPath: tvDownloads });
    expect(put.status >= 200 && put.status < 300, `The library's folder is changed to another folder, so the two old files sit outside every TV library (HTTP ${put.status})`).toBe(true);
    const others = (await stack.deluno.must<any[]>("GET", "/api/libraries")).filter((l) => l.mediaType === "tv" && l.rootPath && oldFiles[0].path.startsWith(String(l.rootPath)));
    expect(others.map((l) => l.name), "No TV library's folder contains the old files now").toEqual([]);

    // ---------------------------------------------------------- 4. Deluno's season search grabs the pack, and the pack replaces them
    const pack = await makeTvPack(title, 5);
    const logStart = Date.now() - 2000;
    const search = await stack.deluno.call("POST", `/api/series/${seriesId}/seasons/1/search`);
    console.log(`season search: ${search.status} ${search.text.slice(0, 700)}`);
    expect(search.status === 200 && search.body?.queuedCount > 0 && String(search.body?.releaseName ?? "").includes(pack.release),
      `"Search season" grabbed the pack (HTTP ${search.status}; outcome ${search.body?.outcome}: ${search.body?.reason ?? ""} ${search.body?.dispatchMessage ?? ""}; release ${search.body?.releaseName})`).toBe(true);
    await until("qBittorrent to finish the pack", 300, () => clientFinished(stack, "qbittorrent", pack), 2000);
    const imported = await until("the pack to be placed under the library's new folder", 360, async () => {
      const placed = (await filesUnder("pack", movedRoot)).filter((f) => f.path.endsWith(".mkv") && !f.path.includes(".deluno-pack-backup-"));
      return placed.length >= 5 ? placed : undefined;
    }, 3000).catch(() => undefined);
    expect.soft(imported !== undefined, `The pack's five episodes were placed under the library's new folder (${(await filesUnder("pack", movedRoot)).length} file(s) there)`).toBe(true);

    // ---------------------------------------------------------- what is left
    const gone: string[] = [];
    const differ: string[] = [];
    const asides: string[] = [];
    const all = await filesUnder("pack", root);
    for (const [file, hash] of before) {
      const dir = file.slice(0, file.lastIndexOf("/"));
      const name = file.slice(file.lastIndexOf("/") + 1);
      const aside = all.find((f) => f.path.startsWith(`${dir}/${name}.deluno-pack-backup-`));
      const kept = all.some((f) => f.path === file) ? file : aside?.path;
      if (!kept) { gone.push(name); continue; }
      asides.push(kept.slice(kept.lastIndexOf("/") + 1));
      if ((await sha256Of(kept))?.sha256 !== hash) differ.push(kept.slice(kept.lastIndexOf("/") + 1));
    }
    expect.soft(gone, "Both old files are still on disk (left where they were, or at the aside name the pack path gave them)").toEqual([]);
    expect.soft(differ, "The old files are byte-identical to what they were before the pack (SHA-256 of both unchanged)").toEqual([]);
    console.log(`kept as: ${asides.join(", ")}; files under the new folder: ${all.filter((f) => f.path.startsWith(movedRoot)).length}`);

    // Deluno says why. It writes it to its log as a warning, once for each replaced file.
    const said = await until("Deluno's log to say why", 60, async () => {
      const hits = containerLog("deluno", logStart).split("\n").filter((l) => /because no library matched it; nothing was deleted/.test(l));
      return hits.length ? hits : undefined;
    }, 3000).catch(() => undefined);
    const lines = said?.value ?? [];
    const mine = lines.filter((l) => l.includes(title));
    expect.soft(mine.length >= 1, `Deluno says why: its log says the replaced file was left because no library matched it, and that nothing was deleted (${mine[0] ? mine[0].replace(/\s+/g, " ").slice(0, 300) : `no such line names ${title} among ${lines.length} line(s) of that wording`})`).toBe(true);
    expect.soft(mine.length, "It says so once for each replaced file").toBeGreaterThanOrEqual(2);
    const bad = containerLog("deluno", logStart).split("\n").filter((l) => l.includes(title) && /moved to the recycle bin|removed the previous/i.test(l));
    expect.soft(bad, "Nothing says the old file was recycled or removed").toEqual([]);
  } finally {
    // Best effort: the show and the library go; the files stay on the volume as evidence.
    if (seriesId) await stack.deluno.call("POST", "/api/series/bulk", { seriesIds: [seriesId], operation: "remove" });
    if (libId) await stack.deluno.call("DELETE", `/api/libraries/${libId}`);
  }
});

import { expect } from "@playwright/test";
import { isUsenet, type ClientKind } from "./clients.ts";
import type { FilmOptions } from "./stack.ts";
import { linkWeirLibrary, makeFilm, routeLibraryTo, setUpStack, until, type Stack } from "./stack.ts";

/**
 * TV episodes through the same stack as the films (docs/exec-plans/active/PLAN-E2E-IN-CI.md, step 4): a real show whose one episode is
 * searched and grabbed through Deluno, sent to a client, handed to Weir (the TV workflow) and imported into a TV library.
 *
 * The show is made up (a title that begins "Gppack"), and the fixture is its metadata broker (docs/metadata-broker-contract.md): it
 * lists the show's five episodes of season 1, all aired in 2020, so Deluno has an episode to look for. The episode's release is a real
 * video named like a scene release (Title.S01E01.1080p.WEB-DL.x264-E2E) that the fixture offers on its Torznab and Newznab feeds under
 * the TV categories. Everything after the search is the films' own path: the clients, Weir and the shared folders are read through
 * their own APIs.
 */

export const TV_FOLDERS = {
  library: "/media/TV",
  downloads: "/downloads/completed/TV",
  weirOutput: "/weir/Ready/TV"
};

export const TV_CATEGORY = "deluno-tv";

/** The show's title and release for a send: letters and digits only, and unique to the send. */
export function tvTitle(label: string, kind: ClientKind, layout: "folder" | "file") {
  const word = (text: string) => text[0].toUpperCase() + text.slice(1);
  return `Gppack${word(label)}${Date.now().toString(36)}${word(kind)}${layout === "file" ? "File" : ""}`;
}

/**
 * The movies stack (setUpStack) plus what an episode needs: Weir's TV library (its watched and output folders, linked to Deluno), the
 * qBittorrent TV category, the metadata broker, a profile with no size limits (the fixture's episodes are two minutes long), and a TV
 * library that refines before import through Weir. `libraryId` is then the TV library, so routeLibraryTo and the sends use it.
 */
export async function setUpTvStack(): Promise<Stack> {
  const stack = await setUpStack();
  const { deluno, weir, qbittorrent } = stack;

  await linkWeirLibrary(weir, "tv", TV_FOLDERS.downloads, TV_FOLDERS.weirOutput);
  await until("Weir to watch the TV library's folder", 120, async () => {
    const libs = await weir.get<any[]>("/api/v1/processing/libraries");
    return libs.body.some((l) => l.enabled && l.watched_folder === TV_FOLDERS.downloads);
  }, 2000);

  const categories = await qbittorrent.get<Record<string, unknown>>("/api/v2/torrents/categories");
  if (!categories[TV_CATEGORY]) await qbittorrent.post("/api/v2/torrents/createCategory", { category: TV_CATEGORY, savePath: TV_FOLDERS.downloads });

  await deluno.must("PATCH", "/api/settings", { metadataProviderMode: "broker", metadataBrokerUrl: "http://fixture:8765" });

  const profiles = await deluno.must<any[]>("GET", "/api/quality-profiles");
  let profile = profiles.find((p) => p.mediaType === "tv" && /E2E TV/.test(p.name));
  if (!profile) {
    const qualities = ["WEB 720p", "Bluray 720p", "WEB 1080p", "Bluray 1080p"];
    profile = await deluno.must("POST", "/api/quality-profiles", {
      name: "Short clips - E2E TV", mediaType: "tv", cutoffQuality: "WEB 1080p", allowedQualities: qualities.join(","),
      customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false,
      sizeRules: qualities.map((quality) => ({ quality, minGb: 0, maxGb: 0, minMb: 0, maxMb: 0 }))
    });
  }
  const libraries = await deluno.must<any[]>("GET", "/api/libraries");
  let library = libraries.find((l) => l.mediaType === "tv" && l.rootPath === TV_FOLDERS.library);
  if (!library) {
    library = await deluno.must("POST", "/api/libraries", {
      name: "TV", mediaType: "tv", purpose: "Main library", rootPath: TV_FOLDERS.library, downloadsPath: TV_FOLDERS.downloads,
      qualityProfileId: profile.id, autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false,
      removeEmptySourceFolders: false, searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10
    });
  }
  const processors = await deluno.must<any[]>("GET", "/api/integrations/processors/connections");
  const processor = processors.find((p) => p.id === stack.processorId)!;
  await deluno.must("PUT", `/api/libraries/${library.id}/workflow`, {
    importWorkflow: "refine-before-import", processorConnectionId: processor.id, processorName: processor.name,
    processorOutputPath: TV_FOLDERS.weirOutput, processorTimeoutMinutes: 2880, processorFailureMode: "block",
    cleanupMode: "keep-source", removeEmptySourceFolders: false
  });
  const tv: Stack = { ...stack, libraryId: library.id, media: "tv" };
  await routeLibraryTo(tv, "qbittorrent");
  return tv;
}

export interface SentEpisode { seriesId: string; episodeId: string; film: Awaited<ReturnType<typeof makeFilm>>; sentAt: number; search: any }

/**
 * Adds the show to the TV library, asks the metadata broker for its episodes, routes the library to the one client, and presses
 * "Search" for the one episode, as the episode's row on the show's page does. The search finds the release on the feed and grabs it.
 */
export async function searchEpisode(
  stack: Stack, kind: ClientKind, label: string, layout: "folder" | "file", options: FilmOptions = {}
): Promise<SentEpisode> {
  const title = tvTitle(label, kind, layout);
  const usenet = isUsenet(kind);
  const film = await makeFilm(title, false, usenet, usenet ? "folder" : layout, { ...options, tvEpisode: 1 });
  const series = await stack.deluno.must<any>("POST", "/api/series/", { title, startYear: film.year, monitored: true, libraryId: stack.libraryId });
  await stack.deluno.must("POST", `/api/series/${series.id}/metadata/refresh`);
  const inventory = await stack.deluno.must<any>("GET", `/api/series/${series.id}/inventory`);
  const episode = ((inventory.episodes ?? []) as any[]).find((e) => e.seasonNumber === 1 && e.episodeNumber === 1);
  expect(episode?.episodeId, `The metadata broker gave ${title} its episode S01E01`).toBeTruthy();
  await routeLibraryTo(stack, kind);
  const sentAt = Date.now();
  const search = await stack.deluno.call<any>("POST", `/api/series/${series.id}/episodes/search`, { episodeIds: [episode.episodeId] });
  if (search.status < 200 || search.status >= 300 || !(search.body?.queuedCount > 0)) {
    throw new Error(`Searching ${title} S01E01 did not send it: HTTP ${search.status} ${search.text.slice(0, 700)}`);
  }
  return { seriesId: series.id, episodeId: episode.episodeId, film, sentAt, search: search.body };
}

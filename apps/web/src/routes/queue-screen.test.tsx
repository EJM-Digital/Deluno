import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { DownloadDispatchItem, DownloadQueueItem, ImportPreviewResponse, LibraryItem } from "../lib/api";
import { buildImportRequest, findDispatchForQueueItem, ImportPreviewFacts } from "./queue-screen";

function packPreview(canExecute: boolean): ImportPreviewResponse {
  return {
    sourcePath: "C:\\Downloads\\Example.Show.S01",
    destinationFolder: "C:\\TV\\Example Show (2026)",
    destinationPath: "C:\\TV\\Example Show (2026)",
    preferredTransferMode: "hardlink",
    hardlinkAvailable: true,
    matchedRuleId: "tv-main",
    matchedRuleName: "TV",
    sourceExists: true,
    destinationExists: false,
    sourceSizeBytes: 8192,
    destinationSizeBytes: 0,
    isSupportedMediaFile: true,
    mediaProbe: null,
    transferExplanation: "Every file is staged before the catalogue transaction.",
    warnings: [],
    explanation: "Every file has a unique episode mapping.",
    decisionSteps: [],
    pack: {
      canExecute,
      alreadyCommitted: false,
      sourceFileCount: 2,
      episodeCount: 2,
      files: [
        {
          sourcePath: "C:\\Downloads\\Example.Show.S01\\Example.Show.S01E01.mkv",
          destinationPath: "C:\\TV\\Example Show (2026)\\Example Show - S01E01 - One.mkv",
          sourceSizeBytes: 4096,
          episodeKeys: ["S01E01"],
          warnings: []
        },
        {
          sourcePath: "C:\\Downloads\\Example.Show.S01\\Example.Show.S01E02.mkv",
          destinationPath: "C:\\TV\\Example Show (2026)\\Example Show - S01E02 - Two.mkv",
          sourceSizeBytes: 4096,
          episodeKeys: ["S01E02"],
          warnings: []
        }
      ],
      blockReasons: canExecute ? [] : ["Episode S01E01 is claimed by more than one file in the pack."]
    }
  };
}

describe("season-pack import preview", () => {
  it("shows every reviewed episode destination before execution", () => {
    render(<ImportPreviewFacts preview={packPreview(true)} />);

    const region = screen.getByRole("region", { name: "Season pack import preview" });
    expect(within(region).getByText("2 files · 2 episodes")).toBeVisible();
    expect(within(region).getByText("Every file and episode is resolved")).toBeVisible();
    expect(within(region).getByText("S01E01")).toBeVisible();
    expect(within(region).getByText(/Example Show - S01E02 - Two\.mkv/)).toBeVisible();
  });

  it("makes a whole-pack block reason visible instead of offering a partial plan", () => {
    render(<ImportPreviewFacts preview={packPreview(false)} />);

    const region = screen.getByRole("region", { name: "Season pack import preview" });
    expect(within(region).getByText("Needs recovery review")).toBeVisible();
    expect(within(region).getByText(/S01E01 is claimed by more than one file/)).toBeVisible();
  });

  it("carries the dispatch's series identity into the Transfers preview and job", () => {
    const queueItem = {
      id: "torrent-hash",
      clientId: "client-1",
      mediaType: "tv",
      title: "Example Show",
      releaseName: "Example.Show.S01.1080p",
      sourcePath: "C:\\Downloads\\Example.Show.S01"
    } as DownloadQueueItem;
    const dispatch = {
      id: "dispatch-1",
      downloadClientId: "client-1",
      torrentHashOrItemId: "TORRENT-HASH",
      entityType: "series",
      entityId: "series-42",
      releaseName: queueItem.releaseName
    } as DownloadDispatchItem;

    const matched = findDispatchForQueueItem(queueItem, [dispatch]);
    const request = buildImportRequest(queueItem, [] as LibraryItem[], matched);

    expect(matched?.id).toBe("dispatch-1");
    expect(request.seriesId).toBe("series-42");
  });
});

import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { DownloadDispatchItem, DownloadHealthFinding, DownloadQueueItem, ImportPreviewResponse, LibraryItem } from "../lib/api";
import { RemovalReasonDialog, type RemovalReason } from "../components/ui/removal-reason-dialog";
import { buildImportRequest, findDispatchForQueueItem, ImportPreviewFacts, unmonitorRequestFor, visibleFindings } from "./queue-screen";

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

  // #617: Deluge's name for the torrent, and the indexer's for the release, as seen on the rig.
  it("finds the grab behind a download whose name differs only in case and punctuation", () => {
    const queueItem = { id: "deluge-hash", clientId: "deluge", releaseName: "house.on.-haunted.-hill.-1959.1080p.-blu-ray.-x-264-amiable" } as DownloadQueueItem;
    const grab = { id: "dispatch-house", downloadClientId: "deluge", torrentHashOrItemId: null, releaseName: "House On Haunted Hill 1959 1080p Blu-Ray x264-AMIABLE" } as DownloadDispatchItem;
    const other = { id: "dispatch-other", downloadClientId: "deluge", torrentHashOrItemId: null, releaseName: "House On Haunted Hill 1999 1080p BluRay x264" } as DownloadDispatchItem;

    expect(findDispatchForQueueItem(queueItem, [other, grab])?.id).toBe("dispatch-house");
  });
});

// #880/#881: a setup-problem finding (a full disk, permissions, a missing
// file) stays visible until it clears on its own, but a person can dismiss
// it. Dismissing calls the server's existing health/{kind}/ignore endpoint,
// which sets ignoredUntilUtc — this is what makes that dismissal actually
// disappear from the drawer instead of the finding just sitting there.
// Cleanup sheet J1/J2 (#924): removing a download from the Queue asks why,
// and each of the three plain-word choices maps to a distinct API call.
describe("the Queue's removal-reason dialog", () => {
  it("gives every choice its own accessible name, so the smoke test never sees two buttons called the same thing", () => {
    render(
      <RemovalReasonDialog
        open
        onOpenChange={() => {}}
        title="Example.Movie.2026.1080p"
        busyReason={null}
        onChoose={() => {}}
      />
    );

    expect(screen.getByRole("button", { name: /It's bad/ })).toBeVisible();
    expect(screen.getByRole("button", { name: /I don't want it/ })).toBeVisible();
    expect(screen.getByRole("button", { name: /Just stop tracking it/ })).toBeVisible();
  });

  it.each([
    ["It's bad", "failed"],
    ["I don't want it", "unwanted"],
    ["Just stop tracking it", "forget"]
  ] as const)("choosing \"%s\" calls back with reason \"%s\"", (label, reason) => {
    const onChoose = vi.fn();
    render(
      <RemovalReasonDialog
        open
        onOpenChange={() => {}}
        title="Example.Movie.2026.1080p"
        busyReason={null}
        onChoose={onChoose}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: new RegExp(label.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")) }));

    expect(onChoose).toHaveBeenCalledWith(reason satisfies RemovalReason);
  });

  it("disables every choice while one of them is in flight", () => {
    render(
      <RemovalReasonDialog
        open
        onOpenChange={() => {}}
        title="Example.Movie.2026.1080p"
        busyReason="failed"
        onChoose={() => {}}
      />
    );

    expect(screen.getByRole("button", { name: /It's bad/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /I don't want it/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Just stop tracking it/ })).toBeDisabled();
  });
});

describe("unmonitoring a title after \"I don't want it\"", () => {
  it("sends a movie to the movies bulk endpoint", () => {
    expect(unmonitorRequestFor("movie-1", "movie")).toEqual({
      url: "/api/movies/bulk",
      body: { movieIds: ["movie-1"], operation: "monitoring", monitored: false }
    });
  });

  it("sends a series to the series bulk endpoint", () => {
    expect(unmonitorRequestFor("series-1", "tv")).toEqual({
      url: "/api/series/bulk",
      body: { seriesIds: ["series-1"], operation: "monitoring", monitored: false }
    });
  });

  it("treats a null media type as a movie rather than guessing wrong silently", () => {
    expect(unmonitorRequestFor("movie-2", null).url).toBe("/api/movies/bulk");
  });
});

describe("dismissing a health finding", () => {
  function findingAt(ignoredUntilUtc: string | null): DownloadHealthFinding {
    return {
      severity: "critical",
      kind: "client-setup-problem",
      summary: "This looks like a problem on your machine, not the release.",
      evidence: "Disk full.",
      recommendedAction: "Free up space, then Resume.",
      canSafelyRetry: true,
      canSafelyRemove: false,
      strikeCount: 0,
      candidateBlocked: false,
      ignoredUntilUtc
    };
  }

  it("keeps a finding with no ignoredUntilUtc visible", () => {
    const item = { healthFindings: [findingAt(null)] } as DownloadQueueItem;

    expect(visibleFindings(item)).toHaveLength(1);
  });

  it("hides a finding dismissed into the future", () => {
    const future = new Date(Date.now() + 60_000).toISOString();
    const item = { healthFindings: [findingAt(future)] } as DownloadQueueItem;

    expect(visibleFindings(item)).toHaveLength(0);
  });

  it("shows a finding again once its dismissal has elapsed", () => {
    const past = new Date(Date.now() - 60_000).toISOString();
    const item = { healthFindings: [findingAt(past)] } as DownloadQueueItem;

    expect(visibleFindings(item)).toHaveLength(1);
  });
});

import type { HeldReleaseRow } from "../lib/held-releases";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { MovieImportRecoverySummary, DownloadClientTelemetrySnapshot, DownloadDispatchItem, DownloadHealthFinding, DownloadQueueItem, ImportPreviewResponse, LibraryItem, MediaProbeInfo, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { RemovalReasonDialog, type RemovalReason } from "../components/ui/removal-reason-dialog";
import { buildImportRequest, dispatchStageLabel, findDispatchForQueueItem, healthFindingRuleSummary, ImportPreviewFacts, isResendingHandoff, QueuePage, queueRowExtras, reorderControlsFor, visibleFindings } from "./queue-screen";
import { DEFAULT_DISPLAY_PREFERENCES } from "../lib/display-preferences";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const revalidateMock = vi.fn();
const queueLoaderData = {
  telemetry: {
    summary: {
      activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0,
      processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0
    },
    clients: [] as DownloadClientTelemetrySnapshot[],
    capturedUtc: new Date().toISOString()
  },
  dispatches: [],
  movieRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] as MovieImportRecoverySummary["recentCases"] },
  seriesRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] },
  libraries: [],
  settings: emptyPlatformSettingsSnapshot,
  jobs: [],
  failedJobs: [],
  healthRecords: [],
  processorConnections: [],
  processorHandoffs: [],
  activityEvents: [],
  orphans: [] as OrphanedDownloadItem[],
  heldReleases: [] as HeldReleaseRow[]
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(queueLoaderData, revalidateMock)
  };
});

// #810: reorder/force-start controls are shown only when the client's own
// reported capabilities say so, never guessed at per protocol.
describe("reorderControlsFor (#810)", () => {
  const capabilities = (overrides: Partial<DownloadClientTelemetrySnapshot["capabilities"]>): Pick<DownloadClientTelemetrySnapshot, "capabilities"> => ({
    capabilities: {
      supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true,
      supportsRecheck: true, supportsImportPath: true, authMode: "form",
      ...overrides
    }
  });

  it("shows nothing for a missing client", () => {
    expect(reorderControlsFor(null)).toEqual({ canReorder: false, canForceStart: false });
  });

  it("hides reorder controls for a client that could not confirm its queue API (uTorrent)", () => {
    expect(reorderControlsFor(capabilities({}))).toEqual({ canReorder: false, canForceStart: false });
  });

  it("shows reorder controls without force-start for a torrent/usenet client other than qBittorrent", () => {
    expect(reorderControlsFor(capabilities({ supportsQueueReorder: true }))).toEqual({ canReorder: true, canForceStart: false });
  });

  it("shows both reorder and force-start for qBittorrent", () => {
    expect(reorderControlsFor(capabilities({ supportsQueueReorder: true, supportsForceStart: true }))).toEqual({ canReorder: true, canForceStart: true });
  });
});

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
});

describe("MediaInfo detail (#794)", () => {
  function probe(overrides: Partial<MediaProbeInfo> = {}): MediaProbeInfo {
    return {
      status: "succeeded",
      tool: "ffprobe",
      message: null,
      durationSeconds: 1420,
      container: "matroska",
      bitrate: 8_500_000,
      videoStreams: [
        { index: 0, codec: "hevc", profile: "Main 10", width: 3840, height: 2160, pixelFormat: "yuv420p10le", frameRate: 23.976, bitrate: 8_000_000, language: null }
      ],
      audioStreams: [
        { index: 1, codec: "eac3", profile: null, channels: 6, channelLayout: "5.1", sampleRate: 48000, bitrate: 448_000, language: "eng" }
      ],
      subtitleStreams: [],
      ...overrides
    };
  }

  function previewWithProbe(mediaProbe: MediaProbeInfo | null): ImportPreviewResponse {
    return {
      sourcePath: "C:\\Downloads\\Example.mkv",
      destinationFolder: "C:\\Movies\\Example (2026)",
      destinationPath: "C:\\Movies\\Example (2026)\\Example (2026).mkv",
      preferredTransferMode: "hardlink",
      hardlinkAvailable: true,
      matchedRuleId: "movies-main",
      matchedRuleName: "Movies",
      sourceExists: true,
      destinationExists: false,
      sourceSizeBytes: 8192,
      destinationSizeBytes: 0,
      isSupportedMediaFile: true,
      mediaProbe,
      transferExplanation: "Every file is staged before the catalogue transaction.",
      warnings: [],
      explanation: "Matched by extension.",
      decisionSteps: []
    };
  }

  it("shows pixel format, frame rate, sample rate and channel layout for a fully-probed file", () => {
    render(<ImportPreviewFacts preview={previewWithProbe(probe())} />);

    const region = screen.getByRole("region", { name: "MediaInfo" });
    expect(within(region).getByText("yuv420p10le")).toBeVisible();
    expect(within(region).getByText("23.98 fps")).toBeVisible();
    expect(within(region).getByText("5.1")).toBeVisible();
    expect(within(region).getByText("48,000 Hz")).toBeVisible();
  });

  it("shows a missing field as Unavailable rather than blank", () => {
    render(<ImportPreviewFacts preview={previewWithProbe(probe({
      videoStreams: [{ index: 0, codec: "h264", profile: null, width: 1920, height: 1080, pixelFormat: null, frameRate: null, bitrate: null, language: null }],
      audioStreams: [{ index: 1, codec: "aac", profile: null, channels: null, channelLayout: null, sampleRate: null, bitrate: null, language: null }]
    }))} />);

    const region = screen.getByRole("region", { name: "MediaInfo" });
    expect(within(region).getAllByText("Unavailable").length).toBeGreaterThanOrEqual(3);
  });

  it("shows the probe's own message when the file could not be probed at all", () => {
    render(<ImportPreviewFacts preview={previewWithProbe({
      status: "unavailable",
      tool: "ffprobe",
      message: "ffprobe is not installed.",
      durationSeconds: null,
      container: null,
      bitrate: null,
      videoStreams: [],
      audioStreams: [],
      subtitleStreams: []
    })} />);

    const region = screen.getByRole("region", { name: "MediaInfo" });
    expect(within(region).getByText("ffprobe is not installed.")).toBeVisible();
  });

  it("renders nothing extra when there is no probe at all", () => {
    render(<ImportPreviewFacts preview={previewWithProbe(null)} />);

    expect(screen.queryByRole("region", { name: "MediaInfo" })).not.toBeInTheDocument();
  });
});

describe("dispatch identity in the import preview", () => {
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

  // #1171: a TV row with no grab sent its release name as the "title" the person typed.
  it("sends no title hint for a TV download with no dispatch, so the server matches the library's show", () => {
    const queueItem = {
      id: "hash-1",
      clientId: "client-1",
      mediaType: "tv",
      title: "www.UIndex.org - The Twilight Zone S01E04 16 Millimeter Shrine 1080p AMZN WEB-DL DD 2 0 H 264-playWEB",
      releaseName: "www.UIndex.org - The Twilight Zone S01E04 16 Millimeter Shrine 1080p AMZN WEB-DL DD 2 0 H 264-playWEB",
      sourcePath: "C:\\Downloads\\www.UIndex.org - The Twilight Zone S01E04"
    } as DownloadQueueItem;

    const request = buildImportRequest(queueItem, [] as LibraryItem[], null);

    expect(request.title).toBeNull();
    expect(request.year).toBeNull();
    expect(request.dispatchId).toBeNull();
  });

  // #1171: a site-prefixed folder is not a file; the server picks the file.
  it("does not send a folder name as the file name", () => {
    const folder = { id: "h", clientId: "c", mediaType: "tv", title: "t", releaseName: "r", sourcePath: "C:\\Downloads\\www.UIndex.org - The Twilight Zone S01E04" } as DownloadQueueItem;
    expect(buildImportRequest(folder, [] as LibraryItem[], null).fileName).toBeNull();
    const file = { ...folder, sourcePath: "C:\\Downloads\\Show\\Show.S01E04.mkv" } as DownloadQueueItem;
    expect(buildImportRequest(file, [] as LibraryItem[], null).fileName).toBe("Show.S01E04.mkv");
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

/**
 * #783: manual import scans a folder and lets the user review every guessed
 * match before importing all reviewed rows in one action.
 */
describe("Manual import: scan a folder", () => {
  const authedFetch = authedFetchMock;

  function jsonResponse(body: unknown) {
    return Promise.resolve({
      ok: true,
      status: 200,
      json: () => Promise.resolve(body),
      text: () => Promise.resolve(JSON.stringify(body)),
      url: "test"
    } as Response);
  }

  function scanResponse() {
    return {
      folderPath: "D:/Downloads/Batch",
      items: [
        {
          sourcePath: "D:/Downloads/Batch/Arrival.2016.WEB.1080p-GROUP.mkv",
          fileName: "Arrival.2016.WEB.1080p-GROUP.mkv",
          guessedTitle: "Arrival",
          guessedYear: 2016,
          matched: true,
          preview: { ...packPreview(true), pack: null, sourcePath: "D:/Downloads/Batch/Arrival.2016.WEB.1080p-GROUP.mkv", destinationPath: "C:/Movies/Arrival (2016)/Arrival (2016).mkv" }
        },
        {
          sourcePath: "D:/Downloads/Batch/unknown_file.mkv",
          fileName: "unknown_file.mkv",
          guessedTitle: null,
          guessedYear: null,
          matched: false,
          preview: { ...packPreview(true), pack: null, sourcePath: "D:/Downloads/Batch/unknown_file.mkv", destinationPath: "" }
        }
      ],
      issues: []
    };
  }

  async function openManualImportInFolderMode() {
    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("button", { name: /Manual import/ }));
    fireEvent.click(screen.getByRole("radio", { name: "Scan a folder" }));
  }

  it("scans a folder and shows every file with its guessed match, flagging the one Deluno could not guess", async () => {
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/filesystem/import/scan-folder") return jsonResponse(scanResponse());
      return jsonResponse({});
    });

    await openManualImportInFolderMode();

    fireEvent.change(screen.getByPlaceholderText("D:/Downloads/Some.Release.2024"), { target: { value: "D:/Downloads/Batch" } });
    fireEvent.click(screen.getByRole("button", { name: "Scan folder" }));

    await waitFor(() => expect(screen.getByText("Arrival.2016.WEB.1080p-GROUP.mkv")).toBeInTheDocument());
    expect(screen.getByText("unknown_file.mkv")).toBeInTheDocument();
    expect(screen.getByText(/Deluno could not guess a title/)).toBeInTheDocument();
    expect(screen.getByDisplayValue("Arrival")).toBeInTheDocument();
  });

  it("imports every included row in one batch action", async () => {
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/filesystem/import/scan-folder") return jsonResponse(scanResponse());
      if (path === "/api/filesystem/import/execute-batch") {
        return jsonResponse({
          requestedCount: 1,
          importedCount: 1,
          results: [{ sourcePath: "D:/Downloads/Batch/Arrival.2016.WEB.1080p-GROUP.mkv", succeeded: true, message: "Imported.", response: null }]
        });
      }
      return jsonResponse({});
    });

    await openManualImportInFolderMode();

    fireEvent.change(screen.getByPlaceholderText("D:/Downloads/Some.Release.2024"), { target: { value: "D:/Downloads/Batch" } });
    fireEvent.click(screen.getByRole("button", { name: "Scan folder" }));
    await waitFor(() => expect(screen.getByText("Arrival.2016.WEB.1080p-GROUP.mkv")).toBeInTheDocument());

    // Only the matched row is included by default; the unmatched one needs a person's attention first.
    fireEvent.click(screen.getByRole("button", { name: /Import selected/ }));

    await waitFor(() => expect(screen.getByText(/Imported 1 of 1/)).toBeInTheDocument());
    const batchCall = authedFetch.mock.calls.find(([path]) => path === "/api/filesystem/import/execute-batch");
    expect(batchCall).toBeTruthy();
    const body = JSON.parse((batchCall![1] as RequestInit).body as string);
    expect(body.items).toHaveLength(1);
    expect(body.items[0].preview.sourcePath).toContain("Arrival");
  });

  // #961: a row Deluno could not guess gets a title/year typed in by hand, then
  // Re-check must send the correction and pick up the new match.
  it("re-checks a single row after its title and year are corrected by hand", async () => {
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/filesystem/import/scan-folder") return jsonResponse(scanResponse());
      if (path === "/api/filesystem/import/preview") {
        return jsonResponse({
          ...packPreview(true),
          pack: null,
          sourcePath: "D:/Downloads/Batch/unknown_file.mkv",
          destinationPath: "C:/Movies/Arrival (2016)/Arrival (2016).mkv"
        });
      }
      return jsonResponse({});
    });

    await openManualImportInFolderMode();

    fireEvent.change(screen.getByPlaceholderText("D:/Downloads/Some.Release.2024"), { target: { value: "D:/Downloads/Batch" } });
    fireEvent.click(screen.getByRole("button", { name: "Scan folder" }));
    await waitFor(() => expect(screen.getByText("unknown_file.mkv")).toBeInTheDocument());
    expect(screen.getByText(/Deluno could not guess a title/)).toBeInTheDocument();

    const titleInputs = screen.getAllByLabelText("Title");
    const yearInputs = screen.getAllByLabelText(/^Year/);
    // The unmatched row (unknown_file.mkv) is the second one scanned.
    fireEvent.change(titleInputs[1], { target: { value: "Arrival" } });
    fireEvent.change(yearInputs[1], { target: { value: "2016" } });

    const recheckButtons = screen.getAllByRole("button", { name: /Re-check/ });
    fireEvent.click(recheckButtons[1]);

    await waitFor(() => expect(screen.getByText("C:/Movies/Arrival (2016)/Arrival (2016).mkv")).toBeInTheDocument());
    // The now-matched row no longer shows the "could not guess" warning.
    expect(screen.queryByText(/Deluno could not guess a title/)).not.toBeInTheDocument();

    const previewCall = authedFetch.mock.calls.find(([path]) => path === "/api/filesystem/import/preview");
    expect(previewCall).toBeTruthy();
    const body = JSON.parse((previewCall![1] as RequestInit).body as string);
    expect(body.sourcePath).toBe("D:/Downloads/Batch/unknown_file.mkv");
    expect(body.title).toBe("Arrival");
    expect(body.year).toBe(2016);
  });
});

/**
 * #932/#958: the leftovers list's own batch action. Only what Deluno can
 * prove it downloaded is ever selectable or removable -- a leftover Deluno
 * did not grab has no checkbox at all.
 */
describe("leftover downloads: select all", () => {
  afterEach(() => {
    queueLoaderData.orphans = [];
  });

  function orphan(overrides: Partial<OrphanedDownloadItem>): OrphanedDownloadItem {
    return {
      path: "C:\\Downloads\\Complete\\Example.S01E01",
      name: "Example.S01E01",
      libraryId: "library-1",
      libraryName: "TV",
      sizeBytes: 1_073_741_824,
      lastWrittenUtc: new Date().toISOString(),
      ...overrides
    };
  }

  it("shows the select-all button and selects every leftover", () => {
    queueLoaderData.orphans = [
      orphan({ path: "C:\\Downloads\\Complete\\First.S01E01", name: "First.S01E01" }),
      orphan({ path: "C:\\Downloads\\Complete\\Second.S01E02", name: "Second.S01E02" })
    ];

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    const selectAll = screen.getByRole("button", { name: "Select all leftovers" });
    expect(selectAll).toBeVisible();
    fireEvent.click(selectAll);

    expect(screen.getByRole("switch", { name: "Select First.S01E01" })).toBeChecked();
    expect(screen.getByRole("switch", { name: "Select Second.S01E02" })).toBeChecked();
    expect(screen.getByRole("button", { name: "Remove 2 selected" })).toBeVisible();
  });

  it("says in one plain line that Transfers only shows downloads Deluno sent (#1062)", () => {
    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    expect(screen.getByText("Deluno only shows downloads it sent.")).toBeVisible();
  });

  it("offers no select-all button when there are no leftovers", () => {
    queueLoaderData.orphans = [];

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    expect(screen.queryByRole("button", { name: "Select all leftovers" })).not.toBeInTheDocument();
  });
});

/** #1167: a pile of things that need the user clears in one go, and the count is the rows. */
describe("Needs attention: select all, then Dismiss", () => {
  afterEach(() => {
    queueLoaderData.movieRecovery = { ...queueLoaderData.movieRecovery, recentCases: [] };
    authedFetchMock.mockReset();
  });

  it("dismisses every selected row with the same request the row's own Dismiss sends", async () => {
    queueLoaderData.movieRecovery = {
      ...queueLoaderData.movieRecovery,
      recentCases: ["a", "b", "c"].map((id) => ({
        id: `case-${id}`,
        title: `Film ${id}`,
        failureKind: "import-timeout",
        summary: "Import was detected but never completed after 24 hours.",
        recommendedAction: "Retry the import.",
        detailsJson: null,
        detectedUtc: "2026-10-07T01:00:00Z"
      }))
    };
    authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    expect(screen.getAllByRole("button", { name: "Dismiss" })).toHaveLength(3);
    fireEvent.click(screen.getByRole("button", { name: "Select all 3 to dismiss" }));
    expect(screen.getByRole("switch", { name: "Select Film a" })).toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss 3 selected" }));

    await waitFor(() => {
      const dismissed = authedFetchMock.mock.calls.map(([path]) => String(path)).filter((path) => path.endsWith("/dismiss"));
      expect(dismissed.sort()).toEqual([
        "/api/movies/import-recovery/case-a/dismiss",
        "/api/movies/import-recovery/case-b/dismiss",
        "/api/movies/import-recovery/case-c/dismiss"
      ]);
    });
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
      ignoredUntilUtc,
      ruleId: null,
      ruleActionPreset: null,
      ruleStrikeLimit: null,
      nextStrikeUtc: null
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

  // #1186: a gone download says its one plain thing once; the generic post-processing block is not shown beside it.
  it("drops the post-processing finding for a hand-off whose download was gone", () => {
    const post = { ...findingAt(null), kind: "post-processing-failed" };
    const other = { ...findingAt(null), kind: "import-failed" };
    const item = { healthFindings: [post, other] } as DownloadQueueItem;

    expect(visibleFindings(item, { status: "source-gone" }).map((finding) => finding.kind)).toEqual(["import-failed"]);
    expect(visibleFindings(item, { status: "failed" })).toHaveLength(2);
    expect(visibleFindings(item)).toHaveLength(2);
  });
});

describe("healthFindingRuleSummary (#905)", () => {
  function ruleFinding(overrides: Partial<DownloadHealthFinding> = {}): DownloadHealthFinding {
    return {
      severity: "warning",
      kind: "no-progress",
      summary: "This download has no observed throughput.",
      evidence: "0 MB/s for at least 30 minutes.",
      recommendedAction: "Check peers, then Resume or Recheck.",
      canSafelyRetry: true,
      canSafelyRemove: false,
      strikeCount: 2,
      candidateBlocked: false,
      ignoredUntilUtc: null,
      ruleId: "rule-1",
      ruleActionPreset: "replace",
      ruleStrikeLimit: 3,
      nextStrikeUtc: "2026-09-27T12:30:00Z",
      ...overrides
    };
  }

  it("names the rule, the strikes so far out of its limit, when the next one is due, and its action", () => {
    const summary = healthFindingRuleSummary(ruleFinding(), DEFAULT_DISPLAY_PREFERENCES);

    expect(summary).toContain("No progress rule");
    expect(summary).toContain("2 strikes so far of 3");
    expect(summary).toContain("set to replace it");
  });

  it("shows only the strike count for a finding outside the rule engine", () => {
    const summary = healthFindingRuleSummary(
      ruleFinding({ ruleId: null, ruleActionPreset: null, ruleStrikeLimit: null, nextStrikeUtc: null, strikeCount: 1 }),
      DEFAULT_DISPLAY_PREFERENCES
    );

    expect(summary).toBe("1 strike so far");
  });
});

describe("queueRowExtras (#796)", () => {
  it("reads quality, custom-format score and language from the matched dispatch's decision telemetry", () => {
    const item = {
      releaseName: "Movie.2026.1080p.WEB-DL-GROUP",
      sizeBytes: 4_294_967_296,
      etaSeconds: 0
    } as DownloadQueueItem;
    const dispatch = {
      decisionQuality: "WEB 1080p",
      decisionCustomFormatScore: 125,
      decisionLanguage: "English"
    } as DownloadDispatchItem;

    const extras = queueRowExtras(item, dispatch, false);

    expect(extras.quality).toBe("WEB 1080p");
    expect(extras.customFormat).toBe("125");
    expect(extras.language).toBe("English");
    expect(extras.size).toBe("4.0 GB");
  });

  it("falls back to a dash for quality, custom-format score and language when nothing is known", () => {
    const item = {
      releaseName: "Movie.2026.1080p.WEB-DL-GROUP",
      sizeBytes: 0,
      etaSeconds: 0
    } as DownloadQueueItem;

    const extras = queueRowExtras(item, null, false);

    expect(extras.quality).toBe("—");
    expect(extras.customFormat).toBe("—");
    expect(extras.language).toBe("—");
    expect(extras.timeleft).toBe("—");
  });

  it("only shows a timeleft while transferring, from the matching queue item", () => {
    const item = {
      releaseName: "Movie.2026.MULTI.1080p.WEB-DL-GROUP",
      sizeBytes: 0,
      etaSeconds: 120
    } as DownloadQueueItem;

    expect(queueRowExtras(item, null, true).timeleft).toBe("2m left");
    expect(queueRowExtras(item, null, false).timeleft).toBe("—");
  });
});

/** #1151: a failed processor hand-off is put right from the download's own drawer, once, blaming the processor. */
describe("download drawer: a failed processor hand-off (#1151)", () => {
  const reason = "The processor refused the hand-off with HTTP 400: Weir's watched folder is not set for this media scope.";

  afterEach(() => {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
    queueLoaderData.processorHandoffs = [] as unknown as typeof queueLoaderData.processorHandoffs;
    authedFetchMock.mockReset();
  });

  function openFailedHandoffDrawer() {
    const item = {
      id: "deluge-1", clientId: "deluge-client", clientName: "Deluge", protocol: "deluge", mediaType: "movies", title: "Nosferatu",
      releaseName: "Nosferatu.2024.1080p.BluRay-GROUP", category: "movies", status: "processingFailed", progress: 100, speedMbps: 0, etaSeconds: 0,
      sizeBytes: 1000, downloadedBytes: 1000, peers: 0, indexerName: "Fixture", errorMessage: reason, addedUtc: new Date().toISOString(),
      sourcePath: null, libraryId: null, healthFindings: null,
      failure: {
        serviceType: "processor", serviceId: "h1", serviceName: "Weir", operation: "hand-off", kind: "RejectedAction", retryState: "ManualAction",
        message: reason, code: "failed", httpStatus: null, upstreamDetail: null, externalId: "h1", retryAfterUtc: null, attempts: 0, isTransient: false,
        legacyCategory: "rejected", summary: `Weir hand-off failed: ${reason}`, nextAction: "Review the provider response and the requested action before retrying."
      }
    } as unknown as DownloadQueueItem;
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "deluge-client", clientName: "Deluge", protocol: "deluge", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0 },
        queue: [item], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    queueLoaderData.processorHandoffs = [{
      id: "h1", libraryId: "library-1", mediaType: "movies", clientId: "deluge-client", queueItemId: "deluge-1", releaseName: "Nosferatu.2024.1080p.BluRay-GROUP",
      sourcePath: "/downloads/nosferatu", processorName: "Weir", status: "failed", outputPath: null, importJobId: null, failureMessage: reason,
      createdUtc: new Date().toISOString(), updatedUtc: new Date().toISOString()
    }] as unknown as typeof queueLoaderData.processorHandoffs;

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );
    // The Needs attention row opens the download's own drawer.
    fireEvent.click(screen.getAllByText("Nosferatu.2024.1080p.BluRay-GROUP")[0]);
  }

  it("offers one send-again button, import original, search for another and dismiss in the download's own drawer", async () => {
    openFailedHandoffDrawer();

    expect(await screen.findByRole("button", { name: "Send to Weir again" })).toBeVisible();
    expect(screen.getAllByRole("button", { name: "Send to Weir again" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "Ask the processor again" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Import original" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Search for another release" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Dismiss this hand-off" })).toBeVisible();
  });

  it("says the processor refused it, once, and never blames the download client", async () => {
    openFailedHandoffDrawer();
    await screen.findByRole("button", { name: "Send to Weir again" });

    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getAllByText(reason)).toHaveLength(1);
    expect(within(dialog).queryByText(/Deluge.*queue failed/)).not.toBeInTheDocument();
  });

  it("sends the hand-off again with the same request the hand-off drawer used", async () => {
    authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });
    openFailedHandoffDrawer();

    fireEvent.click(await screen.findByRole("button", { name: "Send to Weir again" }));

    await waitFor(() => {
      expect(authedFetchMock).toHaveBeenCalledWith("/api/integrations/processors/handoffs/h1/retry", { method: "POST" });
    });
  });
});

/** #811/#970: qBittorrent's availability, last-activity and completion telemetry, shown in the download drawer. */
describe("download drawer: availability, last activity and completion (#970)", () => {
  afterEach(() => {
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [] as DownloadClientTelemetrySnapshot[]
    };
  });

  function queueItem(overrides: Partial<DownloadQueueItem> = {}): DownloadQueueItem {
    return {
      id: "qbit-1",
      clientId: "qbit-client",
      clientName: "qBittorrent",
      protocol: "qbittorrent",
      mediaType: "movies",
      title: "Example Movie",
      releaseName: "Example.Movie.2026.1080p.WEB-DL-GROUP",
      category: "movies",
      status: "downloading",
      progress: 40,
      speedMbps: 1.5,
      etaSeconds: 600,
      sizeBytes: 4_294_967_296,
      downloadedBytes: 1_073_741_824,
      peers: 6,
      indexerName: "Fixture indexer",
      errorMessage: null,
      addedUtc: new Date(Date.now() - 600_000).toISOString(),
      sourcePath: null,
      libraryId: null,
      healthFindings: null,
      ...overrides
    };
  }

  function openDrawerWith(item: DownloadQueueItem) {
    const client: DownloadClientTelemetrySnapshot = {
      clientId: "qbit-client",
      clientName: "qBittorrent",
      protocol: "qbittorrent",
      endpointUrl: null,
      healthStatus: "healthy",
      lastHealthMessage: null,
      capabilities: {
        supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true,
        supportsRecheck: true, supportsImportPath: true, authMode: "form"
      },
      summary: {
        activeCount: 1, queuedCount: 0, completedCount: 0, stalledCount: 0,
        processingCount: 0, importReadyCount: 0, totalSpeedMbps: 1.5, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0
      },
      queue: [item],
      history: [],
      capturedUtc: new Date().toISOString(),
      historyTruncated: false
    };
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [client]
    };

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByText(item.releaseName));
  }

  it("shows availability, last activity and completion in the drawer when the client reports them", () => {
    openDrawerWith(queueItem({
      availability: 1.5,
      lastActivityUtc: new Date(Date.now() - 120_000).toISOString(),
      completionUtc: null
    }));

    expect(screen.getByText("Availability")).toBeInTheDocument();
    expect(screen.getByText(/1\.50 \(1\.0 is one full copy/)).toBeInTheDocument();
    expect(screen.getByText("Last activity")).toBeInTheDocument();
    expect(screen.queryByText("Completed")).not.toBeInTheDocument();
  });

  it("offers no Preview import or Queue import for a magnet that has no files yet (#1138)", () => {
    openDrawerWith(queueItem({ sizeBytes: 0, downloadedBytes: 0, progress: 0, status: "queued" }));

    expect(screen.queryByRole("button", { name: "Preview import" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Queue import" })).not.toBeInTheDocument();
    expect(screen.getByText(/no files yet/i)).toBeInTheDocument();
  });

  it("still offers Preview import once the download has files (#1138)", () => {
    openDrawerWith(queueItem());

    expect(screen.getByRole("button", { name: "Preview import" })).toBeInTheDocument();
  });

  it("hides availability, last activity and completion facts entirely when the client never reported them", () => {
    openDrawerWith(queueItem({ availability: null, lastActivityUtc: null, completionUtc: null }));

    expect(screen.queryByText("Availability")).not.toBeInTheDocument();
    expect(screen.queryByText("Last activity")).not.toBeInTheDocument();
    expect(screen.queryByText("Completed")).not.toBeInTheDocument();
  });

  // #1026: Transmission and Deluge report a queue place and per-torrent seed limits.
  it("shows the place in the client queue and the seed limit when the client reports them", () => {
    openDrawerWith(queueItem({ queuePosition: 3, seedLimit: "Stops seeding at ratio 2 or after 30 minutes with no uploading." }));

    expect(screen.getByText("Place in queue")).toBeInTheDocument();
    expect(screen.getByText(/3 \(1 is next to start\)/)).toBeInTheDocument();
    expect(screen.getByText("Seed limit")).toBeInTheDocument();
    expect(screen.getByText(/Stops seeding at ratio 2/)).toBeInTheDocument();
  });

  it("hides the queue place and seed limit when the client reports neither", () => {
    openDrawerWith(queueItem({ queuePosition: null, seedLimit: null }));

    expect(screen.queryByText("Place in queue")).not.toBeInTheDocument();
    expect(screen.queryByText("Seed limit")).not.toBeInTheDocument();
  });
});

/** #1115: the history lives on Activity; Transfers keeps only a way there. */
describe("Transfers points to Activity for the history (#1115)", () => {
  it("has no Recent activity card, and links to Activity", () => {
    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    expect(screen.queryByRole("heading", { name: "Recent activity" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /See everything in Activity/ })).toHaveAttribute("href", "/activity");
  });
});

/** #1162: a download with a processor hand-off is one line, and the page does not repeat its help. */
describe("Transfers: one line per download, help said once", () => {
  afterEach(() => {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
    queueLoaderData.processorHandoffs = [];
  });

  function download(id: string, releaseName: string): DownloadQueueItem {
    return {
      id, clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies",
      title: releaseName, releaseName, category: "movies", status: "waiting-for-processor", progress: 100, speedMbps: 0,
      etaSeconds: 0, sizeBytes: 1_000_000_000, downloadedBytes: 1_000_000_000, peers: 0, indexerName: "Fixture indexer",
      errorMessage: null, addedUtc: new Date().toISOString(), sourcePath: null, libraryId: null, healthFindings: null
    } as DownloadQueueItem;
  }

  it("lists a download with a hand-off once, with the processor's stage in its row", async () => {
    const { repeatedSentences } = await import("../test/repeated-text");
    const kept = download("qbit-1", "Kept.In.The.List.2026.1080p.WEB-DL-GROUP");
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: 0, queuedCount: 0, completedCount: 1, stalledCount: 0, processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 1 },
        queue: [kept], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    queueLoaderData.processorHandoffs = [
      { id: "h1", libraryId: "l", mediaType: "movies", clientId: "qbit-client", queueItemId: "qbit-1", releaseName: kept.releaseName, sourcePath: "D:/in/a", processorName: "Weir", status: "processing", outputPath: null, importJobId: null, failureMessage: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z" },
      { id: "h2", libraryId: "l", mediaType: "movies", clientId: "qbit-client", queueItemId: "gone", releaseName: "Left.The.Client.2025.1080p-GROUP", sourcePath: "D:/in/b", processorName: "Weir", status: "processing", outputPath: null, importJobId: null, failureMessage: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z" }
    ] as never;

    const { container } = render(<MemoryRouter><QueuePage /></MemoryRouter>);

    expect(screen.getAllByText(kept.releaseName)).toHaveLength(2); // the one row: its name and its release line
    expect(screen.getAllByText("Left.The.Client.2025.1080p-GROUP").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Weir: /).length).toBeGreaterThan(0);
    expect(repeatedSentences(container)).toEqual([]);
  });
});

/** Rig, 8 Oct (#1155): for a few seconds the download has finished and no hand-off exists yet. */
describe("Transfers: before the hand-off exists", () => {
  afterEach(() => {
    queueLoaderData.telemetry = { ...queueLoaderData.telemetry, clients: [] as DownloadClientTelemetrySnapshot[] };
    queueLoaderData.processorHandoffs = [];
  });

  it("says the download is being handed over, not that it waits for the processor", () => {
    const item = {
      id: "qbit-9", clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", mediaType: "movies",
      title: "Just.Finished.2026.1080p-GROUP", releaseName: "Just.Finished.2026.1080p-GROUP", category: "movies", status: "waitingForProcessor", progress: 100, speedMbps: 0,
      etaSeconds: 0, sizeBytes: 1_000_000_000, downloadedBytes: 1_000_000_000, peers: 0, indexerName: "Fixture indexer",
      errorMessage: null, addedUtc: new Date().toISOString(), sourcePath: null, libraryId: null, healthFindings: null
    } as DownloadQueueItem;
    queueLoaderData.telemetry = {
      ...queueLoaderData.telemetry,
      clients: [{
        clientId: "qbit-client", clientName: "qBittorrent", protocol: "qbittorrent", endpointUrl: null, healthStatus: "healthy", lastHealthMessage: null,
        capabilities: { supportsQueue: true, supportsHistory: true, supportsPauseResume: true, supportsRemove: true, supportsRecheck: true, supportsImportPath: true, authMode: "form" },
        summary: { activeCount: 0, queuedCount: 0, completedCount: 1, stalledCount: 0, processingCount: 1, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 1 },
        queue: [item], history: [], capturedUtc: new Date().toISOString(), historyTruncated: false
      }]
    };
    queueLoaderData.processorHandoffs = [];

    render(<MemoryRouter><QueuePage /></MemoryRouter>);

    expect(screen.getAllByText("Handing over").length).toBeGreaterThan(0);
    expect(screen.queryByText(/Waiting for processor/i)).toBeNull();
    expect(screen.queryByText(/Waiting for the cleaned output/i)).toBeNull();
  });
});

/**
 * #1171: a one-file manual import says whose show it is, will not queue a TV
 * file that matched nothing, and cannot be queued twice.
 */
describe("Manual import: one file", () => {
  const authedFetch = authedFetchMock;

  function jsonResponse(body: unknown) {
    return Promise.resolve({
      ok: true,
      status: 200,
      json: () => Promise.resolve(body),
      text: () => Promise.resolve(JSON.stringify(body)),
      url: "test"
    } as Response);
  }

  async function openAndPreview(preview: Record<string, unknown>) {
    authedFetch.mockImplementation((path: string) => {
      if (path === "/api/filesystem/import/preview") return jsonResponse(preview);
      if (path === "/api/filesystem/import/jobs") {
        return jsonResponse({ jobId: "01a117f2aaaa", preview, job: {} });
      }
      return jsonResponse({});
    });
    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: /Manual import/ }));
    fireEvent.change(screen.getByPlaceholderText("D:/Downloads/Some.Release.2024"), { target: { value: "D:/Downloads/Show.S01E05.1080p.mkv" } });
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
  }

  it("asks for the title and keeps Queue import closed when a TV file matched nothing", async () => {
    await openAndPreview({ ...packPreview(true), pack: null, needsTitle: true, matchedTitle: null });

    await waitFor(() => expect(screen.getByText(/could not tell which show in your library this is/i)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: "Queue import" })).toBeDisabled();
  });

  it("says in words what was queued and where it will land, and cannot queue the same file twice", async () => {
    await openAndPreview({ ...packPreview(true), pack: null, matchedTitle: "Chernobyl (2019)", destinationPath: "TV/Chernobyl (2019)/Chernobyl - S01E05 - Vichnaya Pamyat.mkv" });

    await waitFor(() => expect(screen.getByText("Chernobyl (2019)")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "Queue import" }));

    await waitFor(() => expect(screen.getByText(/will be filed at/)).toBeInTheDocument());
    expect(screen.getByRole("link", { name: /Follow it in Activity/ })).toBeInTheDocument();
    expect(screen.queryByText(/Queued as job/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Queue import" })).toBeDisabled();
  });

  it("tells the person a download client is still sharing the file", async () => {
    await openAndPreview({ ...packPreview(true), pack: null, clientHoldsSource: true });

    await waitFor(() => expect(screen.getByText(/download client is still sharing this file/i)).toBeInTheDocument());
  });
});

/** #1159: a release a search held shows under Needs attention with Approve / Find another / Dismiss, and approving sends it. */
describe("a release held for approval (#1159)", () => {
  const row: HeldReleaseRow = {
    historyId: "h1", mediaType: "series", id: "s1", episodeId: "e1", title: "The Wire", year: 2002, episodeLabel: "S01E04", libraryId: "lib",
    releaseName: "The.Wire.S01E04.1080p.WEB-GRP", indexerName: "Nyaa", indexerId: "ix", downloadUrl: "https://x/get", quality: "WEB 1080p",
    sizeBytes: 1_000_000_000, seeders: 5, reason: "Held for your review.", heldUtc: "2026-10-08T00:00:00Z"
  };

  afterEach(() => {
    queueLoaderData.heldReleases = [];
    authedFetchMock.mockReset();
  });

  it("lists the candidate with its three answers", () => {
    queueLoaderData.heldReleases = [row];

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    expect(screen.getByText("The Wire S01E04")).toBeVisible();
    expect(screen.getByText(/Waiting for your OK: The.Wire.S01E04.1080p.WEB-GRP/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Approve The.Wire.S01E04.1080p.WEB-GRP" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Find another for The Wire S01E04" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Dismiss The.Wire.S01E04.1080p.WEB-GRP" })).toBeVisible();
  });

  it("approving asks the server to judge and send that release, and never calls the grab route itself", async () => {
    queueLoaderData.heldReleases = [row];
    authedFetchMock.mockResolvedValue({ ok: false, status: 409, json: async () => ({ message: "That title or episode no longer needs a release: the file you wanted is already there." }) });

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );
    fireEvent.click(screen.getByRole("button", { name: "Approve The.Wire.S01E04.1080p.WEB-GRP" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/attention/held-releases/h1/approve", { method: "POST" }));
    expect(authedFetchMock.mock.calls.some((call) => String(call[0]).includes("/grab"))).toBe(false);
  });
});

/** #1167: the number in the header is the number of rows, and the card says how every one of them is cleared. */
describe("Needs attention: the count matches the rows", () => {
  afterEach(() => {
    queueLoaderData.movieRecovery = { ...queueLoaderData.movieRecovery, recentCases: [] };
    queueLoaderData.orphans = [];
  });

  it("counts every row listed, and says which are dismissed together and which are cleared from their own row", () => {
    queueLoaderData.movieRecovery = {
      ...queueLoaderData.movieRecovery,
      recentCases: ["a", "b"].map((id) => ({
        id: `case-${id}`,
        title: `Film ${id}`,
        failureKind: "import-timeout",
        summary: "Import was detected but never completed after 24 hours.",
        recommendedAction: "Retry the import.",
        detailsJson: null,
        detectedUtc: "2026-10-07T01:00:00Z"
      }))
    };
    queueLoaderData.orphans = [{
      path: "/downloads/complete/Left.Over", name: "Left.Over", libraryId: "library-1", libraryName: "TV",
      sizeBytes: 1_073_741_824, lastWrittenUtc: new Date().toISOString()
    }];

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    // Two cases and one leftover folder: three rows listed, three counted.
    expect(screen.getByText(/3 things Deluno could not finish on its own\. 2 can be dismissed together; 1 leftover folder \(Remove or Keep\) are cleared from their own rows/)).toBeVisible();
    expect(screen.getByRole("button", { name: "Select all 2 to dismiss" })).toBeVisible();
    expect(screen.getByRole("switch", { name: "Select Film a" })).toBeVisible();
    expect(screen.getByRole("switch", { name: "Select Left.Over" })).toBeVisible();
  });
});

/** #1168: one download is one row, whichever stage it has reached. */
describe("Transfers: a download is in one table only", () => {
  afterEach(() => {
    queueLoaderData.processorHandoffs = [];
    queueLoaderData.jobs = [];
    queueLoaderData.failedJobs = [];
  });

  it("lists a failed import under Needs attention only, and says the connector's stage once", async () => {
    const { repeatedSentences } = await import("../test/repeated-text");
    const name = "Nosferatu.2024.1080p.WEB-DL-GRP";
    const job = { id: "job-n", jobType: "filesystem.import.execute", label: "Import", source: "x", sourceLabel: "x", status: "failed", attempts: 3, lastError: "The destination already holds a different file.", payloadJson: JSON.stringify({ title: name, sourcePath: `C:/Downloads/Completed/${name}` }), createdUtc: "2026-10-01T00:00:00Z", scheduledUtc: "2026-10-01T00:00:00Z", startedUtc: null, completedUtc: null, leasedUntilUtc: null, workerId: null, relatedEntityType: null, relatedEntityId: null, idempotencyKey: null, dedupeKey: null } as never;
    queueLoaderData.jobs = [job] as never;
    queueLoaderData.failedJobs = [job] as never;
    queueLoaderData.processorHandoffs = [
      { id: "hn", libraryId: "l", mediaType: "movies", clientId: "gone-client", queueItemId: "gone", releaseName: name, sourcePath: `C:/Downloads/Completed/${name}`, processorName: "Weir", status: "completed", outputPath: `C:/Out/${name}.mkv`, importJobId: "job-n", failureMessage: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z" }
    ] as never;

    const { container } = render(<MemoryRouter><QueuePage /></MemoryRouter>);

    const rows = Array.from(container.querySelectorAll('[role="row"]')).filter((row) => row.textContent?.includes(name));
    expect(rows).toHaveLength(1);
    expect(repeatedSentences(container, 25)).toEqual([]);
  });
});

describe("the connector's sub-line (#1168)", () => {
  it("is dropped when it only repeats the stage", async () => {
    const { connectorSubLine } = await import("./queue-screen");
    expect(connectorSubLine({ status: "completed", outputPath: "C:/Out/a.mkv" }, null)).toBeUndefined();
    expect(connectorSubLine({ status: "started", outputPath: null }, "Weir: working on it")).toBe("Weir: working on it");
  });
});

describe("a refused import on the Transfers drawer", () => {
  it("never reads Imported, and a real import does", () => {
    const refused = { id: "d", status: "sent", importStatus: "failed", importCompletedUtc: "2026-10-08T05:00:00Z", importFailureCode: "health-remediation-applied", importFailureMessage: "Replaced." } as never;
    expect(dispatchStageLabel(refused)).not.toBe("Imported");
    const sample = { id: "d", status: "sent", importStatus: "failed", importCompletedUtc: "2026-10-08T05:00:00Z", importFailureCode: "likelySample", importFailureMessage: "Likely a sample." } as never;
    expect(dispatchStageLabel(sample)).not.toBe("Imported");
    expect(dispatchStageLabel({ id: "d", status: "sent", importStatus: "imported", importCompletedUtc: "2026-10-08T05:00:00Z" } as never)).toBe("Imported");
  });
});

describe("Try again hides the old failure (#1198)", () => {
  it("is sending again only for a hand-off just sent again that waits on the processor", () => {
    const sent = new Set(["h1"]);
    expect(isResendingHandoff({ id: "h1", status: "waiting" }, sent)).toBe(true);
    expect(isResendingHandoff({ id: "h1", status: "submitted" }, sent)).toBe(true);
  });

  it("is not sending again once the answer is a failure, or for a hand-off nobody retried", () => {
    expect(isResendingHandoff({ id: "h1", status: "failed" }, new Set(["h1"]))).toBe(false);
    expect(isResendingHandoff({ id: "h2", status: "waiting" }, new Set(["h1"]))).toBe(false);
  });
});

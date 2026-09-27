import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadDispatchItem, DownloadHealthFinding, DownloadQueueItem, ImportPreviewResponse, LibraryItem, MediaProbeInfo } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { RemovalReasonDialog, type RemovalReason } from "../components/ui/removal-reason-dialog";
import { buildImportRequest, findDispatchForQueueItem, ImportPreviewFacts, QueuePage, reorderControlsFor, visibleFindings } from "./queue-screen";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const revalidateMock = vi.fn();
const queueLoaderData = {
  telemetry: {
    summary: {
      activeCount: 0, queuedCount: 0, completedCount: 0, stalledCount: 0,
      processingCount: 0, importReadyCount: 0, totalSpeedMbps: 0, totalUploadSpeedMbps: 0, waitingForProcessorCount: 0
    },
    clients: [],
    capturedUtc: new Date().toISOString()
  },
  dispatches: [],
  movieRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] },
  seriesRecovery: { openCount: 0, qualityCount: 0, unmatchedCount: 0, corruptCount: 0, downloadFailedCount: 0, importFailedCount: 0, recentCases: [] },
  libraries: [],
  settings: emptyPlatformSettingsSnapshot,
  jobs: [],
  failedJobs: [],
  healthRecords: [],
  processorConnections: [],
  processorHandoffs: [],
  activityEvents: [],
  orphans: []
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => queueLoaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" }),
  useNavigate: () => vi.fn()
}));

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

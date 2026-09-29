import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientTelemetrySnapshot, DownloadDispatchItem, DownloadHealthFinding, DownloadQueueItem, ImportPreviewResponse, LibraryItem, MediaProbeInfo, OrphanedDownloadItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { RemovalReasonDialog, type RemovalReason } from "../components/ui/removal-reason-dialog";
import { buildImportRequest, findDispatchForQueueItem, healthFindingRuleSummary, ImportPreviewFacts, QueuePage, queueRowExtras, reorderControlsFor, visibleFindings } from "./queue-screen";
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
  orphans: [] as OrphanedDownloadItem[]
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
      delunoGrabbedIt: true,
      ...overrides
    };
  }

  it("shows the select-all button and selects every leftover Deluno can prove it downloaded", () => {
    queueLoaderData.orphans = [
      orphan({ path: "C:\\Downloads\\Complete\\First.S01E01", name: "First.S01E01" }),
      orphan({ path: "C:\\Downloads\\Complete\\Second.S01E02", name: "Second.S01E02" }),
      // Not Deluno's own grab: never selectable, so select-all must skip it.
      orphan({ path: "C:\\Downloads\\Complete\\Not.Deluno.S01E03", name: "Not.Deluno.S01E03", delunoGrabbedIt: false })
    ];

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    const selectAll = screen.getByRole("button", { name: "Select all leftovers" });
    expect(selectAll).toBeVisible();
    // The item Deluno cannot prove it downloaded gets no checkbox at all.
    expect(screen.queryByRole("switch", { name: "Select Not.Deluno.S01E03" })).not.toBeInTheDocument();

    fireEvent.click(selectAll);

    expect(screen.getByRole("switch", { name: "Select First.S01E01" })).toBeChecked();
    expect(screen.getByRole("switch", { name: "Select Second.S01E02" })).toBeChecked();
    expect(screen.getByRole("button", { name: "Remove 2 selected" })).toBeVisible();
  });

  it("offers no select-all button when there is nothing Deluno can remove", () => {
    queueLoaderData.orphans = [orphan({ delunoGrabbedIt: false })];

    render(
      <MemoryRouter>
        <QueuePage />
      </MemoryRouter>
    );

    expect(screen.queryByRole("button", { name: "Select all leftovers" })).not.toBeInTheDocument();
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

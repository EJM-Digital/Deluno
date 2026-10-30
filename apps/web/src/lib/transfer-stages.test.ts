import { describe, expect, it } from "vitest";
import type { JobQueueItem, ProcessorHandoffItem } from "./api";
import { placeDownloadsOnce, type TransferStageInput } from "./transfer-stages";

function handoff(id: string, releaseName: string, extra: Partial<ProcessorHandoffItem> = {}): ProcessorHandoffItem {
  return {
    id, libraryId: "l", mediaType: "movies", clientId: "qbit", queueItemId: `q-${id}`, releaseName,
    sourcePath: `C:/Downloads/Completed/${releaseName}`, processorName: "Weir", status: "completed", outputPath: `C:/Out/${releaseName}.mkv`,
    importJobId: null, failureMessage: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...extra
  } as ProcessorHandoffItem;
}

function importJob(id: string, title: string, extra: Record<string, unknown> = {}): JobQueueItem {
  return { id, jobType: "filesystem.import.execute", status: "completed", attempts: 1, lastError: null, payloadJson: JSON.stringify({ title, ...extra }) } as unknown as JobQueueItem;
}

const none: TransferStageInput = { handoffs: [], importJobs: [], failedImportJobs: [], failedHandoffs: [], failedDownloads: [], queueAttention: [] };

/** Every id a download is known by, per table: the test asserts no download is in two. */
function tablesOf(result: ReturnType<typeof placeDownloadsOnce>, input: TransferStageInput) {
  const tables: Record<string, string[]> = {
    processing: result.handoffs.map((h) => h.releaseName),
    imports: result.importJobs.map((j) => JSON.parse(j.payloadJson!).title as string),
    attention: [...input.failedImportJobs.map((j) => JSON.parse(j.payloadJson!).title as string), ...input.failedHandoffs.map((h) => h.releaseName)]
  };
  return tables;
}

describe("placeDownloadsOnce (#1168)", () => {
  it("lists a download that failed to import only under Needs attention", () => {
    const failedJob = importJob("job-1", "Nosferatu.2024.1080p-GRP", { sourcePath: "C:/Downloads/Completed/Nosferatu.2024.1080p-GRP" });
    const nosferatu = handoff("h1", "Nosferatu.2024.1080p-GRP", { importJobId: "job-1" });
    const input: TransferStageInput = { ...none, handoffs: [nosferatu], importJobs: [failedJob], failedImportJobs: [failedJob] };
    const result = placeDownloadsOnce(input);
    expect(result.handoffs).toEqual([]);
    expect(result.importJobs).toEqual([]);
  });

  it("lists a download that is importing under Import & naming, not also under Processing connector", () => {
    const job = importJob("job-2", "Severance", { fileName: "Severance.S02E01.1080p-GRP.mkv" });
    const severance = handoff("h2", "Severance.S02E01.1080p-GRP", { importJobId: "job-2" });
    const byName = handoff("h3", "The.Phantom.of.the.Opera.1925-GRP", { outputPath: null });
    const operaJob = importJob("job-3", "The Phantom of the Opera", { fileName: "The.Phantom.of.the.Opera.1925-GRP.mkv" });
    const result = placeDownloadsOnce({ ...none, handoffs: [severance, byName], importJobs: [job, operaJob] });
    expect(result.handoffs).toEqual([]);
    expect(result.importJobs).toHaveLength(2);
  });

  it("matches a Windows path to its file name", () => {
    const job = importJob("job-w", "Other", { sourcePath: "D:\\in\\Same.File.2020-GRP.mkv" });
    const moved = handoff("h-w", "Unrelated Name", { sourcePath: "E:\\x\\Same.File.2020-GRP", outputPath: null });
    expect(placeDownloadsOnce({ ...none, handoffs: [moved], importJobs: [job] }).handoffs).toEqual([]);
  });

  it("keeps a hand-off still with the processor, and an import of something else, where they are", () => {
    const working = handoff("h4", "Still.Working.2026-GRP", { status: "started", outputPath: null });
    const other = importJob("job-4", "Another.Film.2020-GRP");
    const result = placeDownloadsOnce({ ...none, handoffs: [working], importJobs: [other] });
    expect(result.handoffs).toEqual([working]);
    expect(result.importJobs).toEqual([other]);
  });

  it("puts a download in no more than one table across a mixed set", () => {
    const failedJob = importJob("j1", "Nosferatu.2024-GRP");
    const failedHandoff = handoff("h6", "Failing.Show.S01E02-GRP", { status: "failed", failureMessage: "boom" });
    const input: TransferStageInput = {
      handoffs: [handoff("h5", "Nosferatu.2024-GRP", { importJobId: "j1" }), failedHandoff, handoff("h7", "Quiet.One.2019-GRP", { status: "started", outputPath: null })],
      importJobs: [failedJob, importJob("j2", "Failing.Show.S01E02-GRP"), importJob("j3", "Imported.Fine.2018-GRP")],
      failedImportJobs: [failedJob],
      failedHandoffs: [failedHandoff],
      failedDownloads: [],
      queueAttention: []
    };
    const result = placeDownloadsOnce(input);
    const seen = new Map<string, string>();
    for (const [table, names] of Object.entries(tablesOf(result, input))) {
      for (const name of names) {
        const key = name.toLowerCase().replace(/[^a-z0-9]/g, "");
        expect(seen.has(key), `${name} is in ${seen.get(key)} and ${table}`).toBe(false);
        seen.set(key, table);
      }
    }
    expect(result.handoffs.map((h) => h.releaseName)).toEqual(["Quiet.One.2019-GRP"]);
    expect(result.importJobs.map((j) => j.id)).toEqual(["j3"]);
  });

  it("keeps a download out of Processing when it failed in the client, by its client id or, with no id, by its name", () => {
    const byId = handoff("h8", "Some.Film.2020-GRP", { clientId: "qbit", queueItemId: "abc123", outputPath: null });
    const byName = handoff("h9", "Other.Film.2021-GRP", { clientId: "qbit", queueItemId: "zzz", outputPath: null });
    const failedDownloads = [
      { dispatch: { releaseName: "Unrelated", downloadClientId: "qbit", torrentHashOrItemId: "ABC123" } },
      { dispatch: { releaseName: "Other.Film.2021-GRP", downloadClientId: "qbit", torrentHashOrItemId: null } }
    ] as unknown as TransferStageInput["failedDownloads"];
    const result = placeDownloadsOnce({ ...none, handoffs: [byId, byName], failedDownloads });
    expect(result.handoffs).toEqual([]);
  });

  it("keeps a download out of Processing when the queue says it needs attention", () => {
    const stuck = handoff("h10", "Stuck.Film.2022-GRP", { clientId: "qbit", queueItemId: "q-stuck", outputPath: null });
    const queueAttention = [{ clientId: "qbit", id: "q-stuck", releaseName: "Stuck.Film.2022-GRP" }] as unknown as TransferStageInput["queueAttention"];
    expect(placeDownloadsOnce({ ...none, handoffs: [stuck], queueAttention }).handoffs).toEqual([]);
  });
});

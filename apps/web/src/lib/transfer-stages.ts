import type { DownloadQueueItem, JobQueueItem, ProcessorHandoffItem } from "./api";
import { parseImportJobPayload } from "./import-job-payload";
import type { FailedDownloadRow } from "./needs-action";

/**
 * One download, one table (#1162, #1168).
 *
 * A single download can leave a trail of records as it moves: a processor
 * hand-off, an import job and, if it went wrong, a failure. Each used to get
 * its own row in its own In progress table, so one film showed three times. Every
 * record is reduced to the names, paths and ids that identify its download, and
 * a download is shown only in the table for where it is now: a problem wins over
 * the import stage, and the import stage wins over the processor's.
 */
// A file is named like its release with an extension on the end, so the extension is not part of what identifies it.
const comparable = (value: string | null | undefined) =>
  (value ?? "").toLowerCase().replace(/\.(mkv|mp4|m4v|avi|mov|wmv|ts|m2ts)$/i, "").replace(/[^\p{L}\p{N}]/gu, "");

function fileName(path: string | null | undefined) {
  return path?.split(/[\\/]/).filter(Boolean).at(-1) ?? null;
}

function tokens(parts: { job?: string | null; download?: [string, string] | null; names: (string | null | undefined)[]; paths?: (string | null | undefined)[] }) {
  const result: string[] = [];
  if (parts.job) result.push(`job:${parts.job.toLowerCase()}`);
  if (parts.download) result.push(`dl:${`${parts.download[0]}:${parts.download[1]}`.toLowerCase()}`);
  for (const name of parts.names) {
    const key = comparable(name);
    if (key) result.push(`name:${key}`);
  }
  for (const path of parts.paths ?? []) {
    const key = comparable(path);
    if (key) result.push(`path:${key}`);
    const leaf = comparable(fileName(path));
    if (leaf) result.push(`name:${leaf}`);
  }
  return result;
}

export function handoffTokens(handoff: ProcessorHandoffItem) {
  return tokens({
    job: handoff.importJobId,
    download: [handoff.clientId, handoff.queueItemId],
    names: [handoff.releaseName],
    paths: [handoff.sourcePath, handoff.outputPath]
  });
}

export function importJobTokens(job: JobQueueItem) {
  const info = parseImportJobPayload(job.payloadJson);
  return tokens({ job: job.id, names: [info?.title, info?.fileName], paths: [info?.sourcePath, info?.destinationPath] });
}

export interface TransferStageInput {
  handoffs: ProcessorHandoffItem[];
  importJobs: JobQueueItem[];
  failedImportJobs: JobQueueItem[];
  failedHandoffs: ProcessorHandoffItem[];
  failedDownloads: FailedDownloadRow[];
  queueAttention: DownloadQueueItem[];
}

export function placeDownloadsOnce(input: TransferStageInput) {
  const problem = new Set<string>();
  for (const job of input.failedImportJobs) importJobTokens(job).forEach((token) => problem.add(token));
  for (const handoff of input.failedHandoffs) handoffTokens(handoff).forEach((token) => problem.add(token));
  for (const { dispatch } of input.failedDownloads) {
    tokens({ names: [dispatch.releaseName] }).forEach((token) => problem.add(token));
    if (dispatch.torrentHashOrItemId) problem.add(`dl:${`${dispatch.downloadClientId}:${dispatch.torrentHashOrItemId}`.toLowerCase()}`);
  }
  for (const item of input.queueAttention) tokens({ download: [item.clientId, item.id], names: [item.releaseName] }).forEach((token) => problem.add(token));

  const failedJobIds = new Set(input.failedImportJobs.map((job) => job.id));
  const importJobs = input.importJobs.filter((job) => !failedJobIds.has(job.id) && !importJobTokens(job).some((token) => problem.has(token)));
  const importing = new Set<string>();
  for (const job of importJobs) importJobTokens(job).forEach((token) => importing.add(token));

  const handoffs = input.handoffs.filter((handoff) => {
    const own = handoffTokens(handoff);
    return !own.some((token) => problem.has(token) || importing.has(token));
  });
  return { handoffs, importJobs };
}

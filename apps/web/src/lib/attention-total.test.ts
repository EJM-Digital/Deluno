import { describe, expect, it } from "vitest";
import { attentionTotal, type AttentionSnapshot } from "./use-attention";

const snapshot = (over: Partial<AttentionSnapshot>): AttentionSnapshot => ({
  failedJobs: 0,
  deadLetteredJobs: 0,
  indexerAlerts: 0,
  downloadsNeedingAction: 0,
  failedImportJobs: 0,
  movieWanted: 0,
  tvWanted: 0,
  healthProblems: 0,
  loading: false,
  ...over
});

describe("attentionTotal", () => {
  it("counts a failed import once: it is one of the failed jobs and one of the downloads Transfers lists", () => {
    // One failed job that is an import, and it is also among the 80 downloads needing action.
    expect(attentionTotal(snapshot({ failedJobs: 1, failedImportJobs: 1, downloadsNeedingAction: 80, indexerAlerts: 1 }))).toBe(81);
  });

  it("still counts a failed job that is not an import, beside the downloads", () => {
    expect(attentionTotal(snapshot({ failedJobs: 3, failedImportJobs: 1, downloadsNeedingAction: 5, healthProblems: 2 }))).toBe(9);
  });
});

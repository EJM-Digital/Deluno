import { describe, expect, it } from "vitest";
import { computeAttention } from "../routes/dashboard/attention";
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
  total: 0,
  needs: computeAttention(undefined),
  loading: false,
  ...over
});

describe("attentionTotal", () => {
  it("is the one count the bell shows and the Dashboard's two badges add up to, not a second sum of its own", () => {
    // The pieces below would add to 9 under the old sidebar rule; the number is the Dashboard's.
    expect(attentionTotal(snapshot({ failedJobs: 3, failedImportJobs: 1, downloadsNeedingAction: 5, healthProblems: 2, total: 12 }))).toBe(12);
    expect(attentionTotal(snapshot({}))).toBe(0);
  });
});

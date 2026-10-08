import { beforeEach, describe, expect, it, vi } from "vitest";

const authedFetch = vi.fn();
vi.mock("./use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

import { retryFailedJob } from "./failed-jobs";

describe("Try again on a failed job (#1155)", () => {
  beforeEach(() => authedFetch.mockReset());

  it("calls the single-job endpoint with that job's id, never the retry-everything one", async () => {
    authedFetch.mockResolvedValue({ ok: true });
    await retryFailedJob("job 1");
    expect(authedFetch).toHaveBeenCalledTimes(1);
    expect(authedFetch).toHaveBeenCalledWith("/api/jobs/job%201/retry", { method: "POST" });
  });

  it("says so when the job could not be tried again", async () => {
    authedFetch.mockResolvedValue({ ok: false, status: 404 });
    await expect(retryFailedJob("job-1")).rejects.toThrow("could not be tried again");
  });
});

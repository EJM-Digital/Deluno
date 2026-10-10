import { beforeEach, describe, expect, it, vi } from "vitest";

const authedFetch = vi.fn();
vi.mock("./use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));

import { dismissFailedDispatch, retryFailedJob } from "./failed-jobs";

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

describe("Dismiss on a failed send (#1218)", () => {
  beforeEach(() => authedFetch.mockReset());

  it("calls the dismiss endpoint for that send only", async () => {
    authedFetch.mockResolvedValue({ ok: true });
    await dismissFailedDispatch("d 1");
    expect(authedFetch).toHaveBeenCalledWith("/api/download-dispatches/d%201/dismiss", { method: "POST" });
  });

  it("treats a send that is gone or no longer failed as already cleared", async () => {
    authedFetch.mockResolvedValue({ ok: false, status: 409 });
    await expect(dismissFailedDispatch("d1")).resolves.toBeUndefined();
  });

  it("says so when the server could not do it", async () => {
    authedFetch.mockResolvedValue({ ok: false, status: 500 });
    await expect(dismissFailedDispatch("d1")).rejects.toThrow("could not be dismissed");
  });
});

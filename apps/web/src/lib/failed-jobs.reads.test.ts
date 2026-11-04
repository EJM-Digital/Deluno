import { beforeEach, describe, expect, it, vi } from "vitest";

const authedFetch = vi.fn();
const fetchAllPages = vi.fn();
vi.mock("./use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetch(...args) }));
vi.mock("./api", () => ({ fetchAllPages: (...args: unknown[]) => fetchAllPages(...args) }));

import { dismissFailedDispatch, dismissFailedJob, fetchFailedJobs } from "./failed-jobs";

describe("the failed jobs wherever they sit in the queue", () => {
  beforeEach(() => { authedFetch.mockReset(); fetchAllPages.mockReset(); });

  it("asks for failures by name and keeps only the ones that failed, even if an older server sent every job", async () => {
    fetchAllPages.mockResolvedValue([
      { id: "a", status: "failed" },
      { id: "b", status: "completed" },
      { id: "c", status: "queued" }
    ]);

    const jobs = await fetchFailedJobs();

    expect(fetchAllPages).toHaveBeenCalledWith("/api/jobs?failed=true&pageSize=500&includePayload=true");
    expect(jobs.map((job) => job.id)).toEqual(["a"]);
  });
});

describe("Dismiss on a failed job", () => {
  beforeEach(() => authedFetch.mockReset());

  it("calls the dismiss endpoint for that job only", async () => {
    authedFetch.mockResolvedValue({ ok: true });
    await dismissFailedJob("job 1");
    expect(authedFetch).toHaveBeenCalledWith("/api/jobs/job%201/dismiss", { method: "POST" });
  });

  it("treats a job that is no longer failed as already cleared", async () => {
    authedFetch.mockResolvedValue({ ok: false, status: 404 });
    await expect(dismissFailedJob("job-1")).resolves.toBeUndefined();
  });

  it("says so when the server could not do it", async () => {
    authedFetch.mockResolvedValue({ ok: false, status: 500 });
    await expect(dismissFailedJob("job-1")).rejects.toThrow("That job could not be dismissed.");
  });

  it("does not treat a refusal of a failed send with another status as cleared", async () => {
    authedFetch.mockResolvedValue({ ok: false, status: 404 });
    await expect(dismissFailedDispatch("d1")).resolves.toBeUndefined();
    authedFetch.mockResolvedValue({ ok: false, status: 400 });
    await expect(dismissFailedDispatch("d1")).rejects.toThrow();
  });
});

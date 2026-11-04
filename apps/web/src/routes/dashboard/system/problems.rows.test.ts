import { describe, expect, it } from "vitest";
import type { HealthCheckResultItem } from "../../../lib/api";
import { healthProblemActions, jobsGaveUpActions } from "../../../lib/alert-actions";
import { serverRow, summaryOf } from "../../../lib/attention-summary-fixtures";
import { emptySummary } from "../summary-fixtures";
import { buildHealth, problemButton, STARTUP_PREFIX } from "./problems";

const check = (id: string, name: string, over: Partial<HealthCheckResultItem> = {}): HealthCheckResultItem => ({
  id, category: "machine", name, status: "problem", message: `${name} message`, cause: null, fix: null, detailsJson: null,
  lastRunUtc: "2026-10-01T09:00:00Z", problemSinceUtc: "2026-10-01T08:00:00Z", snoozedUntilUtc: null, ...over
});
const none = summaryOf([]);
const rowOf = (model: ReturnType<typeof buildHealth>, id: string) => model.rows.find((row) => row.id === id)!;

describe("a button made from the shared definition", () => {
  it("opens a screen when the action has one", () => {
    expect(problemButton({ id: "a", label: "Open", hint: "Opens it.", to: "/system" } as never)).toEqual({ label: "Open", note: "Opens it.", to: "/system" });
  });

  it("sends a request when the action has one, naming what happens either way", () => {
    const button = problemButton({ id: "a", label: "Retry", hint: "Retries.", request: { method: "POST", path: "/x", body: { a: 1 }, failure: "Failed.", done: "Done." } } as never);
    expect(button).toEqual({ label: "Retry", note: "Retries.", post: { method: "POST", path: "/x", body: { a: 1 }, failure: "Failed.", done: "Done." } });
  });

  it("has neither when the action does nothing", () => {
    expect(problemButton({ id: "a", label: "None", hint: "Nothing." } as never)).toEqual({ label: "None", note: "Nothing.", post: undefined });
  });

  it("exports the startup problem prefix", () => {
    expect(STARTUP_PREFIX.length).toBeGreaterThan(0);
  });
});

describe("the health rows from the stored checks", () => {
  it("lists an open problem with its cause and fix, how long it has been wrong and its buttons", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("machine:sleep", "Computer sleep", { cause: "Sleep is on.", fix: "Turn it off." })] });
    const row = rowOf(model, "problem:machine:sleep");
    expect(row).toMatchObject({ area: "machine", tone: "bad", title: "Computer sleep", why: "Computer sleep message Sleep is on. Turn it off.", atUtc: "2026-10-01T08:00:00Z" });
    expect(row.buttons.length).toBeGreaterThan(0);
    expect(model.need).toBe(1);
    expect(model.more).toBe(0);
  });

  it("says just the message when there is no cause or fix, and the cause or fix alone when only one is given", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A"), check("b", "B", { cause: "Because." }), check("c", "C", { fix: "Do this." })] });
    expect(rowOf(model, "problem:a").why).toBe("A message");
    expect(rowOf(model, "problem:b").why).toBe("B message Because.");
    expect(rowOf(model, "problem:c").why).toBe("C message Do this.");
  });

  it("dates a problem from when it was last run when it has no start", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { problemSinceUtc: null })] });
    expect(rowOf(model, "problem:a").atUtc).toBe("2026-10-01T09:00:00Z");
  });

  it("sends a problem to the screen the summary names, or to System", () => {
    const summary = emptySummary();
    summary.health = { ...summary.health!, topProblems: [{ id: "a", title: "A", category: "machine", message: "m", sinceUtc: null, link: "/settings/libraries", snoozed: false } as never] };
    const model = buildHealth({ summary, attention: none, checks: [check("a", "A"), check("b", "B")] });
    expect(rowOf(model, "problem:a").buttons.some((b) => b.to === "/settings/libraries")).toBe(true);
    expect(rowOf(model, "problem:b").buttons.some((b) => b.to === "/settings/libraries")).toBe(false);
  });

  it("puts snoozed problems after the live ones, with when they wake and a way to unsnooze", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { snoozedUntilUtc: "2999-01-05T00:00:00Z" })] });
    const row = rowOf(model, "snoozed:a");
    expect(row).toMatchObject({ tone: "idle", note: "Snoozed until 5 Jan", atUtc: "2026-10-01T08:00:00Z" });
    expect(row.buttons).toEqual([expect.objectContaining({ label: "Unsnooze", post: expect.objectContaining({ path: "/api/system/health/a/unsnooze" }) })]);
    expect(model.need).toBe(0);
    expect(model.rows.some((r) => r.id === "problem:a")).toBe(false);
  });

  it("dates a snoozed problem from its last run when it has no start", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { snoozedUntilUtc: "2999-01-05T00:00:00Z", problemSinceUtc: null })] });
    expect(rowOf(model, "snoozed:a").atUtc).toBe("2026-10-01T09:00:00Z");
  });

  it("treats a problem whose snooze has passed as live again", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { snoozedUntilUtc: "2000-01-01T00:00:00Z" })] });
    expect(model.rows.some((r) => r.id === "problem:a")).toBe(true);
  });

  it("lists what is not checked yet, suggestions and what passes, quietly and last, in that order", () => {
    const model = buildHealth({
      summary: emptySummary(), attention: none,
      checks: [check("ok1", "Fine", { status: "ok" }), check("sug", "Idea", { status: "suggestion" }), check("unk", "Unknown", { status: "unknown" }), check("bad", "Bad")]
    });
    expect(model.rows.map((r) => r.id)).toEqual(["problem:bad", "unknown:unk", "suggestion:sug", "ok:ok1"]);
    expect(rowOf(model, "unknown:unk")).toMatchObject({ tone: "idle", note: "Not checked yet", atUtc: null, buttons: [] });
    expect(rowOf(model, "suggestion:sug")).toMatchObject({ tone: "idle", note: "Suggestion", atUtc: null, buttons: [] });
    expect(rowOf(model, "ok:ok1")).toMatchObject({ tone: "ok", atUtc: "2026-10-01T09:00:00Z", buttons: [] });
  });

  it("counts passing checks and all checks except suggestions, and says when the checks were last run", () => {
    const summary = emptySummary();
    summary.health = { ...summary.health!, lastRunUtc: "2026-10-01T09:30:00Z" };
    const model = buildHealth({ summary, attention: none, checks: [check("ok1", "A", { status: "ok" }), check("sug", "B", { status: "suggestion" }), check("bad", "C")] });
    expect(model).toMatchObject({ pass: 1, total: 2, lastRunUtc: "2026-10-01T09:30:00Z" });
    expect(buildHealth({ summary: undefined, attention: none, checks: undefined })).toMatchObject({ pass: 0, total: 0, need: 0, lastRunUtc: null, rows: [] });
  });

  it("tallies each area: bad when a problem is open, ok otherwise, with its totals", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { category: "library" }), check("b", "B", { category: "library", status: "ok" }), check("c", "C", { category: "indexer", status: "ok" })] });
    expect(model.tallies.map((t) => t.key)).toEqual(["library", "downloads", "connections", "processing", "storage", "security", "machine"]);
    expect(model.tallies.find((t) => t.key === "library")).toEqual({ key: "library", name: "Library", total: 2, ok: 1, tone: "bad" });
    expect(model.tallies.find((t) => t.key === "connections")).toMatchObject({ total: 1, ok: 1, tone: "ok" });
    expect(model.tallies.find((t) => t.key === "machine")).toMatchObject({ total: 0, ok: 0, tone: "ok" });
  });

  it("tallies an area as a warning when only warnings are open there", () => {
    const attention = summaryOf([serverRow({ id: "storage-low", kind: "storage", tone: "warn", title: "Disk is filling" })]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: [] });
    expect(model.tallies.find((t) => t.key === "storage")?.tone).toBe("warn");
  });
});

describe("the rows the server adds beyond the stored checks", () => {
  it("lists other Deluno problems from the server, bad or warning, with a button to the screen that deals with them", () => {
    const attention = summaryOf([
      serverRow({ id: "storage-low", kind: "storage", tone: "bad", title: "Disk is full", text: "No room.", to: "/settings/libraries", action: "Open Libraries" }),
      serverRow({ id: "updates", kind: "updates", tone: "warn", title: "Update ready", text: "A new version." }),
      serverRow({ id: "media-row", kind: "stuck", section: "media" })
    ]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: [] });
    expect(rowOf(model, "item:storage-low")).toMatchObject({ area: "storage", tone: "bad", title: "Disk is full", why: "No room.", atUtc: null });
    expect(rowOf(model, "item:storage-low").buttons[0]).toMatchObject({ label: "Open Libraries", to: "/settings/libraries" });
    expect(rowOf(model, "item:updates").tone).toBe("warn");
    expect(model.rows.some((r) => r.id === "item:media-row")).toBe(false);
  });

  it("leaves connections, jobs, workers and health problems to their own rows", () => {
    const attention = summaryOf([
      serverRow({ id: "client:c1", kind: "connection" }),
      serverRow({ id: "jobs-gave-up", kind: "jobs" }),
      serverRow({ id: "lanes", kind: "lanes" }),
      serverRow({ id: "health:x", kind: "health:machine" })
    ]);
    expect(buildHealth({ summary: emptySummary(), attention, checks: [] }).rows.some((r) => r.id.startsWith("item:"))).toBe(false);
  });

  it("lists jobs that gave up, saying whether they are title jobs or background ones", () => {
    const attention = summaryOf([
      serverRow({ id: "failed-title-jobs", kind: "jobs", tone: "bad", title: "Title jobs gave up", text: "3 failed." }),
      serverRow({ id: "failed-background-jobs", kind: "jobs", tone: "bad", title: "Background jobs gave up", text: "2 failed." })
    ]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: [] });
    expect(rowOf(model, "title-jobs-gave-up")).toMatchObject({ area: "machine", tone: "bad", title: "Title jobs gave up", why: "3 failed.", atUtc: null });
    expect(rowOf(model, "title-jobs-gave-up").buttons.map((b) => b.label)).toEqual(jobsGaveUpActions("title").map((a) => a.label));
    expect(rowOf(model, "title-jobs-gave-up").buttons[1]?.to).toBe(jobsGaveUpActions("title")[1]?.to);
    expect(rowOf(model, "jobs-gave-up").buttons[1]?.to).toBe(jobsGaveUpActions("background")[1]?.to);
    expect(jobsGaveUpActions("title")[1]?.to).not.toBe(jobsGaveUpActions("background")[1]?.to);
  });

  it("lists a worker that stopped, with its last error or a plain sentence", () => {
    const summary = emptySummary();
    summary.system = { ...summary.system!, lanes: [{ name: "Searching", state: "failing", activeJobs: 0, lastError: "boom" }, { name: "importing-files", state: "failing", activeJobs: 0, lastError: null }, { name: "Fine", state: "running", activeJobs: 0, lastError: null }] } as never;
    const model = buildHealth({ summary, attention: none, checks: [] });
    expect(rowOf(model, "lane:Searching")).toMatchObject({ area: "machine", tone: "bad", title: "Searching isn’t running", why: "boom", atUtc: null });
    expect(rowOf(model, "lane:Searching").buttons[0]).toMatchObject({ label: "Look", to: "/system" });
    expect(rowOf(model, "lane:importing-files").why).toBe("It stopped and hasn’t come back.");
    expect(model.rows.some((r) => r.id === "lane:Fine")).toBe(false);
  });

  it("uses the server's health rows when the stored checks have not arrived", () => {
    const attention = summaryOf([
      serverRow({ id: "health:machine:sleep", kind: "health:machine", title: "Computer sleep", text: "It sleeps.", sinceUtc: "2026-10-01T08:00:00Z", data: { id: "machine:sleep", category: "machine", link: "/settings/x" } }),
      serverRow({ id: "health:machine:other", kind: "health:machine", title: "Other", text: "Other text.", data: { id: "machine:other" } })
    ]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: undefined });
    expect(rowOf(model, "problem:machine:sleep")).toMatchObject({ area: "machine", tone: "bad", title: "Computer sleep", why: "It sleeps.", atUtc: "2026-10-01T08:00:00Z" });
    expect(rowOf(model, "problem:machine:sleep").buttons.map((b) => b.to)).toEqual(healthProblemActions({ id: "machine:sleep", name: "Computer sleep", category: "machine", link: "/settings/x" }).map((a) => a.to));
    expect(rowOf(model, "problem:machine:other").buttons.map((b) => b.to)).toEqual(healthProblemActions({ id: "machine:other", name: "Other", category: undefined, link: "/system" }).map((a) => a.to));
  });

  it("shows a stored check for a connection under the connection's own row, once", () => {
    const attention = summaryOf([serverRow({ id: "client:c1", kind: "connection" }), serverRow({ id: "indexer-1", kind: "connection" })]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: [check("download-client:c1", "qBittorrent", { category: "download-client" }), check("indexer-1", "Nebula", { category: "indexer" }), check("other", "Other")] });
    expect(rowOf(model, "check:download-client:c1")).toMatchObject({ tone: "bad", title: "qBittorrent", why: "qBittorrent message Fix it in Connections.", atUtc: "2026-10-01T08:00:00Z" });
    expect(model.rows.some((r) => r.id === "problem:download-client:c1")).toBe(false);
    expect(model.rows.some((r) => r.id === "check:indexer-1")).toBe(true);
    expect(model.rows.some((r) => r.id === "problem:other")).toBe(true);
    expect(model.rows.some((r) => r.id === "check:other")).toBe(false);
  });

  it("dates a connection's check from its last run when it has no start", () => {
    const attention = summaryOf([serverRow({ id: "client:c1", kind: "connection" })]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: [check("download-client:c1", "qBittorrent", { category: "download-client", problemSinceUtc: null })] });
    expect(rowOf(model, "check:download-client:c1").atUtc).toBe("2026-10-01T09:00:00Z");
  });

  it("counts every row that needs a person, in order: server items, problems, jobs, workers, connection checks", () => {
    const attention = summaryOf([
      serverRow({ id: "storage-low", kind: "storage" }),
      serverRow({ id: "failed-title-jobs", kind: "jobs" }),
      serverRow({ id: "client:c1", kind: "connection" })
    ]);
    const summary = emptySummary();
    summary.system = { ...summary.system!, lanes: [{ name: "Searching", state: "failing", activeJobs: 0, lastError: null }] } as never;
    const model = buildHealth({ summary, attention, checks: [check("a", "A"), check("download-client:c1", "qB", { category: "download-client" }), check("ok", "Fine", { status: "ok" })] });
    expect(model.rows.map((r) => r.id)).toEqual(["item:storage-low", "problem:a", "title-jobs-gave-up", "lane:Searching", "check:download-client:c1", "ok:ok"]);
    expect(model.need).toBe(5);
  });

  it("hands a problem to its buttons with the screen the summary names, or none for them to default", () => {
    const summary = emptySummary();
    summary.health = { ...summary.health!, topProblems: [{ id: "library:x", title: "X", category: "library", message: "m", sinceUtc: null, link: "/settings/libraries", snoozed: false } as never] };
    const model = buildHealth({ summary, attention: none, checks: [check("library:x", "X", { category: "library" }), check("library:y", "Y", { category: "library" })] });
    expect(rowOf(model, "problem:library:x").buttons).toEqual(healthProblemActions({ id: "library:x", name: "X", category: "library", link: "/settings/libraries" }).map(problemButton));
    expect(rowOf(model, "problem:library:y").buttons).toEqual(healthProblemActions({ id: "library:y", name: "Y", category: "library" }).map(problemButton));
  });

  it("has no top problems to read when the summary has no health at all", () => {
    const summary = emptySummary();
    summary.health = null;
    const model = buildHealth({ summary, attention: none, checks: [check("a", "A")] });
    expect(model.lastRunUtc).toBeNull();
    expect(model.rows).toHaveLength(1);
  });

  it("does not treat a row of another kind as a connection that already has its own row", () => {
    const attention = summaryOf([serverRow({ id: "other-check", kind: "storage" })]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: [check("other-check", "Other")] });
    expect(model.rows.some((r) => r.id === "problem:other-check")).toBe(true);
    expect(model.rows.some((r) => r.id === "check:other-check")).toBe(false);
  });

  it("uses only the health rows of the server when the checks are missing, not its other rows", () => {
    const attention = summaryOf([serverRow({ id: "media-row", kind: "stuck", section: "media", data: { id: "media-row" } }), serverRow({ id: "health:a", kind: "health:machine", data: { id: "a" } })]);
    const model = buildHealth({ summary: emptySummary(), attention, checks: undefined });
    expect(model.rows.filter((r) => r.id.startsWith("problem:")).map((r) => r.id)).toEqual(["problem:a"]);
  });

  it("names a worker with no name as a worker", () => {
    const summary = emptySummary();
    summary.system = { ...summary.system!, lanes: [{ name: "", state: "failing", activeJobs: 0, lastError: null }] } as never;
    expect(buildHealth({ summary, attention: none, checks: [] }).rows[0].title).toBe("A worker isn’t running");
  });

  it("does not list a passing check as snoozed just because it has a snooze time", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { status: "ok", snoozedUntilUtc: "2999-01-05T00:00:00Z" })] });
    expect(model.rows.map((r) => r.id)).toEqual(["ok:a"]);
  });

  it("tells the person what Unsnooze does, in words, and what to say when it works or fails", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a b", "A", { snoozedUntilUtc: "2999-01-05T00:00:00Z" })] });
    expect(rowOf(model, "snoozed:a b").buttons).toEqual([{
      label: "Unsnooze",
      note: "Watches this again; it shows here as a problem if it is still true.",
      post: { path: "/api/system/health/a%20b/unsnooze", failure: "That could not be unsnoozed.", done: "A is being watched again" }
    }]);
  });

  it("counts the passing checks, not the failing ones", () => {
    const model = buildHealth({ summary: emptySummary(), attention: none, checks: [check("a", "A", { status: "ok" }), check("b", "B", { status: "ok" }), check("c", "C")] });
    expect(model.pass).toBe(2);
    expect(model.total).toBe(3);
  });
});

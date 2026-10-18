import { describe, expect, it } from "vitest";
import { healthProblemActions } from "../../../lib/alert-actions";
import { serverRow, summaryOf } from "../../../lib/attention-summary-fixtures";
import { bellRows } from "../../../lib/bell-rows";
import { emptySummary } from "../summary-fixtures";
import { buildHealth } from "./problems";

const check = (id: string, name: string, over: Record<string, unknown> = {}) =>
  ({ id, category: "machine", name, status: "problem", message: `${name} message`, cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-10-01T09:00:00Z", problemSinceUtc: "2026-10-01T08:00:00Z", snoozedUntilUtc: null, ...over }) as never;

const MACHINE = [
  check("machine:sleep", "Computer sleep"),
  check("machine:drive:WD Red", "Drive health: WD Red"),
  check("machine:reboot", "Computer restart"),
  check("machine:temperature", "Processor temperature")
];

describe("System's Health and the bell offer the same buttons (#1098)", () => {
  it("every Machine problem's Health row has exactly the buttons the shared definition gives", () => {
    const model = buildHealth({ summary: emptySummary(), attention: summaryOf([]), checks: MACHINE });

    for (const item of MACHINE) {
      const row = model.rows.find((candidate) => candidate.id === `problem:${(item as { id: string }).id}`)!;
      const shared = healthProblemActions({ id: (item as { id: string }).id, category: "system", link: "/system" });
      expect(row.buttons.map((button) => button.label)).toEqual(shared.map((action) => action.label));
      expect(row.buttons.map((button) => button.note)).toEqual(shared.map((action) => action.hint));
      expect(row.buttons.length).toBeLessThanOrEqual(2);
    }
    // They are all in the Machine area.
    expect(model.rows.filter((row) => row.id.startsWith("problem:machine:")).every((row) => row.area === "machine")).toBe(true);
  });

  it("the bell shows the same labels for the same problem", () => {
    const summary = emptySummary();
    summary.health = { ...summary.health!, problems: 1, topProblems: [{ id: "machine:sleep", title: "Computer sleep", category: "machine", message: "m", sinceUtc: null, link: "/system", snoozed: false }] };
    const rows = bellRows(summaryOf([serverRow({ id: "health:machine:sleep", kind: "health:machine", title: "Computer sleep", data: { id: "machine:sleep", category: "machine", name: "Computer sleep", link: "/system" } })]));
    const model = buildHealth({ summary, attention: summaryOf([]), checks: [check("machine:sleep", "Computer sleep")] });

    const bell = rows.deluno.find((row) => row.key === "health:machine:sleep")!;
    const health = model.rows.find((row) => row.id === "problem:machine:sleep")!;
    expect(bell.actions.map((action) => action.label)).toEqual(health.buttons.map((button) => button.label));
  });

  it("Keep awake sends the settings change, not a navigation", () => {
    const model = buildHealth({ summary: emptySummary(), attention: summaryOf([]), checks: [check("machine:sleep", "Computer sleep")] });

    const keepAwake = model.rows.find((row) => row.id === "problem:machine:sleep")!.buttons[0]!;
    expect(keepAwake.to).toBeUndefined();
    expect(keepAwake.post).toMatchObject({ method: "PATCH", path: "/api/settings", body: { keepComputerAwake: true } });
  });

  it("a snoozed problem is listed quietly with the day its snooze ends and an Unsnooze that posts to its own address", () => {
    const model = buildHealth({
      summary: emptySummary(), attention: summaryOf([]),
      checks: [check("storage:disk", "A drive is nearly full", { category: "storage", snoozedUntilUtc: "2999-03-05T10:00:00Z" })]
    });

    const row = model.rows.find((candidate) => candidate.id === "snoozed:storage:disk")!;
    expect(row.tone).toBe("idle");
    expect(row.note).toBe(`Snoozed until ${new Date("2999-03-05T10:00:00Z").toLocaleDateString("en-AU", { day: "numeric", month: "short" })}`);
    expect(model.rows.some((candidate) => candidate.id === "problem:storage:disk")).toBe(false);
    expect(row.buttons.map((button) => button.label)).toEqual(["Unsnooze"]);
    expect(row.buttons[0]!.post?.path).toBe("/api/system/health/storage%3Adisk/unsnooze");
  });

  it("once the snooze has ended the same problem is a live problem again, with Snooze offered", () => {
    const model = buildHealth({
      summary: emptySummary(), attention: summaryOf([]),
      checks: [check("storage:disk", "A drive is nearly full", { category: "storage", snoozedUntilUtc: "2000-01-01T00:00:00Z" })]
    });

    expect(model.rows.some((candidate) => candidate.id === "snoozed:storage:disk")).toBe(false);
    const live = model.rows.find((candidate) => candidate.id === "problem:storage:disk")!;
    expect(live.buttons.map((button) => button.label)).toContain("Snooze 1 day");
  });
});

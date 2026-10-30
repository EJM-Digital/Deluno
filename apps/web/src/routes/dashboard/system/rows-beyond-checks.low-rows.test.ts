import { describe, expect, it } from "vitest";
import { serverRow, summaryOf } from "../../../lib/attention-summary-fixtures";
import { emptySummary } from "../summary-fixtures";
import { buildHealth } from "./problems";

const labels = (row: { buttons: { label: string }[] }) => row.buttons.map((button) => button.label);

describe("System's Health rows beyond the stored checks", () => {
  it("lists the server's outage, queue gate, collections and requests rows with their own Open button, jobs that gave up with Retry and Review, and a failing worker with Look", () => {
    const summary = emptySummary();
    summary.system = { ...summary.system!, lanes: [{ name: "Searching", state: "failing", activeJobs: 0, lastError: "It crashed." }, { name: "Importing", state: "running", activeJobs: 1, lastError: null }] };
    const attention = summaryOf([
      serverRow({ id: "outage", kind: "outage", tone: "bad", title: "The internet is down", to: "/system", action: "Open System" }),
      serverRow({ id: "queue-gate", kind: "queue-gate", title: "Downloading is paused", to: "/queue", action: "Open In progress" }),
      serverRow({ id: "collections", kind: "collections", title: "Collections need a look", to: "/collections", action: "Open Collections" }),
      serverRow({ id: "requests", kind: "requests", title: "Requests are failing", to: "/settings/lists", action: "Open Lists" }),
      serverRow({ id: "failed-title-jobs", kind: "jobs", tone: "bad", title: "3 title jobs gave up", text: "They tried and failed." }),
      serverRow({ id: "failed-jobs", kind: "jobs", tone: "bad", title: "2 background jobs gave up" })
    ]);
    const model = buildHealth({ summary, attention, checks: [] });
    const byId = Object.fromEntries(model.rows.map((row) => [row.id, row]));

    expect(labels(byId["item:outage"])).toEqual(["Open System"]);
    expect(byId["item:outage"].tone).toBe("bad");
    expect(labels(byId["item:queue-gate"])).toEqual(["Open In progress"]);
    expect(byId["item:queue-gate"].tone).toBe("warn");
    expect(labels(byId["item:collections"])).toEqual(["Open Collections"]);
    expect(labels(byId["item:requests"])).toEqual(["Open Lists"]);
    expect(labels(byId["title-jobs-gave-up"])).toEqual(["Retry", "Review"]);
    expect(labels(byId["jobs-gave-up"])).toEqual(["Retry", "Review"]);
    expect(byId["lane:Searching"].title).toBe("Searching isn’t running");
    expect(byId["lane:Searching"].why).toBe("It crashed.");
    expect(labels(byId["lane:Searching"])).toEqual(["Look"]);
    expect(byId["lane:Importing"]).toBeUndefined();
  });

  it("counts each of them once in how many need a person, and never says more are hidden", () => {
    const summary = emptySummary();
    summary.system = { ...summary.system!, lanes: [{ name: "Searching", state: "failing", activeJobs: 0, lastError: null }] };
    const attention = summaryOf([
      serverRow({ id: "outage", kind: "outage", tone: "bad" }),
      serverRow({ id: "collections", kind: "collections" }),
      serverRow({ id: "failed-jobs", kind: "jobs", tone: "bad" })
    ]);
    const model = buildHealth({ summary, attention, checks: [] });
    expect(model.need).toBe(4);
    expect(model.more).toBe(0);
    expect(model.rows.filter((row) => row.tone === "bad" || row.tone === "warn")).toHaveLength(4);
  });
});

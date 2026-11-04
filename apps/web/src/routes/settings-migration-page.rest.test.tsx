import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MigrationAuditReport, MigrationReport, MigrationReportOperation } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

// #1329: the Migration assistant's example, failures, sorting of every list, report download, and the drawer that explains one change.

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("react-router-dom", async (importOriginal) => ({ ...(await importOriginal<typeof import("react-router-dom")>()), useRevalidator: () => ({ revalidate: vi.fn() }) }));

const { SettingsMigrationPage } = await import("./settings-migration-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

const operation = (id: string, name: string, over: Partial<MigrationReportOperation> = {}): MigrationReportOperation => ({
  id, category: "indexer", targetType: "indexer", name, action: "create", canApply: true, reason: `${name} would be added.`, data: {}, warnings: [], ...over
});
const summary = { createCount: 3, skipCount: 0, conflictCount: 0, unsupportedCount: 0, warningCount: 0, titleCount: 0, monitoredCount: 0, wantedCount: 0 };
const report = (over: Partial<MigrationReport> = {}): MigrationReport => ({
  sourceKind: "radarr", sourceName: "Radarr", valid: true, summary, warnings: [], errors: [],
  operations: [operation("op-a", "Aa"), operation("op-b", "Bb", { action: "conflict", canApply: false, reason: "Zeta clash." }), operation("op-c", "Cc", { action: "unsupported", canApply: false, reason: "Alpha not supported." })],
  inventory: null, ...over
} as MigrationReport);
const entry = (category: string, rows: number, actions: Record<string, number>, classes: Record<string, number>, warnings: string[] = []) => ({
  sourceKind: "radarr", mediaType: "movies", category, inputRowCount: rows, accountedRowCount: rows, unaccountedRowCount: 0, actionCounts: actions, classificationCounts: classes, warnings, complete: true
});

let reportsReply: MigrationAuditReport[] = [];
function route(handlers: Record<string, (body: unknown) => Response | Promise<Response>>) {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const body = init?.body ? JSON.parse(String(init.body)) : null;
    if (url === "/api/migration/reports") return json(reportsReply);
    for (const [prefix, handler] of Object.entries(handlers)) if (url === prefix || url.startsWith(prefix)) return handler(body);
    return json({});
  });
}
const renderPage = () => render(<MemoryRouter><SettingsMigrationPage /></MemoryRouter>);
async function preview(over: Partial<MigrationReport> = {}) {
  route({ "/api/migration/preview": () => json(report(over)) });
  renderPage();
  fireEvent.change(screen.getByPlaceholderText(/qualityProfiles/), { target: { value: "{\"qualityProfiles\":[]}" } });
  fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
  await screen.findByText(/change report/i);
}
const press = (label: string) => fireEvent.click(within(screen.getByRole("columnheader", { name: new RegExp(`^${label}`) })).getByRole("button"));
const rowNames = (pattern: RegExp) => screen.getAllByRole("row").map((row) => pattern.exec(row.textContent ?? "")?.[0]).filter(Boolean);

beforeEach(() => { reportsReply = []; vi.mocked(authedFetch).mockReset(); });
afterEach(() => { cleanup(); window.localStorage.clear(); vi.unstubAllGlobals(); });

describe("the source box", () => {
  it("Load example fills the box with the example export, which makes Preview import available", () => {
    route({});
    renderPage();
    const box = screen.getByPlaceholderText(/qualityProfiles/) as HTMLTextAreaElement;
    expect(box.value).toBe("");
    expect((screen.getByRole("button", { name: /Preview import/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "Load example" }));
    expect(box.value).toBe(box.placeholder);
    expect((screen.getByRole("button", { name: /Preview import/ }) as HTMLButtonElement).disabled).toBe(false);
  });
});

describe("a preview or apply that fails", () => {
  it("says why on the Selected cell when a report is already on screen, and falls back to its own words for something that is not a message", async () => {
    await preview();
    route({ "/api/migration/preview": () => json({ message: "That is not valid JSON." }, 400) });
    fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
    expect(await screen.findByText("That is not valid JSON.")).toBeTruthy();
    vi.mocked(authedFetch).mockRejectedValue("offline");
    fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
    expect(await screen.findByText("Migration preview failed.")).toBeTruthy();
  });

  it("says why under the toolbar when the very first preview fails and there is no report yet (#1478)", async () => {
    route({ "/api/migration/preview": () => json({ message: "That is not valid JSON." }, 400) });
    renderPage();
    fireEvent.change(screen.getByPlaceholderText(/qualityProfiles/), { target: { value: "{" } });
    fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
    expect((await screen.findByRole("alert")).textContent).toContain("That is not valid JSON.");
  });

  it("an apply that is rejected without a message says the apply failed", async () => {
    await preview();
    const calls = vi.mocked(authedFetch).getMockImplementation()!;
    vi.mocked(authedFetch).mockImplementation(async (input, init) => {
      if (String(input) === "/api/migration/apply") throw "offline";
      return calls(input, init);
    });
    fireEvent.click(screen.getByRole("button", { name: /Apply 1 selected/ }));
    expect(await screen.findByText("Migration apply failed.")).toBeTruthy();
  });

  it("a test of an imported connection that is rejected without a message says it could not run", async () => {
    await preview();
    route({
      "/api/migration/apply": () => json({ report: report(), applied: [{ operationId: "op-a", targetType: "indexer", name: "Aa", createdId: "i1", result: "created" }], auditReportId: "a1" }),
      "/api/indexers/i1/test": () => { throw "offline"; }
    });
    fireEvent.click(screen.getByRole("button", { name: /Apply 1 selected/ }));
    fireEvent.click(await screen.findByRole("button", { name: /Test all/ }));
    expect(await screen.findByText("The test could not run.")).toBeTruthy();
    expect(screen.getByText("needs review")).toBeTruthy();
  });
});

describe("the summary strip", () => {
  it("says a preview found issues when the report is not valid, and marks nothing to create or unsupported only when there is some", async () => {
    route({ "/api/migration/preview": () => json(report({ valid: false, summary: { ...summary, createCount: 0, unsupportedCount: 2 }, operations: [operation("op-s", "Skipped", { action: "skip", canApply: false })] })) });
    renderPage();
    fireEvent.change(screen.getByPlaceholderText(/qualityProfiles/), { target: { value: "{}" } });
    fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
    expect(await screen.findByText("Preview found issues — see the change report.")).toBeTruthy();
    expect(screen.getByText("skip")).toBeTruthy();
    expect((screen.getByRole("button", { name: /Apply 0 selected/ }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("the inventory and the lists", () => {
  it("sorts the inventory by Source, Rows, Actions and Classification, and shows the row's warnings", async () => {
    await preview({
      inventory: {
        inputRowCount: 9, accountedRowCount: 9, unaccountedRowCount: 0,
        entries: [entry("indexers", 5, { create: 2 }, { supported: 5 }, ["Check the keys."]), entry("clients", 3, {}, {}), entry("profiles", 1, { skip: 1 }, { opaque: 1 })]
      }
    } as never);
    expect(screen.getByText("Check the keys.")).toBeTruthy();
    expect(screen.getByText("No mapped actions")).toBeTruthy();
    expect(screen.getByText("No special classification")).toBeTruthy();
    const names = () => rowNames(/indexers|clients|profiles/);
    const order: Record<string, string[]> = { Source: ["clients", "indexers", "profiles"], Rows: ["profiles", "clients", "indexers"], Actions: ["indexers", "clients", "profiles"], Classification: ["clients", "profiles", "indexers"] };
    for (const [label, expected] of Object.entries(order)) {
      press(label);
      expect(names()).toEqual(expected);
      press(label);
      expect(names()).toEqual([...expected].reverse());
    }
  });

  it("sorts the changes by Item, Why and Action, and a change with warnings counts them", async () => {
    await preview({ operations: [operation("op-a", "Aa", { warnings: ["One.", "Two."] }), operation("op-b", "Bb", { action: "conflict", canApply: false, reason: "Zeta clash." }), operation("op-c", "Cc", { action: "unsupported", canApply: false, reason: "Alpha not supported." })] });
    expect(screen.getByText("2 warnings")).toBeTruthy();
    const names = () => rowNames(/Aa|Bb|Cc/);
    press("Item");
    expect(names()).toEqual(["Aa", "Bb", "Cc"]);
    press("Item");
    expect(names()).toEqual(["Cc", "Bb", "Aa"]);
    press("Why");
    expect(names()).toEqual(["Aa", "Cc", "Bb"]);
    press("Action");
    expect(names()[2]).toBe("Aa");
    press("Action");
    expect(names()[0]).toBe("Aa");
  });

  it("an empty report says nothing was recognised", async () => {
    await preview({ operations: [] });
    expect(screen.getByText("Nothing to import")).toBeTruthy();
  });

  it("sorts the imported connections by Connection, Result and Status", async () => {
    await preview();
    route({
      "/api/migration/apply": () => json({ report: report(), applied: [
        { operationId: "op-a", targetType: "indexer", name: "Xa", createdId: "i1", result: "created" },
        { operationId: "op-b", targetType: "download-client", name: "Xb", createdId: "c1", result: "created" },
        { operationId: "op-c", targetType: "indexer", name: "Xc", createdId: "i2", result: "created" }
      ], auditReportId: "a1" }),
      "/api/indexers/i1/test": () => json({ healthStatus: "healthy", message: "Zeta answered.", latencyMs: null }),
      "/api/indexers/i2/test": () => json({ message: "The indexer fell over." }, 500),
      "/api/download-clients/c1/test": () => json({ healthStatus: "unreachable", message: "Nothing answered." })
    });
    fireEvent.click(screen.getByRole("button", { name: /Apply 1 selected/ }));
    const testAll = await screen.findByRole("button", { name: /Test all/ });
    press("Status");
    expect(screen.getAllByText("Untested")).toHaveLength(3);
    fireEvent.click(testAll);
    await screen.findByText("Zeta answered.");
    const names = () => rowNames(/Xa|Xb|Xc/);
    const order: Record<string, string[]> = { Connection: ["Xa", "Xb", "Xc"], Result: ["Xb", "Xc", "Xa"] };
    for (const [label, expected] of Object.entries(order)) {
      press(label);
      expect(names()).toEqual(expected);
      press(label);
      expect(names()).toEqual([...expected].reverse());
    }
    press("Status");
    const byStatus = names();
    press("Status");
    expect(names()).toEqual([...byStatus].reverse());
    expect(screen.getByText("The indexer fell over.")).toBeTruthy();
    expect(screen.getByText("needs review")).toBeTruthy();
  });

  it("sorts the history by Import, Outcome and When", async () => {
    const record = (id: string, name: string, created: number, applied: string) => ({
      id, sourceKind: "radarr", sourceName: name, appliedUtc: applied, preflightReport: report({ sourceName: name }), resultReport: report({ sourceName: name }),
      applied: Array.from({ length: created }, (_, index) => ({ operationId: `${id}-${index}`, targetType: "indexer", name: "x", createdId: "i", result: "created" }))
    }) as unknown as MigrationAuditReport;
    reportsReply = [record("a", "Aa", 3, "2026-10-01T10:00:00Z"), record("b", "Bb", 1, "2026-10-03T10:00:00Z"), record("c", "Cc", 2, "2026-10-02T10:00:00Z")];
    route({});
    renderPage();
    await screen.findByText("Aa");
    const names = () => rowNames(/Aa|Bb|Cc/);
    const order: Record<string, string[]> = { Import: ["Aa", "Bb", "Cc"], Outcome: ["Bb", "Cc", "Aa"], When: ["Aa", "Cc", "Bb"] };
    for (const [label, expected] of Object.entries(order)) {
      press(label);
      expect(names()).toEqual(expected);
      press(label);
      expect(names()).toEqual([...expected].reverse());
    }
  });
});

describe("downloading the report", () => {
  it("saves the report as a file named after the import", async () => {
    await preview({ sourceName: "Home Radarr!" });
    const createObjectURL = vi.fn(() => "blob:report");
    const revokeObjectURL = vi.fn();
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL, revokeObjectURL }));
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { downloads.push(this.download); });
    const downloads: string[] = [];
    fireEvent.click(screen.getByRole("button", { name: /Download report/ }));
    expect(downloads).toEqual(["deluno-migration-home-radarr.json"]);
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:report");
    click.mockRestore();
  });

  it("names a file after a name made of nothing but symbols simply import", async () => {
    await preview({ sourceName: "!!!" });
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: () => "blob:x", revokeObjectURL: () => undefined }));
    const downloads: string[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { downloads.push(this.download); });
    fireEvent.click(screen.getByRole("button", { name: /Download report/ }));
    expect(downloads).toEqual(["deluno-migration-import.json"]);
    click.mockRestore();
  });
});

describe("the drawer for one change", () => {
  it("shows why, what would be created without the empty fields, and its warnings, and its switch changes what is applied", async () => {
    await preview({ operations: [operation("op-a", "Aa", { data: { url: "https://a.example", key: null, empty: "" }, warnings: ["Check this."] })] });
    fireEvent.click(screen.getByText("Aa"));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("Aa would be added.")).toBeTruthy();
    expect(drawer.getByText("https://a.example")).toBeTruthy();
    expect(drawer.queryByText("key")).toBeNull();
    expect(drawer.queryByText("empty")).toBeNull();
    expect(drawer.getByText("Check this.")).toBeTruthy();
    fireEvent.click(drawer.getByRole("switch", { name: "Apply Aa" }));
    fireEvent.click(drawer.getAllByRole("button", { name: "Close" }).at(-1)!);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.getByRole("button", { name: /Apply 0 selected/ })).toBeTruthy();
  });

  it("a change with no data and no warnings shows neither section, and a review-only one says Deluno will not write it", async () => {
    await preview();
    fireEvent.click(screen.getByText("Bb"));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("This one is review-only — Deluno will not write it.")).toBeTruthy();
    expect(drawer.queryByText("What would be created")).toBeNull();
    expect(drawer.queryByText("Warnings")).toBeNull();
    fireEvent.keyDown(drawer.getAllByRole("button", { name: "Close" })[0], { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("has one way out that works: every Close is enabled, there is no dead Cancel beside it, and pressing it closes the drawer (#1478)", async () => {
    await preview();
    fireEvent.click(screen.getByText("Bb"));
    const drawer = within(await screen.findByRole("dialog"));
    const closes = drawer.getAllByRole("button", { name: "Close" });
    for (const button of closes) expect((button as HTMLButtonElement).disabled).toBe(false);
    expect(drawer.queryByRole("button", { name: "Cancel" })).toBeNull();
    fireEvent.click(closes[closes.length - 1]);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("a monitored-state row of the catalog can be applied though it creates nothing, and a report-only row is shown as information", async () => {
    await preview({ operations: [operation("op-m", "Monitored", { category: "catalog", targetType: "monitored-state", canApply: false }), operation("op-r", "Noted", { action: "report", canApply: false })] });
    expect(screen.getByRole("switch", { name: "Apply Monitored" })).toBeTruthy();
    expect(screen.queryByRole("switch", { name: "Apply Noted" })).toBeNull();
    expect(screen.getByText("report")).toBeTruthy();
  });
});

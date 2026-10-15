import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MigrationAuditReport, MigrationReport } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Migration assistant: the source kinds, the preview, the inventory chip, Apply N selected with its message,
 * the imported connections' Test all, and the applied history that reopens a past record.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("react-router-dom", async (importOriginal) => ({ ...(await importOriginal<typeof import("react-router-dom")>()), useRevalidator: () => ({ revalidate: vi.fn() }) }));

const { SettingsMigrationPage } = await import("./settings-migration-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

const operation = (id: string, name: string, targetType: string, action = "create") => ({ id, category: targetType, targetType, name, action, canApply: action === "create", reason: `${name} would be added.`, data: {}, warnings: [] });
const summary = { createCount: 3, skipCount: 0, conflictCount: 0, unsupportedCount: 0, warningCount: 0, titleCount: 0, monitoredCount: 0, wantedCount: 0 };
const report = (over: Partial<MigrationReport> = {}): MigrationReport => ({
  sourceKind: "radarr", sourceName: "Radarr", valid: true, summary, warnings: [], errors: [],
  operations: [operation("op-profile", "Migrated UHD", "quality-profile"), operation("op-indexer", "Migrated Torrent", "indexer"), operation("op-client", "Migrated qBittorrent", "download-client")],
  inventory: null, ...over
} as MigrationReport);
const inventory = (unaccounted: number) => ({
  inputRowCount: 4, accountedRowCount: 4 - unaccounted, unaccountedRowCount: unaccounted,
  entries: [{ sourceKind: "radarr", mediaType: "movies", category: "indexers", inputRowCount: 4, accountedRowCount: 4 - unaccounted, unaccountedRowCount: unaccounted, actionCounts: { create: 2, skip: 1 }, classificationCounts: { supported: 3 }, warnings: [], complete: unaccounted === 0 }]
});

let reportsReply: MigrationAuditReport[] = [];
function route(handlers: Record<string, (body: unknown) => Response>) {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const body = init?.body ? JSON.parse(String(init.body)) : null;
    if (url === "/api/migration/reports") return json(reportsReply);
    for (const [prefix, handler] of Object.entries(handlers)) if (url === prefix || url.startsWith(prefix)) return handler(body);
    return json({});
  });
}
const renderPage = () => render(<MemoryRouter><SettingsMigrationPage /></MemoryRouter>);
const callsTo = (url: string) => vi.mocked(authedFetch).mock.calls.filter(([u]) => u === url);

async function preview(over: Partial<MigrationReport> = {}) {
  route({ "/api/migration/preview": () => json(report(over)) });
  renderPage();
  fireEvent.change(screen.getByPlaceholderText(/qualityProfiles/), { target: { value: "{\"qualityProfiles\":[]}" } });
  fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
  await screen.findByText(/change report/i);
}

beforeEach(() => {
  cleanup();
  reportsReply = [];
  vi.mocked(authedFetch).mockReset();
});

describe("the source", () => {
  it("offers Radarr, Sonarr, Prowlarr, Recyclarr and compatible JSON, each renaming the import after itself", () => {
    route({});
    renderPage();
    const select = screen.getByLabelText("Exported from") as HTMLSelectElement;
    expect([...select.options].map((option) => option.textContent)).toEqual(["Radarr", "Sonarr", "Prowlarr", "Recyclarr", "Compatible JSON"]);
    fireEvent.change(select, { target: { value: "prowlarr" } });
    expect(screen.getByLabelText("Call this import")).toHaveValue("Prowlarr");
  });

  it("sends the source kind, the chosen name, the JSON and the legacy-rules choice with a preview", async () => {
    route({ "/api/migration/preview": () => json(report({ sourceKind: "prowlarr", sourceName: "Home Prowlarr" })) });
    renderPage();
    fireEvent.change(screen.getByLabelText("Exported from"), { target: { value: "prowlarr" } });
    fireEvent.change(screen.getByLabelText("Call this import"), { target: { value: "Home Prowlarr" } });
    fireEvent.change(screen.getByPlaceholderText(/qualityProfiles/), { target: { value: "{\"indexers\":[]}" } });
    fireEvent.click(screen.getByRole("switch", { name: "Keep opaque rules as Advanced legacy input" }));
    fireEvent.click(screen.getByRole("button", { name: /Preview import/ }));
    await screen.findByText(/change report/i);
    expect(JSON.parse(String(vi.mocked(authedFetch).mock.calls.find(([u]) => u === "/api/migration/preview")![1]!.body))).toEqual({
      sourceKind: "prowlarr", sourceName: "Home Prowlarr", payloadJson: "{\"indexers\":[]}", allowAdvancedLegacyRules: true
    });
  });
});

describe("the import inventory", () => {
  it("says Complete when every row is accounted for and how many need review otherwise, with each category's actions", async () => {
    await preview({ inventory: inventory(0) });
    expect(screen.getByText("Complete")).toBeInTheDocument();
    expect(screen.getByText("4 of 4 legacy rows accounted for")).toBeInTheDocument();
    expect(screen.getByText(/create 2/i)).toBeInTheDocument();
    cleanup();
    await preview({ inventory: inventory(2) });
    expect(screen.getByText("2 need review")).toBeInTheDocument();
  });
});

describe("Apply N selected", () => {
  it("selects the applicable operations, posts only the ticked ones, and says how many were imported", async () => {
    await preview();
    expect(screen.getByRole("button", { name: /Apply 3 selected/ })).toBeEnabled();
    fireEvent.click(screen.getByRole("switch", { name: "Apply Migrated qBittorrent" }));
    route({
      "/api/migration/apply": () => json({ report: report(), applied: [{ operationId: "op-profile", targetType: "quality-profile", name: "Migrated UHD", createdId: "q1", result: "created" }, { operationId: "op-indexer", targetType: "indexer", name: "Migrated Torrent", createdId: "i1", result: "created" }], auditReportId: "a1" }),
      "/api/migration/preview": () => json(report())
    });
    fireEvent.click(screen.getByRole("button", { name: /Apply 2 selected/ }));
    await waitFor(() => expect(screen.getByText(/2 selected items imported\. Anything already present was skipped\./)).toBeInTheDocument());
    const body = JSON.parse(String(callsTo("/api/migration/apply")[0][1]!.body));
    expect(body.selectedOperationIds.sort()).toEqual(["op-indexer", "op-profile"]);
  });

  it("says the migration paused safely when the report has errors and keeps the report on screen", async () => {
    await preview();
    route({ "/api/migration/apply": () => json({ report: report({ errors: ["The indexer could not be saved."] }), applied: [], auditReportId: null }) });
    fireEvent.click(screen.getByRole("button", { name: /Apply 3 selected/ }));
    await waitFor(() => expect(screen.getByText(/Migration paused safely\. Review the report, then retry — anything already saved is skipped\./)).toBeInTheDocument());
  });

  it("is dimmed when nothing is ticked and shows the server's message when the apply is refused", async () => {
    await preview();
    for (const name of ["Migrated UHD", "Migrated Torrent", "Migrated qBittorrent"]) fireEvent.click(screen.getByRole("switch", { name: `Apply ${name}` }));
    expect(screen.getByRole("button", { name: /Apply 0 selected/ })).toBeDisabled();
    fireEvent.click(screen.getByRole("switch", { name: "Apply Migrated UHD" }));
    route({ "/api/migration/apply": () => json({ message: "Deluno could not back up first." }, 400) });
    fireEvent.click(screen.getByRole("button", { name: /Apply 1 selected/ }));
    await waitFor(() => expect(screen.queryByText(/imported\./)).toBeNull());
    expect(screen.getByRole("button", { name: /Apply 1 selected/ })).toBeEnabled();
  });
});

describe("imported connections", () => {
  async function appliedWithConnections() {
    await preview();
    route({
      "/api/migration/apply": () => json({ report: report(), applied: [
        { operationId: "op-indexer", targetType: "indexer", name: "Migrated Torrent", createdId: "i1", result: "created" },
        { operationId: "op-client", targetType: "download-client", name: "Migrated qBittorrent", createdId: "c1", result: "created" }
      ], auditReportId: "a1" }),
      "/api/indexers/i1/test": () => json({ healthStatus: "healthy", message: "Reached the indexer.", latencyMs: 12 }),
      "/api/download-clients/c1/test": () => json({ healthStatus: "unreachable", message: "Nothing answered.", latencyMs: null })
    });
    fireEvent.click(screen.getByRole("button", { name: /Apply 3 selected/ }));
    await screen.findByText(/imported connections/i);
  }

  it("lists each created indexer and client as not tested yet, then Test all posts each one's own test and shows its result", async () => {
    await appliedWithConnections();
    expect(screen.getAllByText("Not tested yet")).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: "Test all" }));
    await screen.findByText("Reached the indexer.");
    expect(screen.getByText("12 ms")).toBeInTheDocument();
    expect(screen.getByText("Nothing answered.")).toBeInTheDocument();
    expect(callsTo("/api/indexers/i1/test")).toHaveLength(1);
    expect(callsTo("/api/download-clients/c1/test")).toHaveLength(1);
  });
});

describe("applied history", () => {
  it("lists past real imports with their source, created and failed counts, and a click reopens the preflight report", async () => {
    reportsReply = [{
      id: "a1", sourceKind: "radarr", sourceName: "Old Radarr", appliedUtc: "2026-10-01T10:00:00Z",
      preflightReport: report({ sourceName: "Old Radarr" }), resultReport: report({ sourceName: "Old Radarr" }),
      applied: [
        { operationId: "op-indexer", targetType: "indexer", name: "Migrated Torrent", createdId: "i1", result: "created" },
        { operationId: "op-client", targetType: "download-client", name: "Migrated qBittorrent", createdId: "", result: "failed" }
      ]
    } as MigrationAuditReport];
    route({});
    renderPage();
    const row = (await screen.findByText("Old Radarr")).closest("[role='row']") as HTMLElement;
    expect(within(row).getByText("1 created")).toBeInTheDocument();
    expect(within(row).getByText("1 failed")).toBeInTheDocument();
    fireEvent.click(row);
    expect(await screen.findByText(/change report/i)).toBeInTheDocument();
    expect(screen.getByText(/Showing the record from /)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apply 0 selected/ })).toBeDisabled();
  });

  it("shows no history card on a fresh install", async () => {
    route({});
    renderPage();
    await waitFor(() => expect(callsTo("/api/migration/reports")).toHaveLength(1));
    expect(screen.queryByText(/Applied history/i)).toBeNull();
  });
});

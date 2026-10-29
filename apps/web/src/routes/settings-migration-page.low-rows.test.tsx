import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MigrationAuditReport } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/** Settings > Migration assistant: the applied history when the list of past records cannot be loaded. */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("react-router-dom", async (importOriginal) => ({ ...(await importOriginal<typeof import("react-router-dom")>()), useRevalidator: () => ({ revalidate: vi.fn() }) }));

const { SettingsMigrationPage } = await import("./settings-migration-page");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const stub = { sourceKind: "radarr", sourceName: "Radarr", valid: true, summary: {}, warnings: [], errors: [], operations: [], inventory: null };
const audit = { id: "a1", sourceKind: "radarr", sourceName: "Radarr", appliedUtc: "2026-10-01T00:00:00Z", preflightReport: stub, resultReport: stub, applied: [{ operationId: "o", targetType: "indexer", name: "X", createdId: "i", result: "created" }] } as unknown as MigrationAuditReport;

beforeEach(() => {
  cleanup();
  vi.mocked(authedFetch).mockReset();
});

describe("the applied history", () => {
  it("is simply absent when the list of past records fails to load, and the rest of the page still draws", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      if (String(input) === "/api/migration/reports") throw new Error("The server is not answering.");
      return json({});
    });
    render(<MemoryRouter><SettingsMigrationPage /></MemoryRouter>);
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([url]) => url === "/api/migration/reports")).toBe(true));
    expect(screen.getByRole("button", { name: /Preview import/ })).toBeInTheDocument();
    expect(screen.queryByText(/Applied history/i)).toBeNull();
    expect(screen.queryByText(/not answering/)).toBeNull();
  });

  it("is simply absent when the server refuses the list", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/migration/reports" ? json({ message: "No." }, 500) : json({}));
    render(<MemoryRouter><SettingsMigrationPage /></MemoryRouter>);
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([url]) => url === "/api/migration/reports")).toBe(true));
    expect(screen.queryByText(/Applied history/i)).toBeNull();
  });

  it("lists the past records under Applied history when the list loads", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input) === "/api/migration/reports" ? json([audit]) : json({}));
    render(<MemoryRouter><SettingsMigrationPage /></MemoryRouter>);
    expect(await screen.findByText(/Applied history/i)).toBeInTheDocument();
  });
});

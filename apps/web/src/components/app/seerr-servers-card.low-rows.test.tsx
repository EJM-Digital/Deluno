import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";

// #1329: the Request servers list at its edges,.

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));
vi.mock("./connection-update-panel", () => ({ ConnectionUpdatePanel: () => null }));
vi.mock("./connection-version", () => ({ ConnectionVersionBeside: () => null }));
vi.mock("./tag-picker", () => ({ TagPicker: ({ label }: { label?: string }) => <input aria-label={label} /> }));

const { SeerrServersCard } = await import("./seerr-servers-card");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const server = (over: Record<string, unknown> = {}) => ({
  id: "z1", name: "Seerr on den", baseUrl: "http://den.example.test:5055", hasApiKey: true, isEnabled: true, healthStatus: "healthy", lastHealthMessage: "Reached it.",
  lastHealthLatencyMs: 20, lastHealthTestUtc: new Date().toISOString(), serverName: "Den", version: "2.0.0", movieLibraryId: null, movieQualityProfileId: null,
  seriesLibraryId: null, seriesQualityProfileId: null, hasWebhookSecret: false, tags: "", tags4k: "", ...over
});
const renderCard = (servers: unknown[] = []) => render(
  <MemoryRouter><SeerrServersCard openId={null} servers={servers as never} cleanups={[]} libraries={[]} qualityProfiles={[]} onChanged={vi.fn()} /></MemoryRouter>
);

beforeEach(() => {
  cleanup();
  vi.mocked(authedFetch).mockReset();
});

describe("the Request servers list, low rows (#1329)", () => {
  it("offers Add Seerr and an empty state with no server, then counts the working servers over all of them with the list's columns", () => {
    vi.mocked(authedFetch).mockResolvedValue(json([]));
    renderCard();
    expect(screen.getByRole("button", { name: "Add Seerr" })).toBeTruthy();
    expect(screen.getByText("No Seerr server")).toBeTruthy();
    expect(screen.queryByText(/of .* working/)).toBeNull();
    cleanup();

    renderCard([server(), server({ id: "z2", name: "Seerr on lounge", healthStatus: "failed" }), server({ id: "z3", name: "Seerr off", isEnabled: false })]);
    expect(screen.getByText("1 of 3 working")).toBeTruthy();
    for (const column of ["Server", "Address", "Reports itself as", "Last test", "Status", "On"]) {
      expect(screen.getByRole("columnheader", { name: new RegExp(`^${column}`) }), column).toBeTruthy();
    }
    expect(screen.getByRole("switch", { name: "Pause Seerr on den" }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("switch", { name: "Enable Seerr off" }).getAttribute("aria-checked")).toBe("false");
  });
});

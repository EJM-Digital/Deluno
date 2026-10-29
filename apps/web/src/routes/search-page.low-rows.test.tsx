import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("react-router-dom", async (importOriginal) => ({ ...(await importOriginal<typeof import("react-router-dom")>()) }));

const base = { indexerId: "idx-1", indexerName: "Fixture", quality: "WEB 1080p", meetsCutoff: true, decisionStatus: "eligible", sizeBytes: 1_000_000_000, leechers: 0, downloadUrl: "https://fixture.invalid/x" };
const results = {
  candidates: [
    { ...base, releaseName: "Dead.Torrent.2026.1080p", seeders: 0, indexerProtocol: "torznab" },
    { ...base, releaseName: "Quiet.Usenet.2026.1080p", seeders: 0, indexerProtocol: "usenet" }
  ],
  indexersQueried: 1
};

function mockFetch() {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/search/categories")) return new Response(JSON.stringify([]), { status: 200 });
    if (url.includes("/api/search")) return new Response(JSON.stringify(results), { status: 200 });
    if (url.includes("/api/indexers")) return new Response(JSON.stringify([{ id: "idx-1", name: "Fixture", protocol: "torznab", privacy: "public", baseUrl: "https://fixture.invalid", priority: 1, categories: "2000", tags: "", isEnabled: true, healthStatus: "healthy", mediaScope: "movies" }]), { status: 200 });
    return new Response(JSON.stringify({}), { status: 200 });
  });
}

const { SearchPage } = await import("./search-page");

describe("Search results: the no-seeders flag", () => {
  afterEach(() => vi.clearAllMocks());

  it("a torrent with 0 seeders says no one is seeding it in the row and in its drawer, and a Usenet release with 0 seeders does not", async () => {
    mockFetch();
    render(<MemoryRouter><SearchPage /></MemoryRouter>);
    fireEvent.change(screen.getByPlaceholderText("Search for a release…"), { target: { value: "2026 1080p" } });
    fireEvent.click(screen.getByRole("button", { name: /^Search$/ }));
    await waitFor(() => expect(screen.getByText("Dead.Torrent.2026.1080p")).toBeTruthy());

    const torrentRow = screen.getByText("Dead.Torrent.2026.1080p").closest("[role='row'], tr, li, div[class*='row']") as HTMLElement;
    expect(within(torrentRow).getByText(/No one is seeding this release\./)).toBeTruthy();
    expect(screen.getAllByText(/No one is seeding this release\./)).toHaveLength(1);

    fireEvent.click(screen.getByText("Dead.Torrent.2026.1080p"));
    await waitFor(() => expect(screen.getByRole("button", { name: "Send to download client" })).toBeTruthy());
    expect(screen.getAllByText(/No one is seeding this release\./).length).toBeGreaterThan(1);
  });
});

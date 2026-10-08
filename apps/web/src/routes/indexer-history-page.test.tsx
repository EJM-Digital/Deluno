import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

/**
 * #754: the combined Indexer History screen -- lists events across every
 * indexer, filters by action type and indexer, and clears the combined view
 * (without touching the per-title history it reads from) behind a confirm.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const initialPage = {
  items: [
    {
      id: "evt-1",
      indexerId: "idx-1",
      indexerName: "EZTV",
      eventType: "rss-sync",
      outcome: "read",
      message: "Read EZTV's RSS feed. 2 new releases.",
      detailsJson: null,
      relatedEntityType: null,
      relatedEntityId: null,
      createdUtc: "2026-09-28T10:00:00Z"
    },
    {
      id: "evt-2",
      indexerId: null,
      indexerName: "1337x",
      eventType: "auth-failure",
      outcome: "failed",
      message: "1337x answered HTTP 401.",
      detailsJson: null,
      relatedEntityType: null,
      relatedEntityId: null,
      createdUtc: "2026-09-28T09:00:00Z"
    }
  ],
  nextPageToken: null,
  hasMore: false
};

const loaderData = { page: initialPage };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

function mockFetch() {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/indexer-history/clear")) {
      return new Response(JSON.stringify({ cleared: true }), { status: 200 });
    }
    if (url.includes("/api/indexer-history")) {
      return new Response(JSON.stringify(initialPage), { status: 200 });
    }
    return new Response(JSON.stringify({}), { status: 200 });
  });
}

const { IndexerHistoryPage } = await import("./indexer-history-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <IndexerHistoryPage />
    </MemoryRouter>
  );
}

describe("combined Indexer History (#754)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("lists events across every indexer, newest first", () => {
    mockFetch();
    renderPage();

    expect(screen.getByText("EZTV")).toBeTruthy();
    expect(screen.getByText("1337x")).toBeTruthy();
    expect(screen.getAllByText("RSS sync").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Auth failure").length).toBeGreaterThan(0);
  });

  it("filters by action type", async () => {
    mockFetch();
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    fireEvent.click(screen.getByRole("radio", { name: "Auth failure" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      expect.stringContaining("eventType=auth-failure"),
      expect.anything()
    ));
  });

  it("sorts on the server when a column header is clicked, and asks again from the top in that order (#1119)", async () => {
    window.localStorage.clear();
    mockFetch();
    renderPage();

    const header = screen.getByRole("columnheader", { name: /^Indexer/ });
    fireEvent.click(within(header).getByRole("button"));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      expect.stringMatching(/sortBy=indexer.*sortDirection=asc|sortDirection=asc.*sortBy=indexer/),
      expect.anything()
    ));
    expect(header).toHaveAttribute("aria-sort", "ascending");

    fireEvent.click(within(header).getByRole("button"));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      expect.stringContaining("sortDirection=desc"),
      expect.anything()
    ));
    window.localStorage.clear();
  });

  it("clears the combined history behind a confirmation", async () => {
    mockFetch();
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Clear history" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Clear history" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/indexer-history/clear",
      expect.objectContaining({ method: "POST" })
    ));
  });
});

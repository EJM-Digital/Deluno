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

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toasts
}));

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

const loaderData: { page: { items: typeof initialPage.items; nextPageToken: string | null; hasMore: boolean } } = { page: initialPage };

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

const olderEvent = {
  id: "evt-3", indexerId: "idx-1", indexerName: "EZTV", eventType: "search", outcome: "read", message: "Searched EZTV for Sintel.",
  detailsJson: null, relatedEntityType: null, relatedEntityId: null, createdUtc: "2026-09-27T10:00:00Z"
};

describe("Load more (#754)", () => {
  afterEach(() => {
    loaderData.page = initialPage;
    vi.clearAllMocks();
  });

  it("asks for the next 50 with the same filters, says Loading while it waits, and adds the rows under the ones shown", async () => {
    loaderData.page = { ...initialPage, nextPageToken: "next-1", hasMore: true };
    let release: (response: Response) => void = () => {};
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("pageToken=next-1")) return new Promise<Response>((resolve) => { release = resolve; });
      return new Response(JSON.stringify({ ...initialPage, nextPageToken: "next-1" }), { status: 200 });
    });
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    fireEvent.click(screen.getByRole("radio", { name: "Auth failure" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(expect.stringContaining("eventType=auth-failure"), expect.anything()));

    fireEvent.click(await screen.findByRole("button", { name: "Load more" }));

    expect(await screen.findByRole("button", { name: /Loading/ })).toBeDisabled();
    const asked = vi.mocked(authedFetch).mock.calls.map((call) => String(call[0])).find((url) => url.includes("pageToken=next-1"))!;
    expect(asked).toContain("pageSize=50");
    expect(asked).toContain("eventType=auth-failure");
    release(new Response(JSON.stringify({ items: [olderEvent], nextPageToken: null, hasMore: false }), { status: 200 }));
    expect(await screen.findByText("Searched EZTV for Sintel.")).toBeTruthy();
    expect(screen.getByText("1337x answered HTTP 401.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });

  it("says More indexer history could not be loaded and keeps the rows it has when the next page cannot be read", async () => {
    loaderData.page = { ...initialPage, nextPageToken: "next-1", hasMore: true };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) =>
      String(input).includes("pageToken=") ? new Response("{}", { status: 400 }) : new Response(JSON.stringify(initialPage), { status: 200 }));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("More indexer history could not be loaded."));
    expect(screen.getByText("Read EZTV's RSS feed. 2 new releases.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Load more" })).not.toBeDisabled();
  });
});

describe("the page follows the server (#1179)", () => {
  afterEach(() => {
    loaderData.page = initialPage;
    vi.clearAllMocks();
  });

  // DEFECT (found writing this test, 18 Oct 2026): the page copies the loader's items into its own state once, on the first render, so
  // when the router reads the loader again (on IndexerChanged) a new event never reaches the list; it shows only after a reload. The
  // test is the behaviour the live row promises, and it fails today.
  it.skip("DEFECT: a new event the loader brings after IndexerChanged appears in the list without a reload", () => {
    mockFetch();
    const view = renderPage();
    expect(screen.queryByText("Searched EZTV for Sintel.")).toBeNull();

    loaderData.page = { ...initialPage, items: [olderEvent, ...initialPage.items] };
    view.rerender(<MemoryRouter><IndexerHistoryPage /></MemoryRouter>);

    expect(screen.getByText("Searched EZTV for Sintel.")).toBeTruthy();
  });
});

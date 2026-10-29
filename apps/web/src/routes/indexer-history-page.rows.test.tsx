import { render, screen, waitFor, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

/** #1329: the rows of the combined Indexer History that the older test file leaves out. */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toasts
}));

type Item = {
  id: string; indexerId: string | null; indexerName: string; eventType: "rss-sync" | "search" | "grab" | "auth-failure"; outcome: string; message: string;
  detailsJson: string | null; relatedEntityType: string | null; relatedEntityId: string | null; createdUtc: string;
};
const item = (overrides: Partial<Item> & { id: string }): Item => ({
  indexerId: "idx-1", indexerName: "EZTV", eventType: "rss-sync", outcome: "read", message: `Message ${overrides.id}`, detailsJson: null,
  relatedEntityType: null, relatedEntityId: null, createdUtc: "2026-09-28T10:00:00Z", ...overrides
});

const loaderData: { page: { items: Item[]; nextPageToken: string | null; hasMore: boolean } } = { page: { items: [], nextPageToken: null, hasMore: false } };
const revalidate = vi.fn();

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { IndexerHistoryPage } = await import("./indexer-history-page");

function renderPage() {
  return render(<MemoryRouter><IndexerHistoryPage /></MemoryRouter>);
}

const asked = () => vi.mocked(authedFetch).mock.calls.map((call) => String(call[0]));
const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

beforeEach(() => {
  window.localStorage.clear();
  loaderData.page = { items: [], nextPageToken: null, hasMore: false };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async () => ok({ items: [], nextPageToken: null, hasMore: false }));
  toasts.success.mockReset();
  toasts.error.mockReset();
  revalidate.mockReset();
});
afterEach(() => window.localStorage.clear());

describe("Indexer History's list, drawer and clear (#1329)", () => {
  it("an empty list says 'No indexer activity yet' with its explanation and Clear is disabled", () => {
    renderPage();

    expect(screen.getByText("No indexer activity yet")).toBeTruthy();
    expect(screen.getByText(/RSS syncs, searches, grabs and auth failures across every indexer show up here as they happen/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Clear" })).toBeDisabled();
  });

  it("the card title counts the events: '2 events', '1 event', and '1+ events' while more pages exist", () => {
    loaderData.page = { items: [item({ id: "a" }), item({ id: "b" })], nextPageToken: null, hasMore: false };
    const view = renderPage();
    expect(screen.getByRole("heading", { name: "2 events" })).toBeTruthy();
    view.unmount();

    loaderData.page = { items: [item({ id: "a" })], nextPageToken: null, hasMore: false };
    const single = renderPage();
    expect(screen.getByRole("heading", { name: "1 event" })).toBeTruthy();
    single.unmount();

    loaderData.page = { items: [item({ id: "a" })], nextPageToken: "more", hasMore: true };
    renderPage();
    expect(screen.getByRole("heading", { name: "1+ events" })).toBeTruthy();
  });

  it("each row shows the event name and message, the indexer, and the outcome in plain words with its tone", () => {
    loaderData.page = {
      items: [
        item({ id: "r1", eventType: "rss-sync", outcome: "read", message: "Read the RSS feed" }),
        item({ id: "r2", eventType: "auth-failure", outcome: "failed", message: "Auth refused", indexerName: "Alpha" }),
        item({ id: "r3", eventType: "search", outcome: "failed", message: "Search errored", indexerName: "Bravo" }),
        item({ id: "r4", eventType: "grab", outcome: "matched", message: "Release sent", indexerName: "Charlie" }),
        item({ id: "r5", eventType: "search", outcome: "checked", message: "Nothing to send", indexerName: "Delta" }),
        item({ id: "r6", eventType: "search", outcome: "held", message: "Held it", indexerName: "Echo" }),
        item({ id: "r7", eventType: "search", outcome: "blocked", message: "Blocked it", indexerName: "Foxtrot" })
      ], nextPageToken: null, hasMore: false
    };
    renderPage();

    const row = (message: string) => screen.getByText(message).closest("[role=row]") as HTMLElement;
    const chip = (message: string, words: string) => within(row(message)).getByText(words);
    expect(within(row("Read the RSS feed")).getByText("RSS sync")).toBeTruthy();
    expect(within(row("Read the RSS feed")).getByText("EZTV")).toBeTruthy();
    const toneOf = (message: string, words: string) => chip(message, words).className;
    // An auth failure is bad, any other failure a warning, a grab ok, the rest idle: four different looks.
    const tones = new Set([
      toneOf("Auth refused", "Failed"),
      toneOf("Search errored", "Failed"),
      toneOf("Release sent", "Matched and sent"),
      toneOf("Read the RSS feed", "Read the feed")
    ]);
    expect(tones.size).toBe(4);
    expect(toneOf("Nothing to send", "Checked, nothing to send")).toBe(toneOf("Read the RSS feed", "Read the feed"));
    expect(toneOf("Held it", "Held for review")).toBe(toneOf("Read the RSS feed", "Read the feed"));
    expect(toneOf("Blocked it", "Blocked before searching")).toBe(toneOf("Read the RSS feed", "Read the feed"));
  });

  it("a row opens a drawer with the indexer, outcome in words, when, related title and the detail pretty-printed", () => {
    loaderData.page = {
      items: [item({ id: "d1", eventType: "grab", outcome: "matched", message: "Sent Sintel", relatedEntityType: "movie", relatedEntityId: "movie-9", detailsJson: '{"release":"Sintel.2010","size":123}' })],
      nextPageToken: null, hasMore: false
    };
    renderPage();

    fireEvent.click(screen.getByText("Sent Sintel"));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Indexer")).toBeTruthy();
    expect(within(dialog).getByText("Matched and sent")).toBeTruthy();
    expect(within(dialog).getByText("When")).toBeTruthy();
    expect(within(dialog).getByText("movie movie-9")).toBeTruthy();
    expect(within(dialog).getByText("Request/response detail")).toBeTruthy();
    const pre = dialog.querySelector("pre")!;
    expect(pre.textContent).toBe(JSON.stringify({ release: "Sintel.2010", size: 123 }, null, 2));
  });

  it("detail that is not JSON shows as the raw text", () => {
    loaderData.page = { items: [item({ id: "d2", message: "Odd reply", detailsJson: "HTTP 502 Bad Gateway" })], nextPageToken: null, hasMore: false };
    renderPage();

    fireEvent.click(screen.getByText("Odd reply"));
    expect(within(screen.getByRole("dialog")).getByText("HTTP 502 Bad Gateway")).toBeTruthy();
  });

  it("a failed filtered read shows the toast 'Indexer history could not be loaded.'", async () => {
    loaderData.page = { items: [item({ id: "a" })], nextPageToken: null, hasMore: false };
    vi.mocked(authedFetch).mockImplementation(async () => new Response("{}", { status: 400 }));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    fireEvent.click(screen.getByRole("radio", { name: "Search" }));

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Indexer history could not be loaded."));
  });

  it("'Find an indexer' asks again after 350 ms with indexerName, not on every key", async () => {
    loaderData.page = { items: [item({ id: "a" })], nextPageToken: null, hasMore: false };
    renderPage();

    const box = screen.getByPlaceholderText("Find an indexer");
    fireEvent.change(box, { target: { value: "EZ" } });
    fireEvent.change(box, { target: { value: "EZTV" } });
    expect(asked().some((url) => url.includes("indexerName"))).toBe(false);

    await waitFor(() => expect(asked().filter((url) => url.includes("indexerName=EZTV"))).toHaveLength(1), { timeout: 3000 });
    expect(asked().some((url) => url.includes("indexerName=EZ&"))).toBe(false);
  });

  it("Clear asks first, saying per-title history is untouched; Cancel sends nothing", () => {
    loaderData.page = { items: [item({ id: "a" })], nextPageToken: null, hasMore: false };
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Clear Indexer History?")).toBeTruthy();
    expect(dialog.textContent).toMatch(/never removes the per-movie or per-series search history/);
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(asked().some((url) => url.includes("/clear"))).toBe(false);
  });

  it("confirming Clear posts it, says it is cleared, empties the list and reads again; a failure says so and keeps the list", async () => {
    loaderData.page = { items: [item({ id: "a", message: "Keep me" })], nextPageToken: null, hasMore: false };
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).includes("/clear") ? new Response("{}", { status: 500 }) : ok({ items: [], nextPageToken: null, hasMore: false }));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clear history" }));
    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("Indexer history could not be cleared."));
    expect(screen.getByText("Keep me")).toBeTruthy();

    vi.mocked(authedFetch).mockImplementation(async () => ok({ cleared: true }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clear history" }));
    await waitFor(() => expect(toasts.success).toHaveBeenCalledWith("Indexer history cleared"));
    expect(screen.queryByText("Keep me")).toBeNull();
    expect(screen.getByText("No indexer activity yet")).toBeTruthy();
    expect(revalidate).toHaveBeenCalled();
  });
});

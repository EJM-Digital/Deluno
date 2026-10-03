import { renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const fetchPage = vi.fn();
vi.mock("../../lib/api", () => ({ fetchJson: vi.fn(), fetchPage: (path: string) => fetchPage(path) }));

import { useActivityHistory } from "./activity-history";
import type { ActivitySort } from "../../lib/activity-history";

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

const page = (id: string, next: string | null) => ({
  items: [{ id, category: "movie.search.manual", message: id, createdUtc: "2026-10-01T10:00:00Z" }],
  nextPageToken: next,
  hasMore: next !== null
});

describe("the Activity history's sort (#1119)", () => {
  beforeEach(() => {
    fetchPage.mockReset();
    fetchPage.mockImplementation(async (path: string) => (String(path).includes("pageToken") ? page("b", null) : page("a", "t1")));
  });

  it("asks for newest first with nothing extra, then for the chosen sort from the first page again", async () => {
    const { result, rerender } = renderHook(
      ({ sort }: { sort: ActivitySort }) => useActivityHistory({ enabled: true, onOpenEvent: () => undefined, sort }),
      { wrapper, initialProps: { sort: { id: "when", direction: "desc" } as ActivitySort } }
    );

    await waitFor(() => expect(result.current.events.map((event) => event.id)).toEqual(["a"]));
    expect(fetchPage).toHaveBeenLastCalledWith("/api/activity?pageSize=50");

    await result.current.history.fetchNextPage();
    await waitFor(() => expect(result.current.events.map((event) => event.id)).toEqual(["a", "b"]));
    expect(fetchPage).toHaveBeenLastCalledWith("/api/activity?pageSize=50&pageToken=t1");

    // Another sort is another question: paging starts again from the first page, with no old token.
    rerender({ sort: { id: "title", direction: "asc" } });
    await waitFor(() => expect(fetchPage).toHaveBeenLastCalledWith("/api/activity?pageSize=50&sortBy=title&sortDir=asc"));
    await waitFor(() => expect(result.current.events.map((event) => event.id)).toEqual(["a"]));
  });
});

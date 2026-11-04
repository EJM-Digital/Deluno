import { act, cleanup, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, useLocation, useNavigationType } from "react-router-dom";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const fetchPage = vi.fn();
const fetchJson = vi.fn();
vi.mock("../../lib/api", () => ({ fetchJson: (path: string) => fetchJson(path), fetchPage: (path: string) => fetchPage(path) }));

let pushed: ((event: unknown) => void) | undefined;
const subscribed: [string, string][] = [];
vi.mock("../../lib/use-signalr", () => ({
  RealtimeGroups: { Activity: "activity" },
  useSignalREvent: (name: string, group: string, handler: (event: unknown) => void) => {
    subscribed.push([name, group]);
    pushed = handler;
  }
}));

const readAgain = vi.fn();
let readAgainCallback: (() => void) | undefined;
vi.mock("../../hooks/use-live-interval", () => ({
  useCoalescedRevalidate: (callback: () => void) => {
    readAgainCallback = callback;
    return readAgain;
  }
}));

import { ActivityFilterTokens, ActivityFilters, useActivityHistory } from "./activity-history";
import { emptyActivityFilters } from "../../lib/activity-history";
import { queryClient } from "../../lib/route-cache";

/**
 * The Activity history's address, live push and filter bar (#1100, #1179, #1329): a filter lives in the address, a pushed event
 * goes into every cached view it belongs in, a link to one event opens it once it has been read, and a typed search waits a
 * moment before it reaches the address.
 */
const page = (ids: string[], next: string | null) => ({
  items: ids.map((id) => ({ id, category: "movie.search.manual", message: id, createdUtc: "2026-10-01T10:00:00Z", type: "search" })),
  nextPageToken: next,
  hasMore: next !== null
});

const push = (id: string, extra: Record<string, unknown> = {}) => ({ id, category: "movie.search.manual", message: id, createdUtc: new Date().toISOString(), type: "search", ...extra });

function wrapperAt(path: string) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[path]}>{children}</MemoryRouter>
      </QueryClientProvider>
    );
  };
}

function hook(path: string, options: { enabled?: boolean } = {}) {
  const onOpenEvent = vi.fn();
  const view = renderHook(
    () => {
      const location = useLocation();
      const navigation = useNavigationType();
      return { ...useActivityHistory({ enabled: options.enabled ?? true, onOpenEvent }), search: location.search, navigation };
    },
    { wrapper: wrapperAt(path) }
  );
  return { ...view, onOpenEvent };
}

const ids = (result: { current: { events: { id: string }[] } }) => result.current.events.map((event) => event.id);

beforeEach(() => {
  queryClient.clear();
  fetchPage.mockReset();
  fetchJson.mockReset();
  readAgain.mockReset();
  pushed = undefined;
  fetchPage.mockImplementation(async (path: string) => (String(path).includes("pageToken") ? page(["b"], null) : page(["a"], "t1")));
  fetchJson.mockResolvedValue({ types: [{ value: "search", label: "Search" }], sources: [{ value: "tmdb", label: "TMDB" }], outcomes: [{ value: "grabbed", label: "Grabbed" }], reasons: [{ value: "language", label: "Language" }] });
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("changing the Activity filters in the address", () => {
  it("puts a chosen filter in the address beside what was already there, and clears them all", async () => {
    const { result } = hook("/activity?section=work&type=search");
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    expect(result.current.filters.type).toBe("search");
    expect(result.current.filtered).toBe(true);

    act(() => result.current.setFilters({ since: "today" }));
    expect(result.current.search).toContain("since=today");
    expect(result.current.search).toContain("type=search");
    expect(result.current.search).toContain("section=work");
    expect(result.current.navigation).toBe("REPLACE");

    act(() => result.current.clearFilters());
    expect(result.current.search).toBe("?section=work");
    expect(result.current.filtered).toBe(false);
    expect(result.current.navigation).toBe("REPLACE");
  });

  it("does not ask the history while it is switched off, or for the three steps built from live sources", async () => {
    hook("/activity", { enabled: false });
    hook("/activity?type=processing");
    await act(async () => undefined);
    expect(fetchPage).not.toHaveBeenCalled();
  });
});

describe("a link to one event", () => {
  it("opens the event once it has been read, and only once", async () => {
    const { result, onOpenEvent } = hook("/activity?event=a");
    await waitFor(() => expect(onOpenEvent).toHaveBeenCalledTimes(1));
    expect(onOpenEvent).toHaveBeenCalledWith(expect.objectContaining({ id: "a" }));
    act(() => result.current.setFilters({ since: "today" }));
    await waitFor(() => expect(fetchPage).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(result.current.history.isFetching).toBe(false));
    await act(async () => undefined);
    expect(onOpenEvent).toHaveBeenCalledTimes(1);
  });

  it("reads no older page when the address names no event", async () => {
    const { result } = hook("/activity");
    await waitFor(() => expect(result.current.history.hasNextPage).toBe(true));
    await act(async () => undefined);
    expect(fetchPage).toHaveBeenCalledTimes(1);
  });

  it("reads each older page once and stops when the event is in none of them", async () => {
    const { onOpenEvent } = hook("/activity?event=missing");
    await waitFor(() => expect(fetchPage).toHaveBeenCalledTimes(2));
    await act(async () => undefined);
    expect(fetchPage).toHaveBeenCalledTimes(2);
    expect(onOpenEvent).not.toHaveBeenCalled();
  });

  it("reads older pages until it finds the event", async () => {
    const { onOpenEvent } = hook("/activity?event=b");
    await waitFor(() => expect(onOpenEvent).toHaveBeenCalledWith(expect.objectContaining({ id: "b" })));
    expect(fetchPage).toHaveBeenLastCalledWith(expect.stringContaining("pageToken=t1"));
  });

  it("opens nothing when there is no such event and no older page", async () => {
    fetchPage.mockResolvedValue(page(["a"], null));
    const { onOpenEvent } = hook("/activity?event=missing");
    await waitFor(() => expect(fetchPage).toHaveBeenCalledTimes(1));
    await act(async () => undefined);
    expect(onOpenEvent).not.toHaveBeenCalled();
    expect(fetchPage).toHaveBeenCalledTimes(1);
  });

  it("does not open it while the history is switched off", async () => {
    const { onOpenEvent } = hook("/activity?event=a", { enabled: false });
    await act(async () => undefined);
    expect(onOpenEvent).not.toHaveBeenCalled();
  });
});

describe("a new event pushed to the Activity history (#1179)", () => {
  it("goes at the head of a newest-first view it belongs in, and is not added twice", async () => {
    queryClient.setQueryData(["unrelated"], { pages: "untouched" });
    const { result } = hook("/activity?type=search");
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    expect(subscribed[0]).toEqual(["ActivityEventAdded", "activity"]);
    act(() => pushed!(push("new")));
    await waitFor(() => expect(ids(result)).toEqual(["new", "a"]));
    act(() => pushed!(push("new")));
    expect(ids(result)).toEqual(["new", "a"]);
    expect(readAgain).not.toHaveBeenCalled();
    expect(queryClient.getQueryData(["unrelated"])).toEqual({ pages: "untouched" });
    expect(result.current.folded).toHaveLength(2);
  });

  it("leaves a view it does not belong in alone", async () => {
    const { result } = hook("/activity?type=other");
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    act(() => pushed!(push("new")));
    expect(ids(result)).toEqual(["a"]);
    expect(readAgain).not.toHaveBeenCalled();
  });

  it("reads a view again, once, when the push cannot settle it", async () => {
    const { result } = hook("/activity?title=Dune");
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    act(() => pushed!(push("untitled")));
    expect(readAgain).toHaveBeenCalledTimes(1);
    expect(ids(result)).toEqual(["a"]);
  });

  it("reads a view again, not patched, when it is sorted by a heading", async () => {
    const { result } = renderHook(() => useActivityHistory({ enabled: true, onOpenEvent: () => undefined, sort: { id: "title", direction: "asc" } }), { wrapper: wrapperAt("/activity") });
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    act(() => pushed!(push("new")));
    expect(readAgain).toHaveBeenCalledTimes(1);
    expect(ids(result)).toEqual(["a"]);
  });

  it("skips a view that has not been read yet", async () => {
    const unread = ["activity-history", { ...emptyActivityFilters, type: "search" }, "when", "desc"];
    void queryClient.prefetchQuery({ queryKey: unread, queryFn: () => new Promise(() => undefined) });
    const { result } = hook("/activity");
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    act(() => pushed!(push("new")));
    await waitFor(() => expect(ids(result)).toEqual(["new", "a"]));
    expect(readAgain).not.toHaveBeenCalled();
    expect(queryClient.getQueryData(unread)).toBeUndefined();
  });

  it("does nothing at all while the history is switched off", async () => {
    const first = hook("/activity");
    await waitFor(() => expect(ids(first.result)).toEqual(["a"]));
    first.unmount();
    hook("/activity", { enabled: false });
    act(() => pushed!(push("other")));
    expect(readAgain).not.toHaveBeenCalled();
    const cached = queryClient.getQueryCache().findAll({ queryKey: ["activity-history"] })[0].state.data as { pages: { items: unknown[] }[] };
    expect(cached.pages[0].items).toHaveLength(1);
  });

  it("reads again when any cached view is sorted by a heading, even beside a newest-first one", async () => {
    const { result } = hook("/activity");
    await waitFor(() => expect(ids(result)).toEqual(["a"]));
    queryClient.setQueryData(["activity-history", { ...emptyActivityFilters }, "title", "asc"], { pages: [{ items: [] }], pageParams: [undefined] });
    act(() => pushed!(push("new")));
    expect(readAgain).toHaveBeenCalledTimes(1);
  });

  it("asks the server for the history again when the burst has settled", () => {
    const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue(undefined);
    hook("/activity");
    readAgainCallback!();
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["activity-history"] });
  });
});

function bar(filters = emptyActivityFilters, setFilters: (patch: object) => void = () => undefined) {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ActivityFilters filters={filters} setFilters={setFilters} chips={<div><span>chips</span></div>} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe("the Activity search box", () => {
  it("waits a moment after the last key before it reaches the address", () => {
    vi.useFakeTimers();
    const setFilters = vi.fn();
    bar(emptyActivityFilters, setFilters);
    const box = screen.getByRole("searchbox", { name: "Find a title" });
    fireEvent.change(box, { target: { value: "Du" } });
    act(() => void vi.advanceTimersByTime(200));
    fireEvent.change(box, { target: { value: "Dune" } });
    act(() => void vi.advanceTimersByTime(300));
    expect(setFilters).not.toHaveBeenCalled();
    act(() => void vi.advanceTimersByTime(100));
    expect(setFilters).toHaveBeenCalledTimes(1);
    expect(setFilters).toHaveBeenCalledWith({ title: "Dune" });
  });

  it("lets go of focus on Escape and ignores other keys", () => {
    bar();
    const box = screen.getByRole("searchbox", { name: "Find a title" });
    box.focus();
    fireEvent.keyDown(box, { key: "a" });
    expect(document.activeElement).toBe(box);
    fireEvent.keyDown(box, { key: "Escape" });
    expect(document.activeElement).not.toBe(box);
  });
});

describe("the Activity dropdowns", () => {
  it("offer the server's steps and the live steps, and set the step or the time", async () => {
    const setFilters = vi.fn();
    bar(emptyActivityFilters, setFilters);
    await act(async () => undefined);
    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    expect(await screen.findByRole("radio", { name: "Download health" })).toBeTruthy();
    expect(screen.getByRole("radiogroup", { name: "Filter by step" }).parentElement!.textContent).toContain("Step");
    expect(screen.getByRole("radiogroup", { name: "Filter by time" }).parentElement!.textContent).toContain("Time");
    const names = (group: string) => within(screen.getByRole("radiogroup", { name: group })).getAllByRole("radio").map((radio) => radio.textContent);
    expect(names("Filter by step")).toEqual(["Any step", "Search", "Processing", "Download health", "Download client"]);
    expect(names("Filter by time")).toEqual(["Any time", "Today", "Since yesterday", "Last 7 days", "Last 30 days"]);
    expect(screen.getByRole("radio", { name: "Any step" }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("radio", { name: "Any time" }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(screen.getByRole("radio", { name: "Search" }));
    expect(setFilters).toHaveBeenCalledWith({ type: "search" });
    fireEvent.click(screen.getByRole("radio", { name: "Today" }));
    expect(setFilters).toHaveBeenCalledWith({ since: "today", until: "" });
  });

  it("offer the day a link set as a choice of its own", async () => {
    bar({ ...emptyActivityFilters, since: "2026-10-01" });
    await act(async () => undefined);
    fireEvent.click(screen.getByRole("button", { name: "More filters, 1 on" }));
    expect(await screen.findByRole("radio", { name: "Since 2026-10-01" })).toBeTruthy();
  });
});

describe("the filters a link can still set", () => {
  const tokens = (filters: object, setFilters = vi.fn(), clearFilters = vi.fn()) => {
    const view = render(
      <QueryClientProvider client={queryClient}>
        <ActivityFilterTokens filters={{ ...emptyActivityFilters, ...filters }} setFilters={setFilters} clearFilters={clearFilters} />
      </QueryClientProvider>
    );
    return { view, setFilters, clearFilters };
  };

  it("show nothing when no such filter is set", () => {
    const { view } = tokens({ title: "Dune", type: "search" });
    expect(view.container.firstChild).toBeNull();
  });

  it("show each one in the server's words, and remove each on its own", async () => {
    const { setFilters } = tokens({ file: "a.mkv", source: "tmdb", outcome: "grabbed", reason: "language", item: "m1", until: "2026-10-02" });
    await screen.findByText("From: TMDB");
    for (const text of ["File: a.mkv", "Outcome: Grabbed", "Reason: Language", "Only this title", "Until: 2026-10-02"]) expect(screen.getByText(text)).toBeTruthy();
    const removed = (name: string, patch: object) => {
      setFilters.mockClear();
      fireEvent.click(screen.getByRole("button", { name: `Remove filter ${name}` }));
      expect(setFilters.mock.calls).toEqual([[patch]]);
    };
    removed("File: a.mkv", { file: "" });
    removed("From: TMDB", { source: "" });
    removed("Outcome: Grabbed", { outcome: "" });
    removed("Reason: Language", { reason: "" });
    removed("Only this title", { item: "", itemType: "" });
    removed("Until: 2026-10-02", { until: "" });
  });

  it("show a word the server's list does not know as it came", async () => {
    tokens({ source: "tmdb", outcome: "yyy", reason: "xxx" });
    await screen.findByText("From: TMDB");
    expect(screen.getByText("From: TMDB")).toBeTruthy();
    expect(screen.getByText("Outcome: yyy")).toBeTruthy();
    expect(screen.getByText("Reason: xxx")).toBeTruthy();
  });

  it("show the raw word until the server's list arrives, and a kind of title when no one title is set", () => {
    fetchJson.mockReturnValue(new Promise(() => undefined));
    const { setFilters, clearFilters } = tokens({ source: "tmdb", itemType: "movie" });
    expect(screen.getByText("From: tmdb")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Remove filter Kind of title: movie" }));
    expect(setFilters).toHaveBeenCalledWith({ itemType: "" });
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(clearFilters).toHaveBeenCalledTimes(1);
  });
});

describe("a history page that says there is more but names no next page", () => {
  it("stops paging", async () => {
    fetchPage.mockResolvedValue({ items: page(["a"], null).items, nextPageToken: null, hasMore: true });
    const { result } = hook("/activity");
    await waitFor(() => expect(result.current.events).toHaveLength(1));
    expect(result.current.history.hasNextPage).toBe(false);
  });
});

describe("measuring the room on the header's title line", () => {
  const rect = (width: number) => () => ({ width }) as DOMRect;
  const headerWith = (parts: { chips?: boolean; actions?: boolean; fontSize?: string; gap?: string }) => {
    document.documentElement.style.fontSize = parts.fontSize ?? "";
    document.body.innerHTML = `<div id="topbar-main" style="column-gap:${parts.gap ?? ""}"><div style="column-gap:${parts.gap ?? ""}"><span id="topbar-title"></span></div><div id="topbar-page-tabs"></div><div id="topbar-page-actions">${parts.actions ? "<button>Go</button>" : ""}</div></div>`;
    document.getElementById("topbar-main")!.getBoundingClientRect = rect(1000);
    document.getElementById("topbar-title")!.getBoundingClientRect = rect(200);
    document.getElementById("topbar-page-actions")!.getBoundingClientRect = rect(100);
  };
  const place = async (chips: boolean = true) => {
    await act(async () => {
      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <ActivityFilters filters={emptyActivityFilters} setFilters={() => undefined} chips={chips ? <div><span>chips</span></div> : null} />
          </MemoryRouter>
        </QueryClientProvider>,
        { container: document.body.appendChild(document.createElement("div")) }
      );
    });
    return screen.getByTestId("activity-filters").getAttribute("data-placement");
  };
  beforeEach(() => {
    vi.spyOn(HTMLElement.prototype, "scrollWidth", "get").mockReturnValue(300);
    window.matchMedia = ((query: string) => ({
      matches: true, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false
    })) as unknown as typeof window.matchMedia;
  });
  afterEach(() => {
    document.body.innerHTML = "";
    document.documentElement.style.fontSize = "";
    vi.unstubAllGlobals();
    delete (document as { fonts?: unknown }).fonts;
  });

  it("uses the page's own font size and gaps, and leaves room for the page's buttons", async () => {
    headerWith({ actions: true, fontSize: "20px", gap: "8px" });
    expect(await place()).toBe("header");
    cleanup();
    // 1000 - (100 + 8) - 200 - 8 = 684 left; the widest stage needs (11 + 8.5) * 20 + 300 + 16 = 706.
    headerWith({ actions: true, fontSize: "20px", gap: "8px" });
    document.getElementById("topbar-main")!.getBoundingClientRect = rect(1000 - 100);
    expect(await place()).toBe("header");
    expect(screen.getByRole("searchbox", { name: "Find a title" }).className).toContain("cursor-pointer");
  });

  it("stays in the row under the header while the header has no title, or the chips are not drawn", async () => {
    headerWith({});
    document.getElementById("topbar-title")!.remove();
    expect(await place()).toBe("header");
    cleanup();
    headerWith({});
    expect(await place(false)).toBe("header");
  });

  it("measures again when the header changes size or the fonts arrive", async () => {
    let measureAgain: (() => void) | undefined;
    const watched: Element[] = [];
    let disconnected = 0;
    vi.stubGlobal("ResizeObserver", class {
      constructor(callback: () => void) {
        measureAgain = callback;
      }
      observe(element: Element) {
        watched.push(element);
      }
      disconnect() {
        disconnected += 1;
      }
    });
    let fontsReady: () => void = () => undefined;
    Object.defineProperty(document, "fonts", { configurable: true, value: { ready: new Promise<void>((resolve) => (fontsReady = resolve)) } });
    headerWith({});
    document.getElementById("topbar-page-actions")!.remove();
    expect(await place()).toBe("header");
    expect(watched.map((element) => element.id)).toEqual(["topbar-main", "topbar-title"]);

    document.getElementById("topbar-main")!.getBoundingClientRect = rect(300);
    await act(async () => measureAgain!());
    expect(screen.getByTestId("activity-filters").getAttribute("data-placement")).toBe("row");
    document.getElementById("topbar-main")!.getBoundingClientRect = rect(1000);
    await act(async () => {
      fontsReady();
    });
    expect(screen.getByTestId("activity-filters").getAttribute("data-placement")).toBe("header");
    cleanup();
    expect(disconnected).toBeGreaterThan(0);
  });
});

describe("how the Activity filters are dressed", () => {
  it("fill a row of their own with a full-size search when they are not on the title line", () => {
    bar();
    const group = screen.getByTestId("activity-filters");
    expect(group.className).toContain("w-full pt-3 max-lg:flex-wrap");
    expect(group.className).toContain("flex min-w-0 items-center gap-2");
    expect(group.hasAttribute("data-header-controls")).toBe(false);
    expect(group.className).not.toContain("flex-1");
    const box = screen.getByRole("searchbox", { name: "Find a title" });
    expect(box.className).toContain("h-9.5");
    expect(box.className).toContain("rounded-xl");
    expect(box.className).toContain("min-w-26");
    expect(box.className).not.toContain("cursor-pointer");
    expect(box.className).not.toContain("placeholder:text-transparent");
    expect(box.parentElement!.className).toContain("relative min-w-0");
    expect(box.parentElement!.className).toContain("flex-[0_100_260px]");
    expect(group.querySelector("svg.lucide-search")).toBeNull();
  });

  describe("on the title line", () => {
    beforeEach(() => {
      vi.spyOn(HTMLElement.prototype, "scrollWidth", "get").mockReturnValue(300);
      window.matchMedia = ((query: string) => ({ matches: true, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
    });
    afterEach(() => {
      document.body.innerHTML = "";
    });
    const at = async (room: number) => {
      document.body.innerHTML = '<div id="topbar-main"><div><span id="topbar-title"></span></div><div id="topbar-page-tabs"></div><div id="topbar-page-actions"></div></div>';
      document.getElementById("topbar-main")!.getBoundingClientRect = () => ({ width: room + 200 }) as DOMRect;
      document.getElementById("topbar-title")!.getBoundingClientRect = () => ({ width: 200 }) as DOMRect;
      await act(async () => {
        render(
          <QueryClientProvider client={queryClient}>
            <MemoryRouter>
              <ActivityFilters filters={emptyActivityFilters} setFilters={() => undefined} chips={<div><span>chips</span></div>} />
            </MemoryRouter>
          </QueryClientProvider>,
          { container: document.body.appendChild(document.createElement("div")) }
        );
      });
      return { group: screen.getByTestId("activity-filters"), box: screen.getByRole("searchbox", { name: "Find a title" }) };
    };

    it("share the line, keep the search full size when there is room, and use the full-size dropdown", async () => {
      const { group, box } = await at(900);
      expect(group.className).toContain("flex-1");
      expect(group.className).not.toContain("pt-3");
      expect(group.getAttribute("data-header-controls")).toBe("");
      expect(box.className).not.toContain("cursor-pointer");
      expect(group.querySelector("[data-activity-scope] button")!.className).toContain("h-9.5");
    });

    it("show a magnifier that hides its prompt until it opens, with the compact dropdown only further on", async () => {
      const { group, box } = await at(495);
      expect(box.className).toContain("cursor-pointer pl-9 pr-2 focus:cursor-text");
      expect(box.className).toContain("placeholder:text-transparent");
      expect(box.className).not.toContain("min-w-26");
      expect(box.parentElement!.className).toContain("w-9.5");
      expect(box.parentElement!.className).not.toContain("flex-[0_100_260px]");
      expect(group.querySelector("svg.lucide-search")).not.toBeNull();
      fireEvent.focus(box);
      expect(box.className).not.toContain("placeholder:text-transparent");
      expect(box.className).toContain("cursor-pointer");
    });
  });
});

describe("the Activity filter choices", () => {
  it("are asked once from the server and kept", async () => {
    bar();
    await waitFor(() => expect(queryClient.getQueryData(["activity-filter-choices"])).toBeDefined());
    expect(fetchJson).toHaveBeenCalledWith("/api/activity/filters");
  });

  it("do not set the same search again once the address has caught up", () => {
    vi.useFakeTimers();
    const setFilters = vi.fn();
    bar({ ...emptyActivityFilters, title: "Dune" }, setFilters);
    act(() => void vi.advanceTimersByTime(1000));
    expect(setFilters).not.toHaveBeenCalled();
  });

  it("follow the address when the title there changes", () => {
    const view = bar({ ...emptyActivityFilters, title: "Dune" });
    view.rerender(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <ActivityFilters filters={{ ...emptyActivityFilters, title: "Alien" }} setFilters={() => undefined} chips={<div />} />
        </MemoryRouter>
      </QueryClientProvider>
    );
    expect((screen.getByRole("searchbox", { name: "Find a title" }) as HTMLInputElement).value).toBe("Alien");
  });
});

describe("how much room the Activity filters count on the title line", () => {
  beforeEach(() => {
    vi.spyOn(HTMLElement.prototype, "scrollWidth", "get").mockReturnValue(300);
    window.matchMedia = ((query: string) => ({ matches: true, media: query, onchange: null, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false })) as unknown as typeof window.matchMedia;
  });
  afterEach(() => {
    document.body.innerHTML = "";
  });
  // At 16px to the rem the stages need 628, 490, 482 and 326 px: the compact dropdowns hold from 482 up to 489.
  const compactAt = async (main: number, parts: { gap: string; button: boolean }) => {
    document.body.innerHTML = `<div id="topbar-main" style="column-gap:${parts.gap}"><div style="column-gap:${parts.gap}"><span id="topbar-title"></span></div><div id="topbar-page-tabs"></div><div id="topbar-page-actions">${parts.button ? "<button>Go</button>" : ""}</div></div>`;
    document.getElementById("topbar-main")!.getBoundingClientRect = () => ({ width: main }) as DOMRect;
    document.getElementById("topbar-title")!.getBoundingClientRect = () => ({ width: 200 }) as DOMRect;
    document.getElementById("topbar-page-actions")!.getBoundingClientRect = () => ({ width: 100 }) as DOMRect;
    await act(async () => {
      render(
        <QueryClientProvider client={queryClient}>
          <MemoryRouter>
            <ActivityFilters filters={emptyActivityFilters} setFilters={() => undefined} chips={<div><span>chips</span></div>} />
          </MemoryRouter>
        </QueryClientProvider>,
        { container: document.body.appendChild(document.createElement("div")) }
      );
    });
    const group = screen.getByTestId("activity-filters");
    return { placement: group.getAttribute("data-placement"), compact: !group.querySelector("[data-activity-scope] button")!.className.includes("h-9.5"), magnifier: screen.getByRole("searchbox", { name: "Find a title" }).className.includes("cursor-pointer") };
  };

  it("takes off the page's buttons and both gaps", async () => {
    // 805 - (100 + 8) - 200 - 8 = 489.
    expect(await compactAt(805, { gap: "8px", button: true })).toEqual({ placement: "header", compact: true, magnifier: true });
  });

  it("takes off nothing for buttons that are not there, however wide their box is", async () => {
    expect(await compactAt(689, { gap: "", button: false })).toEqual({ placement: "header", compact: true, magnifier: true });
  });
});

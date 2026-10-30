import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_DISPLAY_PREFERENCES, type DisplayPreferences } from "../lib/display-preferences";

/**
 * The Schedule month grid (#1329): a cell per day, today picked out, days outside the month dimmed, and the week starting on the
 * day the person chose in Interface settings. The clock is fixed to a mid-month day so the month always has days outside it.
 */

const revalidate = vi.fn();
const loaderData = {
  episodes: [
    { episodeId: "e1", seriesId: "s1", seriesTitle: "Timed Show", posterUrl: null, seasonNumber: 1, episodeNumber: 1, title: "With a time", airDateUtc: "2026-10-15T19:30:00Z", monitored: true, wantedStatus: "missing" },
    { episodeId: "e2", seriesId: "s1", seriesTitle: "Timed Show", posterUrl: null, seasonNumber: 1, episodeNumber: 2, title: "Midnight placeholder", airDateUtc: "2026-10-16T00:00:00Z", monitored: true, wantedStatus: "missing" }
  ] as unknown[],
  movies: [
    { movieId: "m1", title: "Film One", releaseYear: 2026, posterUrl: null, kind: "digital" as const, date: "2026-10-15", monitored: true, wantedStatus: "missing" },
    { movieId: "m2", title: "Film Two", releaseYear: 2026, posterUrl: null, kind: "digital" as const, date: "2026-10-20", monitored: true, wantedStatus: "missing" }
  ]
};

let preferences: DisplayPreferences = DEFAULT_DISPLAY_PREFERENCES;

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigate: () => vi.fn()
}));

vi.mock("../lib/route-cache", async (importOriginal) => {
  const { readyRouteSources } = await import("../test/route-sources");
  return {
    ...(await importOriginal<typeof import("../lib/route-cache")>()),
    useRouteSources: () => readyRouteSources(loaderData, revalidate)
  };
});

vi.mock("../lib/display-preferences", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/display-preferences")>()),
  useDisplayPreferences: () => ({ preferences })
}));

const { CalendarPage } = await import("./calendar-page");

beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date(2026, 9, 15, 12, 0, 0));
  preferences = DEFAULT_DISPLAY_PREFERENCES;
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function renderMonth() {
  return render(<MemoryRouter><CalendarPage /></MemoryRouter>);
}

/** The day-number spans of the month grid, in order. */
function dayNumbers(container: HTMLElement) {
  return Array.from(container.querySelectorAll("div.grid.grid-cols-7 > div")).map((cell) => ({
    cell,
    number: cell.querySelector("span")!
  }));
}

describe("Schedule month grid", () => {
  it("draws a cell for every day of whole weeks, with 7 week headers", () => {
    const { container } = renderMonth();
    const cells = dayNumbers(container);

    expect(cells.length % 7).toBe(0);
    expect(cells.length).toBeGreaterThanOrEqual(35);
    expect(cells.filter(({ number }) => number.textContent === "31").length).toBeGreaterThanOrEqual(1);
    expect(container.querySelectorAll("div.grid.grid-cols-7 > span")).toHaveLength(7);
  });

  it("picks out today and only today, and dims the days outside the month", () => {
    const { container } = renderMonth();
    const cells = dayNumbers(container);

    const today = cells.filter(({ cell }) => cell.className.includes("bg-primary/6"));
    expect(today).toHaveLength(1);
    expect(today[0].number.textContent).toBe("15");
    expect(today[0].number.className).toContain("text-primary");

    const outside = cells.filter(({ cell }) => cell.className.includes("bg-surface-2/30"));
    expect(outside.length).toBeGreaterThan(0);
    for (const { number } of outside) expect(number.className).toContain("text-muted-foreground");
    const inside = cells.filter(({ cell }) => !cell.className.includes("bg-surface-2/30"));
    expect(inside.map(({ number }) => number.textContent).filter((n) => n === "1" || n === "31")).toHaveLength(2);
  });

  it("shows a film as a chip on its own day", () => {
    renderMonth();
    expect(screen.getByText("Film One")).toBeInTheDocument();
    expect(screen.getByText("Film Two")).toBeInTheDocument();
  });

  it("starts the week on Monday by default and on Sunday when that is chosen", () => {
    const monday = renderMonth();
    const headers = () => Array.from(document.querySelectorAll("div.grid.grid-cols-7 > span")).map((span) => span.textContent ?? "");
    expect(headers()[0]).toMatch(/^Mon/);
    expect(headers()[6]).toMatch(/^Sun/);
    monday.unmount();

    preferences = { ...DEFAULT_DISPLAY_PREFERENCES, calendarFirstDayOfWeek: "sunday" };
    renderMonth();
    expect(headers()[0]).toMatch(/^Sun/);
    expect(headers()[6]).toMatch(/^Sat/);
  });

  it("lists times by the chosen clock, in the chosen time zone, and says All day for a midnight placeholder", () => {
    preferences = { ...DEFAULT_DISPLAY_PREFERENCES, timezone: "UTC", timeFormat: "12" };
    const twelve = renderMonth();
    fireEvent.click(screen.getByRole("radio", { name: "List" }));
    expect(screen.getByText(/7:30\s?pm/i)).toBeInTheDocument();
    expect(screen.getAllByText("All day").length).toBeGreaterThan(0);
    twelve.unmount();

    preferences = { ...DEFAULT_DISPLAY_PREFERENCES, timezone: "UTC", timeFormat: "24" };
    renderMonth();
    fireEvent.click(screen.getByRole("radio", { name: "List" }));
    expect(screen.getByText("19:30")).toBeInTheDocument();
    expect(screen.queryByText(/7:30\s?pm/i)).toBeNull();
  });
});

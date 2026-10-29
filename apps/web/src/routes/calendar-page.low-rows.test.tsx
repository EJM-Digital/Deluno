import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

function localToday(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

const revalidate = vi.fn();
const movie = (id: string, title: string, wantedStatus = "missing") => ({
  movieId: id, title, releaseYear: 2026, posterUrl: null, kind: "digital" as const, date: localToday(), monitored: true, wantedStatus
});

const loaderData = {
  episodes: [] as unknown[],
  movies: [movie("m1", "First Film"), movie("m2", "Second Film"), movie("m3", "Third Film"), movie("m4", "Fourth Film"), movie("m5", "Fifth Film"), movie("m6", "Short Of Cutoff", "upgrade")]
};

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

const { CalendarPage } = await import("./calendar-page");

beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
});

function renderMonth() {
  render(<MemoryRouter><CalendarPage /></MemoryRouter>);
}

describe("Schedule month grid (low rows)", () => {
  it("a day with more than three entries shows three chips and '+N more'", () => {
    renderMonth();

    expect(screen.getByText("+3 more")).toBeInTheDocument();
  });

  it("'Highlight titles below cutoff' rings an entry short of its quality, and only while the switch is on", () => {
    loaderData.movies = [movie("m1", "Fine Film"), movie("m6", "Short Of Cutoff", "upgrade")];
    renderMonth();
    const ringed = () => Array.from(document.querySelectorAll("button")).filter((button) => button.className.includes("ring-warning/70"));
    expect(ringed()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: /Options/ }));
    fireEvent.click(screen.getByRole("switch", { name: /Highlight titles below cutoff/ }));

    expect(ringed()).toHaveLength(1);
    expect(within(ringed()[0]!).getByText(/Short Of Cutoff/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("switch", { name: /Highlight titles below cutoff/ }));
    expect(ringed()).toHaveLength(0);
  });
});

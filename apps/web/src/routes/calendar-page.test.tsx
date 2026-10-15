import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

// A release date is a calendar date in this computer's time zone. Built from UTC it
// is yesterday for the first hours of a day east of Greenwich, and on the 1st of a
// month the films fell outside the month shown.
function localToday(): string {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

/**
 * #786: Day and Agenda join Week and Month, an options control toggles
 * cutoff-unmet highlighting and which movie release types show, and the
 * calendar link's feed URL carries the unmonitored/tag query options.
 */

const revalidate = vi.fn();

const loaderData = {
  episodes: [
    {
      episodeId: "e1",
      seriesId: "s1",
      seriesTitle: "Fringe Show",
      posterUrl: null,
      seasonNumber: 1,
      episodeNumber: 2,
      title: "Pilot",
      airDateUtc: new Date().toISOString(),
      monitored: true,
      wantedStatus: "upgrade"
    }
  ],
  movies: [
    {
      movieId: "m1",
      title: "In Cinemas Movie",
      releaseYear: 2026,
      posterUrl: null,
      kind: "inCinemas" as const,
      date: localToday(),
      monitored: true,
      wantedStatus: "missing"
    },
    {
      movieId: "m2",
      title: "Digital Movie",
      releaseYear: 2026,
      posterUrl: null,
      kind: "digital" as const,
      date: localToday(),
      monitored: true,
      wantedStatus: "missing"
    }
  ]
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

function renderPage() {
  return render(
    <MemoryRouter>
      <CalendarPage />
    </MemoryRouter>
  );
}

// Schedule remembers its choices between visits, so each test starts from a browser that has kept nothing.
beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
});

describe("Schedule remembers what you chose", () => {
  it("opens on the range, view and stepped-to period you left it on", () => {
    const first = renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "Week" }));
    fireEvent.click(screen.getByRole("radio", { name: "List" }));
    fireEvent.click(screen.getByRole("button", { name: "Next week" }));
    const label = screen.getAllByRole("heading", { level: 2 })[0].textContent;
    first.unmount();

    renderPage();
    expect(screen.getByRole("radio", { name: "Week" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "List" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getAllByRole("heading", { level: 2 })[0].textContent).toBe(label);
  });
});

describe("Calendar scopes (#786)", () => {
  it("offers Day and Agenda alongside Week and Month", () => {
    renderPage();

    expect(screen.getByRole("radio", { name: "Day" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Week" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Month" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Agenda" })).toBeInTheDocument();
  });

  it("hides the Calendar/List view toggle once Day is picked, since a day has no grid shape", () => {
    renderPage();

    fireEvent.click(screen.getByRole("radio", { name: "Day" }));

    expect(screen.queryByRole("radio", { name: "Calendar" })).not.toBeInTheDocument();
    // The list format still renders — Day just has no grid alternative.
    expect(screen.getByText("Fringe Show")).toBeInTheDocument();
  });
});

describe("Calendar options drawer (#786)", () => {
  it("opens from the toolbar and offers the release-type and highlight switches", () => {
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: /Options/ }));

    expect(screen.getByRole("switch", { name: /Highlight titles below cutoff/ })).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "In cinemas" })).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Digital" })).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Physical" })).toBeInTheDocument();
  });

  it("hides a movie release type once its switch is turned off", () => {
    renderPage();
    // Month view, grid form, both movies visible by default.
    fireEvent.click(screen.getByRole("radio", { name: "List" }));
    expect(screen.getByText("In Cinemas Movie")).toBeInTheDocument();
    expect(screen.getByText("Digital Movie")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Options/ }));
    fireEvent.click(screen.getByRole("switch", { name: "Digital" }));

    expect(screen.getByText("In Cinemas Movie")).toBeInTheDocument();
    expect(screen.queryByText("Digital Movie")).not.toBeInTheDocument();
  });
});

describe("Calendar release-type switches hide only their own type", () => {
  it("hides in-cinemas releases when that switch is off and keeps the digital one", () => {
    renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "List" }));
    fireEvent.click(screen.getByRole("button", { name: /Options/ }));
    fireEvent.click(screen.getByRole("switch", { name: "In cinemas" }));

    expect(screen.queryByText("In Cinemas Movie")).not.toBeInTheDocument();
    expect(screen.getByText("Digital Movie")).toBeInTheDocument();
  });

  it("hides a physical release when the Physical switch is off, and shows it again when turned back on", () => {
    loaderData.movies.push({
      movieId: "m3",
      title: "Disc Movie",
      releaseYear: 2026,
      posterUrl: null,
      kind: "physical" as never,
      date: localToday(),
      monitored: true,
      wantedStatus: "missing"
    });
    try {
      renderPage();
      fireEvent.click(screen.getByRole("radio", { name: "List" }));
      expect(screen.getByText("Disc Movie")).toBeInTheDocument();

      fireEvent.click(screen.getByRole("button", { name: /Options/ }));
      fireEvent.click(screen.getByRole("switch", { name: "Physical" }));
      expect(screen.queryByText("Disc Movie")).not.toBeInTheDocument();
      expect(screen.getByText("Digital Movie")).toBeInTheDocument();

      fireEvent.click(screen.getByRole("switch", { name: "Physical" }));
      expect(screen.getByText("Disc Movie")).toBeInTheDocument();
    } finally {
      loaderData.movies.pop();
    }
  });
});

describe("Calendar episode marks (#1179)", () => {
  it("reads an unaired episode as Airs <date> in the list, not Missing", () => {
    const later = { ...loaderData.episodes[0], episodeId: "e2", seriesTitle: "Later Show", wantedStatus: "missing", airDateUtc: new Date(Date.now() + 3 * 86_400_000).toISOString() };
    loaderData.episodes.push(later);
    try {
      renderPage();
      fireEvent.click(screen.getByRole("radio", { name: "Agenda" }));
      const row = screen.getByText("Later Show").closest("[role='row'], tr, li, div")!.parentElement!;
      expect(row.textContent).toMatch(/Airs /);
      expect(row.textContent).not.toMatch(/Missing/);
    } finally {
      loaderData.episodes.pop();
    }
  });

  it("draws an unmonitored episode's chip differently from a monitored one", () => {
    const off = { ...loaderData.episodes[0], episodeId: "e3", seriesTitle: "Unwatched Show", monitored: false };
    loaderData.episodes.push(off);
    const movies = loaderData.movies.splice(0);
    try {
      renderPage();
      fireEvent.click(screen.getByRole("radio", { name: "Month" }));
      const chip = screen.getByRole("button", { name: /Unwatched Show/ });
      const watched = screen.getByRole("button", { name: /Fringe Show/ });
      expect(chip.className).toContain("border-dashed");
      expect(watched.className).not.toContain("border-dashed");
    } finally {
      loaderData.episodes.pop();
      loaderData.movies.push(...movies);
    }
  });
});

import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

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
      date: new Date().toISOString().slice(0, 10),
      monitored: true,
      wantedStatus: "missing"
    },
    {
      movieId: "m2",
      title: "Digital Movie",
      releaseYear: 2026,
      posterUrl: null,
      kind: "digital" as const,
      date: new Date().toISOString().slice(0, 10),
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

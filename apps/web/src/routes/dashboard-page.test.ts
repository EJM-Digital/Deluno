import { describe, expect, it } from "vitest";
import type { MediaItem } from "../lib/media-types";
import { dashboardShouldRevalidate, newestDashboardItems } from "./dashboard-page";

function item(id: string, type: MediaItem["type"], addedUtc: string): MediaItem {
  return {
    id,
    title: id,
    year: null,
    type,
    poster: null,
    backdrop: null,
    quality: null,
    monitored: true,
    sizeGb: null,
    rating: null,
    genres: [],
    added: id,
    addedUtc,
    overview: ""
  };
}

describe("dashboard recently added", () => {
  it("merges movies and TV before taking the newest limit", () => {
    const movies = [item("old movie", "movie", "2026-08-01T00:00:00Z")];
    const shows = [
      item("new show", "show", "2026-08-20T00:00:00Z"),
      item("mid show", "show", "2026-08-10T00:00:00Z")
    ];

    expect(newestDashboardItems(movies, shows, 2).map((entry) => entry.id))
      .toEqual(["new show", "mid show"]);
  });

  it("keeps source order when an added timestamp is unavailable or tied", () => {
    const movies = [item("first", "movie", ""), item("second", "movie", "")];

    expect(newestDashboardItems(movies, [], 10).map((entry) => entry.id))
      .toEqual(["first", "second"]);
  });
});

describe("dashboardShouldRevalidate", () => {
  const here = new URL("http://deluno.local/");

  it("does not re-run the loader for a same-address revalidation, such as returning to a hidden tab", () => {
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: here, defaultShouldRevalidate: true })).toBe(false);
  });

  it("still revalidates after a form submission", () => {
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: here, formMethod: "POST", defaultShouldRevalidate: true })).toBe(true);
  });

  it("defers to React Router when the address changes", () => {
    const next = new URL("http://deluno.local/?range=7");
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: next, defaultShouldRevalidate: true })).toBe(true);
    expect(dashboardShouldRevalidate({ currentUrl: here, nextUrl: next, defaultShouldRevalidate: false })).toBe(false);
  });
});

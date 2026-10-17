import { act, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The Wanted subtitles screen reads again when a movie or an episode changes, or the subtitles area of Settings changes,
 * with no reload and no timer (#1179, #1282): a real router, the real page, and the server's pushes sent by hand.
 */

const pushed = vi.hoisted(() => ({ events: [] as string[], handler: null as null | ((name: string, payload: unknown) => void) }));
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalREvents: (events: readonly string[], _subjects: unknown, handler: (name: string, payload: unknown) => void) => {
    pushed.events = [...events];
    pushed.handler = handler;
  }
}));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const { SubtitleWantedPage } = await import("./subtitle-wanted-screen");

const row = (title: string) => ({
  kind: "Movie", mediaId: `m-${title}`, libraryId: "lib", libraryName: "Movies", title, year: 2021, seasonNumber: null, episodeNumber: null,
  episodeTitle: null, languagesMissing: ["en"], lastAttempt: null
});

let reads = 0;
let wanted = [row("Dune")];

function renderScreen() {
  const router = createMemoryRouter([{ path: "/", element: <SubtitleWantedPage />, loader: () => { reads += 1; return { wanted }; } }]);
  return render(<RouterProvider router={router} />);
}

beforeEach(() => {
  reads = 0;
  wanted = [row("Dune")];
  pushed.events = [];
  pushed.handler = null;
});

describe("Wanted subtitles reading again", () => {
  it("listens for a movie or an episode changing and for the subtitles area of Settings", async () => {
    renderScreen();
    await screen.findByText("Dune");

    expect(pushed.events).toEqual(expect.arrayContaining(["MovieChanged", "EpisodeChanged", "ConfigChanged"]));
  });

  it("shows a movie added elsewhere that asks for a language it lacks, without a reload, and reads once for a burst", async () => {
    renderScreen();
    await screen.findByText("Dune");
    const before = reads;

    wanted = [row("Dune"), row("Arrival")];
    act(() => {
      pushed.handler!("MovieChanged", { id: "m-Arrival" });
      pushed.handler!("EpisodeChanged", { id: "e1" });
      pushed.handler!("MovieChanged", { id: "m-Arrival" });
    });

    expect(await screen.findByText("Arrival", {}, { timeout: 4_000 })).toBeInTheDocument();
    expect(reads).toBe(before + 1);
  });

  it("reads again when the subtitles settings change, and ignores a change to another area", async () => {
    renderScreen();
    await screen.findByText("Dune");
    const before = reads;

    act(() => { pushed.handler!("ConfigChanged", { area: "naming" }); });
    await new Promise((resolve) => setTimeout(resolve, 700)); // reason: a settle window to prove no read follows
    expect(reads).toBe(before);

    wanted = [];
    act(() => { pushed.handler!("ConfigChanged", { area: "subtitles" }); });
    await waitFor(() => expect(reads).toBe(before + 1), { timeout: 4_000 });
    await waitFor(() => expect(screen.queryByText("Dune")).toBeNull());
  });
});

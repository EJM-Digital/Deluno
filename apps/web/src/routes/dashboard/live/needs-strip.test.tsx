import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import type { JourneyCard } from "./journey";
import { needsGroups, NeedsStrip } from "./needs-strip";
import { NO_TITLES, type TitleLookup } from "./titles";

const card = (key: string, title: string, extra: Partial<JourneyCard> = {}): JourneyCard => ({ key, stage: "needs", title, release: `${title}.1080p`, state: { tone: "bad", label: "Stuck" }, reason: "Torrent data missing or broken.", to: "/queue", ...extra });
const stuck = (key: string, title: string) => card(key, title, { canSearchAgain: true, queueRef: { clientId: "c1", itemId: key } });
const review = (key: string, title: string) => card(key, title, { state: { tone: "warn", label: "Import needs a look" }, recovery: { id: key, mediaType: "series", title, failureKind: "importFailed", summary: "x", recommendedAction: "review", detailsJson: null, detectedUtc: "2026-10-01T00:00:00Z" } });
const handoff = (key: string, title: string) => card(key, title, { failedHandoffId: key, state: { tone: "bad", label: "Processor failed" } });

describe("Needs you groups", () => {
  it("groups by kind and by show, most urgent first", () => {
    const cards = [
      stuck("a", "Film One (2001)"), stuck("b", "Film Two (2002)"), stuck("c", "Film Three (2003)"),
      ...Array.from({ length: 9 }, (_, n) => review(`r${n}`, `Pioneer One S01E0${n + 1}`)),
      handoff("h1", "Metropolis (1927)"), handoff("h2", "Nosferatu (1922)")
    ];
    expect(needsGroups(cards, NO_TITLES).map((group) => group.label)).toEqual(["2 hand-offs refused", "3 stuck downloads", "Pioneer One: 9 episodes waiting to import"]);
  });

  it("names a show from the library when it has more than one problem, and leaves a single one in its kind", () => {
    const titles: TitleLookup = { movies: new Map(), series: new Map([["s1", { title: "Lioness" }]]), episodes: new Map([["e1", { seriesId: "s1", season: 3, episode: 1 }], ["e2", { seriesId: "s1", season: 3, episode: 2 }]]) };
    const cards = [stuck("a", "x"), stuck("b", "y"), stuck("c", "A Film (2000)")].map((item, index) => (index < 2 ? { ...item, entity: { kind: "episode" as const, id: `e${index + 1}` } } : item));
    expect(needsGroups(cards, titles).map((group) => group.label)).toEqual(["Lioness: 2 episodes stuck", "1 stuck download"]);
  });
});

describe("the Needs you strip", () => {
  const action = { busy: null, run: async () => {} };
  const view = (cards: JourneyCard[], total = cards.length) => render(<MemoryRouter><NeedsStrip cards={cards} total={total} titles={NO_TITLES} action={action} /></MemoryRouter>);

  it("says so plainly when nothing needs you", () => {
    view([]);
    expect(screen.getByText("Nothing needs you.")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("is one strip with a group and one Review button each, never a card per problem", () => {
    view([stuck("a", "Film One (2001)"), stuck("b", "Film Two (2002)"), handoff("h1", "Metropolis (1927)")]);
    const strip = screen.getByRole("region", { name: "Needs you" });
    expect(within(strip).getByText("3")).toBeInTheDocument();
    expect(within(strip).getByText("1 hand-off refused")).toBeInTheDocument();
    expect(within(strip).getByText("2 stuck downloads")).toBeInTheDocument();
    const buttons = within(strip).getAllByRole("button", { name: /^Review/ });
    expect(buttons).toHaveLength(2);
    expect(new Set(buttons.map((button) => button.className)).size).toBe(1);
    // The problems themselves are not here until a group is opened.
    expect(within(strip).queryByText("Film One (2001)")).toBeNull();
  });

  it("opens a drawer that lists the items with their own side-by-side buttons", async () => {
    const user = userEvent.setup();
    view([stuck("a", "Film One (2001)"), stuck("b", "Film Two (2002)")]);
    await user.click(screen.getByRole("button", { name: "Review 2 stuck downloads" }));
    const drawer = await screen.findByRole("dialog");
    expect(within(drawer).getByText("Film One (2001)")).toBeInTheDocument();
    expect(within(drawer).getByRole("button", { name: "Search again for Film One (2001)" })).toBeInTheDocument();
    expect(within(drawer).getByRole("button", { name: "Search again for Film Two (2002)" })).toBeInTheDocument();
  });

  it("says how many more problems the lists did not bring, and links to Transfers for them", () => {
    view([stuck("a", "Film One (2001)")], 5);
    expect(screen.getByText("4 more in Transfers")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open Transfers for the rest" })).toHaveAttribute("href", "/queue");
  });
});

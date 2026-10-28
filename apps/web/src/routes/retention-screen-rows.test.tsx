import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Retention: the Leaving soon table, the rules list and the rule drawer's trial and history, for the rows a fresh server cannot
 * show (marked titles, several rules, run history, a server that cannot be asked). The page is the real one; the answers are the
 * ones the retention routes give.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const field = (id: string, label: string, valueKind: string, operators: string[], group = "watching") => ({
  id, label, hint: "", group, valueKind, operators, options: null, source: "server", unsupportedOn: [] as string[]
});
const movieFields = [
  field("server.watchedBy", "Watched by", "users", ["nobody", "anyone"], "server"),
  field("server.lastViewed", "Last viewed", "date", ["beyond", "within"], "server")
];
const rule = (overrides: Record<string, unknown> = {}) => ({
  id: "rule-1", name: "Unwatched films", level: "movie", isEnabled: true,
  steps: [{ type: "condition", op: "keep", condition: "server.watchedBy:nobody:" }],
  mediaServerIds: [], removeFromServers: true, waitDays: 14, removalMode: "automatic", checkIntervalHours: 8,
  lastCheckedUtc: null, nextCheckUtc: null, expiryAction: "delete", expiryTag: null, keepInDelunoOnly: false, deferWhilePlaying: true, ...overrides
});
const mark = (overrides: Record<string, unknown> = {}) => ({
  ruleId: "rule-1", ruleName: "Unwatched films", entityId: "movie-9", level: "movie", showId: null, title: "Dementia 13", year: 1963,
  sizeBytes: 1_500_000_000, reasons: ["Watched by nobody"], firstMatchedUtc: "2026-10-01T00:00:00Z", removeAfterUtc: "2099-10-15T00:00:00Z",
  state: "leaving", stateReason: null, manual: false, ...overrides
});
const loaderData = {
  rules: [] as ReturnType<typeof rule>[], servers: [] as Array<Record<string, unknown>>, marks: [] as ReturnType<typeof mark>[], keeps: [] as unknown[],
  fields: { movie: movieFields, show: movieFields, season: movieFields, episode: movieFields }
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
}));
const { RetentionPage } = await import("./retention-screen");

const reply = (body: unknown = {}) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
const renderPage = () => render(<MemoryRouter><RetentionPage /></MemoryRouter>);
const trialOf = (over: Record<string, unknown> = {}) => ({ considered: 40, stepCounts: [{ count: 3 }], matchedCount: 3, matchedBytes: 1000, undecidedCount: 0, unreachableServers: [], problems: [], unmanaged: [], expiryAction: "delete", expiryDescription: "it is removed", ...over });
const orderOf = (names: string[]) => screen.getAllByRole("row").map((row) => names.find((name) => row.textContent?.startsWith(name))).filter(Boolean);
async function sortBy(listLabel: RegExp, column: string, times = 1) {
  const header = screen.getAllByRole("columnheader").find((candidate) => listLabel.test(candidate.textContent ?? "") && candidate.textContent === column)!;
  for (let i = 0; i < times; i += 1) await userEvent.click(within(header).getByRole("button"));
}

beforeEach(() => {
  authedFetchMock.mockReset();
  window.localStorage.clear();
  loaderData.rules = []; loaderData.servers = []; loaderData.marks = []; loaderData.keeps = [];
  authedFetchMock.mockImplementation((path: string) => reply(String(path).includes("/trial") ? trialOf() : []));
});
afterEach(() => vi.useRealTimers());

describe("the Leaving soon card", () => {
  it("counts the titles with their size, and reads each row: title with the year for a film, level and rule, first reason and how many more, the state with its date and the size", () => {
    loaderData.rules = [rule(), rule({ id: "rule-2", name: "Tidy shows", level: "show", expiryAction: "log" })];
    loaderData.marks = [
      mark({ reasons: ["Watched by nobody", "Last viewed over 90 days ago", "Never played"] }),
      mark({ entityId: "show-1", level: "show", ruleId: "rule-2", ruleName: "Tidy shows", title: "Old Show", year: null, sizeBytes: 500_000_000, state: "ready", reasons: ["Watched by nobody"] }),
      mark({ entityId: "movie-3", title: "Added Film", year: 1999, manual: true, sizeBytes: 0, state: "held", stateReason: "Someone is watching it", reasons: ["Added by hand"] }),
      mark({ entityId: "movie-4", title: "Gone Film", year: 2001, state: "done", stateReason: "Removed on the day", sizeBytes: 100_000_000 })
    ];
    renderPage();
    expect(screen.getByText("4 titles · 2.0 GB")).toBeInTheDocument();
    const rowOf = (title: string) => screen.getAllByRole("row").find((row) => row.textContent?.startsWith(title))!;

    expect(rowOf("Dementia 13 (1963)").textContent).toContain("Movies · Unwatched films");
    expect(rowOf("Dementia 13 (1963)").textContent).toContain("Watched by nobody");
    expect(rowOf("Dementia 13 (1963)").textContent).toContain("and 2 more");
    expect(rowOf("Dementia 13 (1963)").textContent).toContain("Leaving soon");
    expect(rowOf("Dementia 13 (1963)").textContent).toContain("1.4 GB");
    expect(rowOf("Old Show").textContent).not.toContain("(");
    expect(rowOf("Old Show").textContent).toContain("Ready");
    expect(rowOf("Old Show").textContent).not.toContain("Ready to remove");
    expect(rowOf("Added Film (1999)").textContent).toContain("Manual");
    expect(rowOf("Added Film (1999)").textContent).toContain("added by hand");
    expect(rowOf("Added Film (1999)").textContent).toContain("Held");
    expect(rowOf("Added Film (1999)").textContent).toContain("Someone is watching it");
    expect(rowOf("Gone Film (2001)").textContent).toContain("Done");
    expect(rowOf("Gone Film (2001)").textContent).toContain("Removed on the day");
  });

  it("the count reads 1 title for one, and a title whose rule removes things reads Ready to remove", () => {
    loaderData.rules = [rule()];
    loaderData.marks = [mark({ state: "ready", sizeBytes: 0 })];
    renderPage();
    expect(screen.getByText("1 title")).toBeInTheDocument();
    expect(screen.getByRole("row", { name: /Dementia 13/ }).textContent).toContain("Ready to remove");
  });

  it("the table sorts by title, why, goes and size", async () => {
    loaderData.rules = [rule()];
    loaderData.marks = [
      mark({ entityId: "a", title: "Charlie", reasons: ["Aaa reason"], removeAfterUtc: "2099-10-10T00:00:00Z", sizeBytes: 300 }),
      mark({ entityId: "b", title: "Alpha", reasons: ["Ccc reason"], removeAfterUtc: "2099-10-12T00:00:00Z", sizeBytes: 100 }),
      mark({ entityId: "c", title: "Bravo", reasons: ["Bbb reason"], removeAfterUtc: "2099-10-11T00:00:00Z", sizeBytes: 200 })
    ];
    renderPage();
    const names = ["Alpha", "Bravo", "Charlie"];
    const inTable = () => orderOf(names);
    await sortBy(/./, "Title");
    expect(inTable()).toEqual(["Alpha", "Bravo", "Charlie"]);
    await sortBy(/./, "Why");
    expect(inTable()).toEqual(["Charlie", "Bravo", "Alpha"]); // Aaa, Bbb, Ccc
    await sortBy(/./, "Goes");
    expect(inTable()).toEqual(["Charlie", "Bravo", "Alpha"]); // 10, 11, 12 Oct
    await sortBy(/./, "Goes");
    expect(inTable()).toEqual(["Alpha", "Bravo", "Charlie"]);
    await sortBy(/./, "Size");
    expect(inTable()).toEqual(["Alpha", "Bravo", "Charlie"]); // 100, 200, 300
  });
});

describe("the rules list", () => {
  it("each row reads the name with its level and the servers it reads, the first step and how many follow, how long it waits and what then happens", () => {
    loaderData.servers = [{ id: "srv-1", name: "Living room", kind: "plex", baseUrl: "http://plex.example:32400", serverName: "Plex" }];
    loaderData.rules = [
      rule({ mediaServerIds: ["srv-1", "gone-1"], steps: [{ type: "condition", op: "keep", condition: "server.watchedBy:nobody:" }, { type: "condition", op: "keep", condition: "server.lastViewed:beyond:90" }, { type: "condition", op: "keep", condition: "server.lastViewed:within:5" }] }),
      rule({ id: "rule-2", name: "Quick shows", level: "show", waitDays: 1, removalMode: "ask", expiryAction: "unmonitor", steps: [{ type: "block", name: "", steps: [] }] }),
      rule({ id: "rule-3", name: "Tag them", level: "movie", waitDays: 7, expiryAction: "tag", expiryTag: "old", steps: [{ type: "condition", op: "keep", condition: "server.watchedBy:nobody:" }, { type: "condition", op: "keep", condition: "server.lastViewed:beyond:90" }] }),
      rule({ id: "rule-4", name: "Log only", waitDays: 30, expiryAction: "log" })
    ];
    renderPage();
    const rowOf = (name: string) => screen.getAllByRole("row").find((row) => row.textContent?.startsWith(name))!;
    expect(screen.getByText("4 of 4 on")).toBeInTheDocument();

    expect(rowOf("Unwatched films").textContent).toContain("Movies · Living room, Removed server");
    expect(rowOf("Unwatched films").textContent).toContain("and 2 more steps");
    expect(rowOf("Unwatched films").textContent).toContain("Waits 14 days");
    expect(rowOf("Unwatched films").textContent).toContain("Then removes it");
    expect(rowOf("Quick shows").textContent).toContain("Waits 1 day");
    expect(rowOf("Quick shows").textContent).toContain("Then asks, and stops monitoring");
    expect(rowOf("Quick shows").textContent).toContain("Block 1");
    expect(rowOf("Tag them").textContent).toContain("and 1 more step");
    expect(rowOf("Tag them").textContent).not.toContain("and 1 more steps");
    expect(rowOf("Tag them").textContent).toContain("Then tags it “old”");
    expect(rowOf("Log only").textContent).toContain("Then only writes it down");
  });

  it("the columns sort by rule, selects, waits, next check and on", async () => {
    loaderData.rules = [
      rule({ id: "r1", name: "Charlie", waitDays: 5, isEnabled: true, nextCheckUtc: "2099-01-03T00:00:00Z", steps: [{ type: "condition", op: "keep", condition: "server.watchedBy:nobody:" }] }),
      rule({ id: "r2", name: "Alpha", waitDays: 30, isEnabled: false, nextCheckUtc: "2099-01-01T00:00:00Z", steps: [{ type: "condition", op: "keep", condition: "server.lastViewed:beyond:90" }] }),
      rule({ id: "r3", name: "Bravo", waitDays: 10, isEnabled: true, nextCheckUtc: "2099-01-02T00:00:00Z", steps: [{ type: "condition", op: "keep", condition: "server.lastViewed:within:5" }] })
    ];
    renderPage();
    const names = ["Alpha", "Bravo", "Charlie"];
    await sortBy(/./, "Rule");
    expect(orderOf(names)).toEqual(["Alpha", "Bravo", "Charlie"]);
    await sortBy(/./, "When it matches");
    expect(orderOf(names)).toEqual(["Charlie", "Bravo", "Alpha"]); // 5, 10, 30 days
    await sortBy(/./, "Next check");
    // 2 Jan, 3 Jan, and the rule that is off has no next check so it comes last.
    expect(orderOf(names)).toEqual(["Bravo", "Charlie", "Alpha"]);
    await sortBy(/./, "On");
    expect(orderOf(names)[0]).toBe("Alpha"); // off before on
    await sortBy(/./, "Selects");
    // By the first step's words: "Last viewed over 90 days ago", "Last viewed within 5 days", "Watched by nobody".
    expect(orderOf(names)).toEqual(["Alpha", "Bravo", "Charlie"]);
  });
});

describe("the rule drawer", () => {
  it("lists the last runs labelled Confirmed, Scheduled, Run now or Earlier version, each with its summary", async () => {
    loaderData.rules = [rule()];
    authedFetchMock.mockImplementation((path: string) => {
      if (String(path).includes("/runs")) {
        return reply([
          { id: "1", ruleId: "rule-1", kind: "confirmed", trigger: "manual", startedUtc: "2026-10-05T10:00:00Z", summary: "Removed 2 titles you confirmed" },
          { id: "2", ruleId: "rule-1", kind: "check", trigger: "schedule", startedUtc: "2026-10-04T10:00:00Z", summary: "3 marked, 1 removed" },
          { id: "3", ruleId: "rule-1", kind: "check", trigger: "manual", startedUtc: "2026-10-03T10:00:00Z", summary: "4 would be marked" },
          { id: "4", ruleId: "rule-1", kind: "edit", trigger: "manual", startedUtc: "2026-10-02T10:00:00Z", summary: "Marks from an earlier version cleared" }
        ]);
      }
      return reply(String(path).includes("/trial") ? trialOf() : []);
    });
    renderPage();
    fireEvent.click(screen.getByText("Unwatched films"));
    const drawer = within(await screen.findByRole("dialog"));
    expect(await drawer.findByText("Removed 2 titles you confirmed")).toBeInTheDocument();
    const labels = ["Confirmed", "Scheduled", "Run now", "Earlier version"];
    const summaries = ["Removed 2 titles you confirmed", "3 marked, 1 removed", "4 would be marked", "Marks from an earlier version cleared"];
    labels.forEach((label, index) => {
      const term = drawer.getByText(summaries[index]).previousElementSibling as HTMLElement;
      expect(term.textContent).toContain(label);
    });
    expect(authedFetchMock).toHaveBeenCalledWith("/api/retention/rules/rule-1/runs?take=10", { cache: "no-store" });
  });

  it("the trial says what would be selected in a warning list: problems, a server that cannot be asked, titles that could not be decided, and the titles on the servers but not in Deluno", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(String(path).includes("/trial") ? trialOf({
      problems: ["Step 2 needs a number of days."], unreachableServers: ["Living room could not be reached"], undecidedCount: 2,
      unmanaged: [{ server: "Living room", title: "Strange Film", year: 1988 }, { server: "Living room", title: "No Year", year: null }]
    }) : []));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    const problems = await drawer.findByRole("alert", {}, { timeout: 3_000 });
    expect(problems.textContent).toContain("Step 2 needs a number of days.");
    expect(drawer.getByText("Nothing would be selected: Living room could not be reached.")).toBeInTheDocument();
    expect(drawer.getByText("2 titles could not be decided — a server could not answer a step — and would be left alone.")).toBeInTheDocument();
    expect(drawer.getByText("2 on your servers but not in Deluno. They are never touched.")).toBeInTheDocument();
    const bring = drawer.getAllByRole("link", { name: "Bring it in" });
    expect(bring[0]).toHaveAttribute("href", "/movies?add=true&title=Strange%20Film%201988");
    expect(bring[1]).toHaveAttribute("href", "/movies?add=true&title=No%20Year");
  });

  it("says a single undecided title in the singular, and what the matches will do on their day", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(String(path).includes("/trial") ? trialOf({ undecidedCount: 1, matchedCount: 1, expiryDescription: "it is removed" }) : []));
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));
    expect(await drawer.findByText("1 title could not be decided — a server could not answer a step — and would be left alone.", {}, { timeout: 3_000 })).toBeInTheDocument();
    expect(drawer.getByText("This title, on its day: it is removed")).toBeInTheDocument();
  });

  it("with servers connected, the two switches say that an unreachable server holds everything back, and that the list can stay in Deluno only", async () => {
    loaderData.servers = [{ id: "srv-1", name: "Living room", kind: "plex", baseUrl: "http://plex.example:32400", serverName: "Plex" }];
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("Tell these servers when a title goes")).toBeInTheDocument();
    expect(drawer.getByText("If any of them cannot be asked when a check runs, nothing is marked or removed until they can.")).toBeInTheDocument();
    expect(drawer.getByText("Keep the list in Deluno only")).toBeInTheDocument();
    expect(drawer.getByText("Nothing is created on these servers. The list stays here for review, and keeping, postponing and the day's action work as usual.")).toBeInTheDocument();
    expect(drawer.getByText("Reports itself as Plex")).toBeInTheDocument();
  });
});

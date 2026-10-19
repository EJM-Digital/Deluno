import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Retention (Automation and Recovery): the rules page. A rule is drawn in a drawer, saved with its own footer, and what it
 * marks waits in Leaving soon where anyone can keep it, postpone it or add a title by hand. Every request below is the one the
 * server's retention routes are tested against (RetentionRoutesDoWhatTheySayTests, RetentionRemovalOnRealFilesTests).
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const unsavedMock = vi.fn();
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: (dirty: boolean) => unsavedMock(dirty) }));

const revalidateMock = vi.fn();

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
  lastCheckedUtc: null, nextCheckUtc: null, expiryAction: "log", expiryTag: null, keepInDelunoOnly: false, deferWhilePlaying: true,
  ...overrides
});

const mark = (overrides: Record<string, unknown> = {}) => ({
  ruleId: "rule-1", ruleName: "Unwatched films", entityId: "movie-9", level: "movie", showId: null, title: "Dementia 13", year: 1963,
  sizeBytes: 1_500_000_000, reasons: ["Watched by nobody"], firstMatchedUtc: "2026-10-01T00:00:00Z", removeAfterUtc: "2099-10-15T00:00:00Z",
  state: "leaving", stateReason: null, manual: false,
  ...overrides
});

const loaderData = {
  rules: [] as ReturnType<typeof rule>[],
  servers: [] as Array<Record<string, unknown>>,
  marks: [] as ReturnType<typeof mark>[],
  keeps: [] as unknown[],
  fields: { movie: movieFields, show: movieFields, season: movieFields, episode: movieFields }
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { RetentionPage } = await import("./retention-screen");

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({
    ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body))
  } as unknown as Response);
}

function render_() {
  return render(
    <MemoryRouter>
      <RetentionPage />
    </MemoryRouter>
  );
}

/** A switch in a labelled row: its label wraps it, so it is found through the label's own text. */
function switchOf(scope: ReturnType<typeof within>, label: string) {
  const row = scope.getAllByText(label).map((element: HTMLElement) => element.closest("label")).find((candidate: HTMLLabelElement | null) => candidate !== null);
  const found = (row?.htmlFor ? document.getElementById(row.htmlFor) : row?.querySelector("[role=switch]")) as HTMLElement | null | undefined;
  if (!found) throw new Error(`No switch labelled ${label}`);
  return found;
}

const optionsOf = (select: HTMLElement) => Array.from((select as HTMLSelectElement).options).map((option) => option.textContent);

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);

beforeEach(() => {
  authedFetchMock.mockReset();
  revalidateMock.mockReset();
  unsavedMock.mockReset();
  loaderData.rules = [];
  loaderData.servers = [];
  loaderData.marks = [];
  loaderData.keeps = [];
  authedFetchMock.mockImplementation((path: string) => reply(String(path).includes("/trial") ? { considered: 120, stepCounts: [{ count: 7 }], matchedCount: 7, matchedBytes: 2_000_000_000, undecidedCount: 0, unreachableServers: [], problems: [], unmanaged: [], expiryAction: "log", expiryDescription: "" } : []));
});

afterEach(() => {
  vi.useRealTimers();
});

describe("New rule", () => {
  it("opens the drawer titled New retention rule with the gentle starting choices", async () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: "New rule" }));

    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("New retention rule")).toBeVisible();
    expect(drawer.getByLabelText("Name")).toHaveValue("");
    expect(drawer.getByLabelText("Cleans out")).toHaveValue("movie");
    expect(switchOf(drawer, "Enabled")).toHaveAttribute("aria-checked", "true");
    // Two starter steps: watched by anyone, and last viewed beyond 90 days.
    expect(drawer.getByDisplayValue("90")).toBeVisible();
    expect(drawer.getAllByText("Watched by").length).toBeGreaterThan(0);
    expect(drawer.getByLabelText("Leaving soon for")).toHaveValue(14);
    expect(drawer.getByLabelText("Check every")).toHaveValue("8");
    expect(drawer.getByLabelText("What happens")).toHaveValue("log");
    expect(drawer.getByRole("radio", { name: "Do it by itself" })).toHaveAttribute("aria-checked", "true");
    expect(switchOf(drawer, "Never remove something while it plays")).toHaveAttribute("aria-checked", "true");
    expect(drawer.getByRole("button", { name: "Add rule" })).toBeVisible();
  });

  it("offers every kind of title, nine check intervals and the four things a rule can do on the day", async () => {
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    expect(optionsOf(drawer.getByLabelText("Cleans out"))).toEqual(["Movies", "Whole shows", "Seasons", "Episodes"]);
    expect(optionsOf(drawer.getByLabelText("Check every"))).toHaveLength(9);
    expect(optionsOf(drawer.getByLabelText("What happens"))).toEqual([
      "Remove it (files to the recycle bin)", "Stop monitoring it (files stay)", "Only write it down in Activity", "Give it a tag"
    ]);
    expect(drawer.getAllByRole("radio").map((radio) => radio.textContent)).toEqual(["Do it by itself", "Ask me first"]);
  });

  it("Add rule posts the whole rule, says Rule added and stays open on the saved rule", async () => {
    const saved = rule({ id: "rule-new", name: "Old films" });
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/retention/rules" ? saved : path.includes("/trial") ? { considered: 0, stepCounts: [], matchedCount: 0, matchedBytes: 0, undecidedCount: 0, unreachableServers: [], problems: [], unmanaged: [], expiryAction: "log", expiryDescription: "" } : []));
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Old films" } });
    fireEvent.change(drawer.getByLabelText("Cleans out"), { target: { value: "show" } });
    fireEvent.click(drawer.getByRole("button", { name: "Add rule" }));

    await waitFor(() => expect(calls("POST", "/api/retention/rules")).toHaveLength(1));
    const body = JSON.parse(String((calls("POST", "/api/retention/rules")[0][1] as RequestInit).body));
    expect(body).toMatchObject({ name: "Old films", level: "show", isEnabled: true, waitDays: 14, checkIntervalHours: 8, removalMode: "automatic", expiryAction: "log", expiryTag: null, deferWhilePlaying: true });
    expect(body.steps).toHaveLength(2);
    expect(await drawer.findByText("Rule added")).toBeVisible();
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("shows the server's validation messages in the footer, joined, and keeps the drawer open", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => path === "/api/retention/rules" && init?.method === "POST"
      ? reply({ errors: { name: ["Give the rule a name."], waitDays: ["Wait between 1 and 365 days."] } }, false, 400)
      : reply([]));
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.click(drawer.getByRole("button", { name: "Add rule" }));

    expect(await drawer.findByText("Give the rule a name. Wait between 1 and 365 days.")).toBeVisible();
    expect(drawer.queryByText("Rule added")).not.toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeVisible();
  });

  it("asks for the tag to give only when the rule gives a tag", async () => {
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.queryByLabelText("Tag to give")).not.toBeInTheDocument();

    fireEvent.change(drawer.getByLabelText("What happens"), { target: { value: "tag" } });

    expect(drawer.getByLabelText("Tag to give")).toBeVisible();
    fireEvent.change(drawer.getByLabelText("What happens"), { target: { value: "delete" } });
    expect(drawer.queryByLabelText("Tag to give")).not.toBeInTheDocument();
  });

  it("says Counting while it asks what the rule would select and then how many match, after a short pause", async () => {
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    expect(drawer.getByText("counting…")).toBeVisible();
    expect(calls("POST", "/api/retention/rules/trial")).toHaveLength(0);

    expect(await drawer.findByText(/7 match/, {}, { timeout: 3_000 })).toBeVisible();
    expect(calls("POST", "/api/retention/rules/trial")).toHaveLength(1);
    expect(drawer.queryByText("counting…")).not.toBeInTheDocument();
  });
});

describe("Editing and switching a rule", () => {
  beforeEach(() => {
    loaderData.rules = [rule()];
  });

  it("opens the saved rule, and Save rule puts it back and says Saved just now", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" ? rule({ name: "Unwatched films (old)" }) : path.includes("/trial") ? { considered: 5, stepCounts: [], matchedCount: 0, matchedBytes: 0, undecidedCount: 0, unreachableServers: [], problems: [], unmanaged: [], expiryAction: "log", expiryDescription: "" } : []));
    render_();

    fireEvent.click(screen.getByText("Unwatched films"));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByLabelText("Name")).toHaveValue("Unwatched films");
    expect(drawer.getByLabelText("Cleans out")).toBeVisible();
    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Unwatched films (old)" } });
    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/retention/rules/rule-1")).toHaveLength(1));
    expect(JSON.parse(String((calls("PUT", "/api/retention/rules/rule-1")[0][1] as RequestInit).body))).toMatchObject({ name: "Unwatched films (old)", level: "movie" });
    expect(await drawer.findByText("Saved just now")).toBeVisible();
  });

  it("the row's On switch puts the rule back with only Enabled flipped", async () => {
    render_();

    fireEvent.click(screen.getByRole("switch", { name: "Turn off Unwatched films" }));

    await waitFor(() => expect(calls("PUT", "/api/retention/rules/rule-1")).toHaveLength(1));
    expect(JSON.parse(String((calls("PUT", "/api/retention/rules/rule-1")[0][1] as RequestInit).body))).toMatchObject({ name: "Unwatched films", isEnabled: false, waitDays: 14 });
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("a switch whose request fails leaves the page standing and reads the rule again", async () => {
    authedFetchMock.mockImplementation((_path: string, init?: RequestInit) => init?.method === "PUT" ? reply({}, false, 500) : reply([]));
    render_();

    fireEvent.click(screen.getByRole("switch", { name: "Turn off Unwatched films" }));

    await waitFor(() => expect(calls("PUT", "/api/retention/rules/rule-1")).toHaveLength(1));
    expect(screen.getByText("Unwatched films")).toBeVisible();
  });

  it("Delete asks first by name and only then deletes the rule", async () => {
    render_();
    fireEvent.click(screen.getByText("Unwatched films"));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.click(drawer.getByRole("button", { name: "Delete…" }));

    expect(await screen.findByText("Delete “Unwatched films”?")).toBeVisible();
    expect(calls("DELETE", "/api/retention/rules/rule-1")).toHaveLength(0);
    fireEvent.click(screen.getAllByRole("button", { name: /^Delete/ }).at(-1)!);
    await waitFor(() => expect(calls("DELETE", "/api/retention/rules/rule-1")).toHaveLength(1));
  });
});

describe("Leaving soon: Keep", () => {
  beforeEach(() => {
    loaderData.rules = [rule()];
    loaderData.marks = [mark()];
  });

  it("opens Keep <title>? offering the rule or every rule and an optional note, fresh each time it opens", async () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: "Keep Dementia 13" }));

    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByText("Keep Dementia 13?")).toBeVisible();
    expect(dialog.getByRole("radio", { name: "“Unwatched films”" })).toBeChecked();
    expect(dialog.getByRole("radio", { name: "Every rule" })).not.toBeChecked();
    const note = dialog.getByPlaceholderText("Kids' favourite");
    expect(note).toHaveAttribute("maxlength", "300");
    fireEvent.click(dialog.getByRole("radio", { name: "Every rule" }));
    fireEvent.change(note, { target: { value: "Kids' favourite" } });
    fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Keep Dementia 13" }));

    const again = within(await screen.findByRole("dialog"));
    expect(again.getByRole("radio", { name: "“Unwatched films”" })).toBeChecked();
    expect(again.getByPlaceholderText("Kids' favourite")).toHaveValue("");
  });

  it("Keep posts the title with the rule it was kept from and the note, and the page says so", async () => {
    render_();
    fireEvent.click(screen.getByRole("button", { name: "Keep Dementia 13" }));
    const dialog = within(await screen.findByRole("dialog"));
    fireEvent.change(dialog.getByPlaceholderText("Kids' favourite"), { target: { value: "Kids' favourite" } });

    fireEvent.click(dialog.getByRole("button", { name: "Keep" }));

    await waitFor(() => expect(calls("POST", "/api/retention/keeps")).toHaveLength(1));
    expect(JSON.parse(String((calls("POST", "/api/retention/keeps")[0][1] as RequestInit).body))).toEqual({ entityId: "movie-9", ruleId: "rule-1", note: "Kids' favourite" });
    expect(await screen.findByText("Dementia 13 is kept from “Unwatched films”.")).toBeVisible();
  });

  it("Every rule posts no rule and the page says it is kept from every rule", async () => {
    render_();
    fireEvent.click(screen.getByRole("button", { name: "Keep Dementia 13" }));
    const dialog = within(await screen.findByRole("dialog"));
    fireEvent.click(dialog.getByRole("radio", { name: "Every rule" }));

    fireEvent.click(dialog.getByRole("button", { name: "Keep" }));

    await waitFor(() => expect(calls("POST", "/api/retention/keeps")).toHaveLength(1));
    expect(JSON.parse(String((calls("POST", "/api/retention/keeps")[0][1] as RequestInit).body))).toMatchObject({ ruleId: null });
    expect(await screen.findByText("Dementia 13 is kept from every rule.")).toBeVisible();
  });

  it("says the title could not be kept when the server refuses", async () => {
    authedFetchMock.mockImplementation((path: string) => path === "/api/retention/keeps" ? reply({}, false, 500) : reply([]));
    render_();
    fireEvent.click(screen.getByRole("button", { name: "Keep Dementia 13" }));
    const dialog = within(await screen.findByRole("dialog"));

    fireEvent.click(dialog.getByRole("button", { name: "Keep" }));

    expect(await screen.findByText("Dementia 13 could not be kept. Try again.")).toBeVisible();
  });
});

describe("Leaving soon: Postpone", () => {
  beforeEach(() => {
    loaderData.rules = [rule()];
    loaderData.marks = [mark()];
  });

  it("starts at 14 days, refuses anything but a whole number from 1 to 3650 and shows the new day", async () => {
    render_();

    fireEvent.click(screen.getByRole("button", { name: "Postpone Dementia 13" }));

    const dialog = within(await screen.findByRole("dialog"));
    const days = dialog.getByLabelText("Days to postpone");
    expect(days).toHaveValue(14);
    expect(dialog.getByText(/Its new day is /)).toBeVisible();
    fireEvent.change(days, { target: { value: "0" } });
    expect(dialog.getByText("Enter a whole number of days from 1 to 3650.")).toBeVisible();
    expect(dialog.getByRole("button", { name: "Postpone" })).toBeDisabled();
    fireEvent.change(days, { target: { value: "3651" } });
    expect(dialog.getByRole("button", { name: "Postpone" })).toBeDisabled();
    fireEvent.change(days, { target: { value: "1.5" } });
    expect(dialog.getByRole("button", { name: "Postpone" })).toBeDisabled();
    fireEvent.change(days, { target: { value: "30" } });
    expect(dialog.getByRole("button", { name: "Postpone" })).toBeEnabled();
  });

  it("Postpone posts the days for that title and rule and says it is postponed and not kept", async () => {
    authedFetchMock.mockImplementation((path: string) => path === "/api/retention/leaving-soon/postpone" ? reply([mark({ removeAfterUtc: "2099-11-14T00:00:00Z" })]) : reply([]));
    render_();
    fireEvent.click(screen.getByRole("button", { name: "Postpone Dementia 13" }));
    const dialog = within(await screen.findByRole("dialog"));
    fireEvent.change(dialog.getByLabelText("Days to postpone"), { target: { value: "30" } });

    fireEvent.click(dialog.getByRole("button", { name: "Postpone" }));

    await waitFor(() => expect(calls("POST", "/api/retention/leaving-soon/postpone")).toHaveLength(1));
    expect(JSON.parse(String((calls("POST", "/api/retention/leaving-soon/postpone")[0][1] as RequestInit).body))).toEqual({ ruleId: "rule-1", entityIds: ["movie-9"], days: 30 });
    expect(await screen.findByText(/Dementia 13 is postponed by 30 days: .* It is not kept\./)).toBeVisible();
  });
});

describe("Media servers in a rule", () => {
  const server = (overrides: Record<string, unknown> = {}) => ({ id: "srv-1", name: "Living room", kind: "jellyfin", baseUrl: "http://192.168.1.20:8096", serverName: null, ...overrides });

  it("says none are connected and where to connect one, when there are none", async () => {
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    expect(drawer.getByText("none connected")).toBeVisible();
    expect(drawer.getByText(/Connect Plex, Jellyfin or Emby under Find & Download/)).toBeVisible();
  });

  it("has one switch per connected server with its name and what it reports itself as, or its address", async () => {
    loaderData.servers = [server(), server({ id: "srv-2", name: "Study", kind: "plex", serverName: "Study Plex" })];
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    expect(switchOf(drawer, "Living room")).toHaveAttribute("aria-checked", "false");
    expect(drawer.getByText("http://192.168.1.20:8096")).toBeVisible();
    expect(switchOf(drawer, "Study")).toHaveAttribute("aria-checked", "false");
    expect(drawer.getByText("Reports itself as Study Plex")).toBeVisible();
    expect(drawer.queryByText("none connected")).not.toBeInTheDocument();
  });

  it("the servers switched on are the ones the saved rule reads", async () => {
    loaderData.servers = [server(), server({ id: "srv-2", name: "Study", kind: "plex" })];
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/retention/rules" ? rule({ id: "rule-new" }) : path.includes("/trial") ? { considered: 0, stepCounts: [], matchedCount: 0, matchedBytes: 0, undecidedCount: 0, unreachableServers: [], problems: [], unmanaged: [], expiryDescription: "" } : []));
    render_();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const drawer = within(await screen.findByRole("dialog"));

    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Old films" } });
    fireEvent.click(switchOf(drawer, "Study"));
    fireEvent.click(drawer.getByRole("button", { name: "Add rule" }));

    await waitFor(() => expect(calls("POST", "/api/retention/rules")).toHaveLength(1));
    expect(JSON.parse(String((calls("POST", "/api/retention/rules")[0][1] as RequestInit).body)).mediaServerIds).toEqual(["srv-2"]);
  });

  it("the list names the servers a rule reads, and a server that was removed", async () => {
    loaderData.servers = [server()];
    loaderData.rules = [rule({ mediaServerIds: ["srv-1", "gone"] })];
    render_();

    expect(screen.getByText(/Movies · Living room, Removed server/)).toBeVisible();
  });
});

describe("The Enabled switch in a rule's drawer", () => {
  it("saves a rule that is off, which is not checked and marks nothing", async () => {
    loaderData.rules = [rule()];
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "PUT" ? rule({ isEnabled: false }) : path.includes("/trial") ? { considered: 0, stepCounts: [], matchedCount: 0, matchedBytes: 0, undecidedCount: 0, unreachableServers: [], problems: [], unmanaged: [], expiryDescription: "" } : []));
    render_();
    fireEvent.click(screen.getByText("Unwatched films"));
    const drawer = within(await screen.findByRole("dialog"));
    expect(drawer.getByText("A rule that is off is not checked, and marks nothing.")).toBeVisible();
    expect(switchOf(drawer, "Enabled")).toHaveAttribute("aria-checked", "true");

    fireEvent.click(switchOf(drawer, "Enabled"));
    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/retention/rules/rule-1")).toHaveLength(1));
    expect(JSON.parse(String((calls("PUT", "/api/retention/rules/rule-1")[0][1] as RequestInit).body)).isEnabled).toBe(false);
  });
});

describe("The Kept card", () => {
  const keep = (overrides: Record<string, unknown> = {}) => ({ id: "keep-1", entityId: "movie-9", level: "movie", title: "Dementia 13", ruleId: "rule-1", ruleName: "Unwatched films", note: "Kids' favourite", createdUtc: "2026-10-02T10:00:00Z", ...overrides });

  it("is not drawn at all while nothing is kept", () => {
    render_();
    expect(screen.queryByText("Kept")).not.toBeInTheDocument();
  });

  it("lists each keep with its title and level, the rule it was kept from, its note and date", () => {
    loaderData.keeps = [keep(), keep({ id: "keep-2", entityId: "show-1", level: "show", title: "Dark Shadows", ruleId: null, ruleName: null, note: null }), keep({ id: "keep-3", title: "Orphan", ruleName: null, note: null })];
    render_();

    expect(screen.getByText("3 titles")).toBeVisible();
    expect(screen.getByText("Dementia 13")).toBeVisible();
    expect(screen.getByText("“Unwatched films”")).toBeVisible();
    expect(screen.getByText("Kids' favourite")).toBeVisible();
    expect(screen.getByText("Every rule")).toBeVisible();
    expect(screen.getByText("“Deleted rule”")).toBeVisible();
    expect(screen.getAllByRole("button", { name: "Undo" })).toHaveLength(3);
  });

  it("Undo takes the keep off the list at the server and reads the page again", async () => {
    loaderData.keeps = [keep()];
    authedFetchMock.mockImplementation(() => reply({}, true, 204));
    render_();

    fireEvent.click(screen.getByRole("button", { name: "Undo" }));

    await waitFor(() => expect(calls("DELETE", "/api/retention/keeps/keep-1")).toHaveLength(1));
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("an undo the server refuses says so and keeps the keep", async () => {
    loaderData.keeps = [keep()];
    authedFetchMock.mockImplementation(() => reply({ message: "No such keep." }, false, 404));
    render_();

    fireEvent.click(screen.getByRole("button", { name: "Undo" }));

    expect(await screen.findByText(/No such keep|could not be taken off the keep list/)).toBeVisible();
    expect(revalidateMock).not.toHaveBeenCalled();
    expect(screen.getByText("Dementia 13")).toBeVisible();
  });
});

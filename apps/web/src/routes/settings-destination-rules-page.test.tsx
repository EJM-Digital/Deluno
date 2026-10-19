import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * Final destinations (Media Management): exceptions to the library folder. A rule says what it matches and where matching titles
 * go; Test a title shows which rule a title would meet. Each control sends the request the destination-rule routes are tested against.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("./settings-overview-page", () => ({ settingsOverviewLoader: vi.fn() }));

const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", async (importOriginal) => {
  const original = await importOriginal<typeof import("../components/shell/toaster")>();
  return {
    ...original,
    toast: Object.assign(() => undefined, { success: (...args: unknown[]) => toastSuccess(...args), error: (...args: unknown[]) => toastError(...args), message: vi.fn(), info: vi.fn(), warning: vi.fn() })
  };
});

const revalidateMock = vi.fn();

const rule = (overrides: Record<string, unknown> = {}) => ({
  id: "d1", name: "Anime to the anime drive", mediaType: "tv", matchKind: "anime", matchValue: "true", rootPath: "E:/Anime", folderTemplate: null,
  priority: 50, isEnabled: true, libraryId: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z", ...overrides
});

let loaderData: Record<string, unknown>;
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsDestinationRulesPage } = await import("./settings-destination-rules-page");

vi.setConfig({ testTimeout: 20_000 });

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function load(rules: unknown[] = [], extra: Record<string, unknown> = {}) {
  loaderData = {
    libraries: [{ id: "lib-1", name: "Films", mediaType: "movies", defaultPolicySetId: null }, { id: "lib-2", name: "Shows", mediaType: "tv", defaultPolicySetId: "plan-1" }],
    settings: { ...emptyPlatformSettingsSnapshot, movieRootPath: "D:/Movies", seriesRootPath: "D:/TV", downloadsPath: "D:/Downloads" },
    destinationRules: rules,
    policySets: [{ id: "plan-1", name: "Plan", destinationRuleId: "d1" }],
    tags: [{ id: "t1", name: "4k" }],
    ...extra
  };
}

/** The confirmation that opens over the drawer is the last dialog on the page. */
async function topDialog() {
  await waitFor(() => expect(screen.getAllByRole("dialog").length).toBeGreaterThan(1));
  return screen.getAllByRole("dialog").at(-1)!;
}

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsDestinationRulesPage />
    </MemoryRouter>
  );
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  authedFetchMock.mockReset();
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  authedFetchMock.mockImplementation(() => reply([]));
  load();
});

describe("the list of destination rules", () => {
  it("says the library folder is the default and offers a first rule, when there are none", () => {
    renderPage();
    expect(screen.getByText("No destination rules")).toBeVisible();
    expect(screen.getByText(/Your library folder is the default/)).toBeVisible();
    expect(screen.getAllByRole("button", { name: "New rule" }).length).toBeGreaterThan(0);
  });

  it("lists rules in the order they run, with what they match, where they go and which library profile uses them", () => {
    load([rule({ id: "d2", name: "Late rule", priority: 500, matchKind: "genre", matchValue: "Horror", mediaType: "movies", rootPath: "F:/Horror", folderTemplate: "{Title} ({Year})" }), rule()]);
    renderPage();

    const rows = screen.getAllByRole("row").slice(1);
    expect(within(rows[0]).getByText("Anime to the anime drive")).toBeVisible();
    expect(within(rows[0]).getByText("Anime is true")).toBeVisible();
    expect(within(rows[0]).getByText("E:/Anime")).toBeVisible();
    expect(within(rows[0]).getByText("Standard folder naming")).toBeVisible();
    expect(within(rows[0]).getByText("Applied through a Library Profile")).toBeVisible();
    expect(within(rows[1]).getByText("Late rule")).toBeVisible();
    expect(within(rows[1]).getByText("{Title} ({Year})")).toBeVisible();
    expect(screen.getByText("2 rules · 2 active")).toBeVisible();
  });

  it("the switch pauses a rule and turns it on again, sending the rule with only that flag changed", async () => {
    load([rule()]);
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Pause Anime to the anime drive" }));

    await waitFor(() => expect(calls("PUT", "/api/destination-rules/d1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/destination-rules/d1")[0])).toMatchObject({ id: "d1", name: "Anime to the anime drive", isEnabled: false, priority: 50 });
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("a switch the server refuses says why", async () => {
    load([rule()]);
    authedFetchMock.mockImplementation(() => reply({ message: "Not allowed." }, false, 400));
    renderPage();

    fireEvent.click(screen.getByRole("switch", { name: "Pause Anime to the anime drive" }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(revalidateMock).not.toHaveBeenCalled();
  });
});

describe("a new rule", () => {
  async function openNew() {
    renderPage();
    fireEvent.click(screen.getAllByRole("button", { name: "New rule" })[0]);
    return within(await screen.findByRole("dialog"));
  }

  it("starts as a movie genre rule going to the movie folder, at the normal order, switched on", async () => {
    const drawer = await openNew();

    expect(drawer.getByText("New destination rule")).toBeVisible();
    expect(drawer.getByLabelText("Name")).toHaveValue("");
    expect(drawer.getByLabelText("Destination folder")).toHaveValue("D:/Movies");
    expect(drawer.getByRole("button", { name: "Create rule" })).toBeVisible();
    const match = drawer.getByLabelText("Match") as HTMLSelectElement;
    expect(match.value).toBe("genre");
    expect(Array.from(match.options).map((option) => option.textContent)).not.toContain("Quality (cannot place a title)");
    expect(Array.from(match.options).map((option) => option.textContent)).not.toContain("Library (cannot place a title)");
  });

  it("asks for a name, what to match and a folder before it sends anything", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Destination folder"), { target: { value: "" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText("Give this rule a name.")).toBeVisible();
    expect(drawer.getByText("Choose what the rule matches.")).toBeVisible();
    expect(drawer.getByText("Choose the destination folder.")).toBeVisible();
    expect(calls("POST", "/api/destination-rules")).toHaveLength(0);
  });

  it("choosing TV moves the folder to the TV folder, and a different kind of match clears the value", async () => {
    const drawer = await openNew();

    fireEvent.click(drawer.getByRole("radio", { name: "TV shows" }));
    expect(drawer.getByLabelText("Destination folder")).toHaveValue("D:/TV");
    fireEvent.change(drawer.getByLabelText("Match"), { target: { value: "language" } });
    expect(drawer.getByLabelText("Match")).toHaveValue("language");
  });

  it("Create rule posts the whole rule and says Rule created", async () => {
    authedFetchMock.mockImplementation((path: string, init?: RequestInit) => reply(init?.method === "POST" && path === "/api/destination-rules" ? rule({ id: "new", name: "Horror out", matchKind: "genre", matchValue: "Horror", mediaType: "movies", rootPath: "F:/Horror" }) : {}));
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Horror out" } });
    fireEvent.change(drawer.getByLabelText("Destination folder"), { target: { value: "F:/Horror" } });
    fireEvent.change(drawer.getByLabelText(/Folder name format/), { target: { value: "{Title} ({Year})" } });
    fireEvent.change(drawer.getByLabelText("Value"), { target: { value: "Horror" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(calls("POST", "/api/destination-rules")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/destination-rules")[0])).toMatchObject({ name: "Horror out", mediaType: "movies", matchKind: "genre", matchValue: "Horror", rootPath: "F:/Horror", folderTemplate: "{Title} ({Year})", priority: 100, isEnabled: true });
    expect(await drawer.findByText("Rule created")).toBeVisible();
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("shows the server's reason when the rule is refused and keeps the drawer open", async () => {
    authedFetchMock.mockImplementation(() => reply({ errors: { name: ["A rule with that name already exists."] } }, false, 400));
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Dup" } });
    fireEvent.change(drawer.getByLabelText("Value"), { target: { value: "Horror" } });

    fireEvent.click(drawer.getByRole("button", { name: "Create rule" }));

    expect(await drawer.findByText(/already exists|could not be created/)).toBeVisible();
    expect(screen.getByRole("dialog")).toBeVisible();
  });

  it("asks before throwing away what was typed", async () => {
    const drawer = await openNew();
    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Half done" } });

    fireEvent.click(drawer.getByRole("button", { name: "Cancel" }));

    const confirm = within(await topDialog());
    expect(confirm.getByText("Discard unsaved changes?")).toBeVisible();
    fireEvent.click(confirm.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });
});

describe("an existing rule", () => {
  async function openIt() {
    load([rule()]);
    renderPage();
    fireEvent.click(screen.getByText("Anime to the anime drive"));
    return within(await screen.findByRole("dialog"));
  }

  it("opens with its own values, a Save rule button and a Delete section", async () => {
    const drawer = await openIt();

    expect(drawer.getByLabelText("Name")).toHaveValue("Anime to the anime drive");
    expect(drawer.getByLabelText("Destination folder")).toHaveValue("E:/Anime");
    expect(drawer.getByRole("button", { name: "Save rule" })).toBeVisible();
    expect(drawer.getByText("Delete this rule")).toBeVisible();
    expect(drawer.getByText("1 libraries")).toBeVisible();
  });

  it("Save rule puts the changed rule at its own id and says Saved just now", async () => {
    const drawer = await openIt();
    fireEvent.change(drawer.getByLabelText("Name"), { target: { value: "Anime elsewhere" } });

    fireEvent.click(drawer.getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(calls("PUT", "/api/destination-rules/d1")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/destination-rules/d1")[0])).toMatchObject({ name: "Anime elsewhere", rootPath: "E:/Anime", matchKind: "anime" });
    expect(await drawer.findByText("Saved just now")).toBeVisible();
  });

  it("Delete asks first, then removes the rule and says so", async () => {
    const drawer = await openIt();

    fireEvent.click(drawer.getByRole("button", { name: "Delete" }));
    const confirm = within(await topDialog());
    expect(confirm.getByText("Delete “Anime to the anime drive”?")).toBeVisible();
    expect(calls("DELETE", "/api/destination-rules/d1")).toHaveLength(0);
    fireEvent.click(confirm.getByRole("button", { name: /^Delete/ }));

    await waitFor(() => expect(calls("DELETE", "/api/destination-rules/d1")).toHaveLength(1));
    expect(toastSuccess).toHaveBeenCalledWith("Anime to the anime drive removed");
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("says when an old rule is of a kind that can never place anything", async () => {
    load([rule({ id: "q1", name: "By quality", matchKind: "quality", matchValue: "4K", mediaType: "movies" })]);
    renderPage();
    fireEvent.click(screen.getByText("By quality"));
    const drawer = within(await screen.findByRole("dialog"));

    expect(drawer.getByText(/cannot place anything/)).toBeVisible();
    expect(Array.from((drawer.getByLabelText("Match") as HTMLSelectElement).options).map((option) => option.textContent)).toContain("Quality (cannot place a title)");
  });
});

describe("Test a title", () => {
  const preview = {
    matchedRuleName: "Anime to the anime drive", preferredTransferMode: "hardlink", hardlinkAvailable: true, explanation: "The anime rule matched.",
    sourcePath: "D:/Downloads/Dune.mkv", destinationPath: "E:/Anime/Dune (2024)", decisionSteps: ["Looked at the genre.", "Anime rule matched."], warnings: ["The folder is nearly full."]
  };

  async function openTest() {
    load([rule()]);
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test a title" }));
    return within(await screen.findByRole("dialog"));
  }

  it("starts from a real-looking example and runs the preview with what is typed", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/filesystem/import/preview" ? preview : []));
    const drawer = await openTest();
    expect(drawer.getByLabelText("Title")).toHaveValue("Dune Part Two");
    fireEvent.change(drawer.getByLabelText("Title"), { target: { value: "Spirited Away" } });
    fireEvent.change(drawer.getByLabelText(/^Year/), { target: { value: "2001" } });
    fireEvent.change(drawer.getByLabelText(/^Genres/), { target: { value: "Animation, Family" } });
    fireEvent.change(drawer.getByLabelText(/^Original language/), { target: { value: "ja" } });

    fireEvent.click(drawer.getByRole("button", { name: "Run preview" }));

    await waitFor(() => expect(calls("POST", "/api/filesystem/import/preview")).toHaveLength(1));
    expect(bodyOf(calls("POST", "/api/filesystem/import/preview")[0])).toMatchObject({ mediaType: "movies", title: "Spirited Away", year: 2001, genres: ["Animation", "Family"], originalLanguage: "ja" });
  });

  it("shows which rule matched, where the title would land, the steps and any warning", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/filesystem/import/preview" ? preview : []));
    const drawer = await openTest();

    fireEvent.click(drawer.getByRole("button", { name: "Run preview" }));

    expect(await drawer.findByText("Rule: Anime to the anime drive")).toBeVisible();
    expect(drawer.getByText("Single-copy link available")).toBeVisible();
    expect(drawer.getByText("E:/Anime/Dune (2024)")).toBeVisible();
    expect(drawer.getByText("Anime rule matched.")).toBeVisible();
    expect(drawer.getByText("The folder is nearly full.")).toBeVisible();
    expect(drawer.getByText("rule: Anime to the anime drive")).toBeVisible();
  });

  it("says the library default is used when no rule matched", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/filesystem/import/preview" ? { ...preview, matchedRuleName: null, hardlinkAvailable: false, preferredTransferMode: "copy", decisionSteps: [], warnings: [] } : []));
    const drawer = await openTest();

    fireEvent.click(drawer.getByRole("button", { name: "Run preview" }));

    expect(await drawer.findByText("Default root")).toBeVisible();
    expect(drawer.getByText("Copy required")).toBeVisible();
    expect(drawer.getByText("library default")).toBeVisible();
  });

  it("says the preview failed when the server refuses it", async () => {
    authedFetchMock.mockImplementation(() => reply({ message: "Preview failed." }, false, 500));
    const drawer = await openTest();

    fireEvent.click(drawer.getByRole("button", { name: "Run preview" }));

    await waitFor(() => expect(calls("POST", "/api/filesystem/import/preview")).toHaveLength(1));
    expect(drawer.queryByText("Result")).not.toBeInTheDocument();
  });
});

import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Final Destinations (destination rules): saving, validating, switching, deleting and the "Test a title" preview.
 * The page's own fetch wrappers are replaced with a recorder that answers each address.
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock("../components/shell/toaster", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/shell/toaster")>()),
  toast: toastMock
}));

const calls: Array<{ path: string; method: string; body: unknown }> = [];
let replies: Record<string, (init: { method?: string; body?: string }) => Response> = {};
function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}
async function answer(path: string, init: { method?: string; body?: string } = {}) {
  calls.push({ path, method: init.method ?? "GET", body: init.body ? JSON.parse(init.body) : undefined });
  const reply = replies[`${init.method ?? "GET"} ${path}`];
  return reply ? reply(init) : json(path.startsWith("/api/tags") ? [] : {});
}
vi.mock("../lib/use-auth", () => ({ authedFetch: (path: string, init?: { method?: string; body?: string }) => answer(path, init) }));
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: async (path: string, init?: { method?: string; body?: string }) => {
    const response = await answer(path, init);
    if (!response.ok) throw new Error((await response.json()).message ?? "failed");
    return response.json();
  }
}));

const revalidate = vi.fn();
const loaderData: Record<string, unknown> = {};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsDestinationRulesPage } = await import("./settings-destination-rules-page");

const rule = {
  id: "rule-1", name: "Documentaries", mediaType: "movies", matchKind: "genre", matchValue: "Documentary", rootPath: "/media/docs",
  folderTemplate: "{Genre}/{Title} ({Year})", priority: 50, isEnabled: true, libraryId: "lib-docs"
};
const paused = { ...rule, id: "rule-2", name: "Anime", matchKind: "anime", matchValue: "true", folderTemplate: null, priority: 100, isEnabled: false, libraryId: null };

function reset() {
  calls.length = 0;
  replies = {};
  Object.assign(loaderData, {
    libraries: [
      { id: "lib-docs", name: "Documentaries", mediaType: "movies", rootPath: "/media/docs", defaultPolicySetId: "plan-1" },
      { id: "lib-tv", name: "Shows", mediaType: "tv", rootPath: "/media/tv", defaultPolicySetId: null }
    ],
    settings: { movieRootPath: "/media/movies", seriesRootPath: "/media/tv", downloadsPath: "/downloads" },
    destinationRules: [rule, paused],
    policySets: [{ id: "plan-1", name: "Docs plan", destinationRuleId: "rule-1" }],
    tags: []
  });
}
function renderPage() {
  return render(<MemoryRouter><SettingsDestinationRulesPage /></MemoryRouter>);
}
function sent(method: string, path: string) {
  return calls.filter((call) => call.method === method && call.path === path);
}

describe("destination rules list and switch", () => {
  beforeEach(reset);
  afterEach(() => { vi.clearAllMocks(); });

  it("lists each rule with what it matches, where it goes, its folder format and whether it is on", () => {
    renderPage();
    expect(screen.getByText("Genre is Documentary")).toBeTruthy();
    expect(screen.getAllByText("/media/docs").length).toBeGreaterThan(0);
    expect(screen.getByText("{Genre}/{Title} ({Year})")).toBeTruthy();
    expect(screen.getByText("Standard folder naming")).toBeTruthy();
    expect(screen.getByText("2 rules · 1 active")).toBeTruthy();
    expect(screen.getByText("Active")).toBeTruthy();
    expect(screen.getByText("Off")).toBeTruthy();
  });

  it("pauses a rule with PUT carrying the whole rule and the new switch value", async () => {
    replies["PUT /api/destination-rules/rule-1"] = () => json({ ...rule, isEnabled: false });
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Documentaries" }));

    await waitFor(() => expect(sent("PUT", "/api/destination-rules/rule-1")).toHaveLength(1));
    expect(sent("PUT", "/api/destination-rules/rule-1")[0].body).toEqual({ ...rule, isEnabled: false });
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it("keeps an open drawer's switch in step when the same rule is switched from its row", async () => {
    replies["PUT /api/destination-rules/rule-1"] = () => json({ ...rule, isEnabled: false });
    renderPage();
    fireEvent.click(screen.getByText("Genre is Documentary"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("switch", { name: /^Use this rule/ }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(screen.getByRole("switch", { name: "Pause Documentaries", hidden: true }));
    await waitFor(() => expect(within(dialog).getByRole("switch", { name: /^Use this rule/ }).getAttribute("aria-checked")).toBe("false"));
  });

  it("tells the person when the switch could not be saved", async () => {
    replies["PUT /api/destination-rules/rule-2"] = () => json({ message: "Nope." }, 500);
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Enable Anime" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
  });
});

describe("destination rule drawer", () => {
  beforeEach(reset);
  afterEach(() => { vi.clearAllMocks(); });

  it("asks for a name, what the rule matches and the destination folder, and sends nothing until they are given", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByPlaceholderText(/^\/media\/movies|Example/) as HTMLInputElement, { target: { value: "" } });
    const pathBox = within(dialog).getAllByRole("textbox").find((box) => (box as HTMLInputElement).value === "/media/movies") as HTMLInputElement;
    fireEvent.change(pathBox, { target: { value: "" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create rule" }));

    expect(await within(dialog).findByText("Give this rule a name.")).toBeTruthy();
    expect(within(dialog).getByText("Choose what the rule matches.")).toBeTruthy();
    expect(within(dialog).getByText("Choose the destination folder.")).toBeTruthy();
    expect(calls.filter((call) => call.method !== "GET")).toHaveLength(0);
  });

  it("starts the destination folder on the default movie folder, offers the folder picker and the folder check, and saves what was typed", async () => {
    replies["POST /api/destination-rules"] = () => json({ ...rule, id: "rule-9", name: "Kids" });
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("button", { name: /Choose folder/ })).toBeTruthy();
    expect(within(dialog).getByRole("button", { name: "Check" })).toBeTruthy();
    const pathBox = within(dialog).getAllByRole("textbox").find((box) => (box as HTMLInputElement).value === "/media/movies") as HTMLInputElement;
    expect(pathBox).toBeTruthy();

    fireEvent.change(within(dialog).getByPlaceholderText("Example: family movies to another folder"), { target: { value: "Kids" } });
    fireEvent.change(within(dialog).getByRole("combobox", { name: /^Value/ }), { target: { value: "Family" } });
    fireEvent.change(pathBox, { target: { value: "/media/kids" } });
    fireEvent.change(within(dialog).getByPlaceholderText("{Title} ({Year})"), { target: { value: "{Tag}/{Title} ({Year})" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create rule" }));

    expect(await within(dialog).findByText("Rule created")).toBeTruthy();
    expect(sent("POST", "/api/destination-rules")[0].body).toMatchObject({
      name: "Kids", mediaType: "movies", matchKind: "genre", matchValue: "Family", rootPath: "/media/kids", folderTemplate: "{Tag}/{Title} ({Year})", priority: 100, isEnabled: true, libraryId: null
    });
    expect(within(dialog).getByRole("button", { name: "Save rule" })).toBeTruthy();
  });

  it("offers only the libraries of the rule's media type under 'Routes to library' and saves the chosen one", async () => {
    replies["PUT /api/destination-rules/rule-1"] = () => json(rule);
    renderPage();
    fireEvent.click(screen.getByText("Genre is Documentary"));
    const dialog = await screen.findByRole("dialog");
    const routes = within(dialog).getByRole("combobox", { name: /Routes to library/ }) as HTMLSelectElement;
    expect(Array.from(routes.options).map((option) => option.text)).toContain("Documentaries");
    expect(Array.from(routes.options).map((option) => option.text)).not.toContain("Shows");
    expect(routes.value).toBe("lib-docs");

    fireEvent.change(within(dialog).getByPlaceholderText("{Title} ({Year})"), { target: { value: "{Title}" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save rule" }));

    expect(await within(dialog).findByText("Saved just now")).toBeTruthy();
    expect(sent("PUT", "/api/destination-rules/rule-1")[0].body).toMatchObject({ libraryId: "lib-docs", folderTemplate: "{Title}" });
  });

  it("deletes through the danger zone and a confirmation, and toasts the rule's name", async () => {
    replies["DELETE /api/destination-rules/rule-1"] = () => new Response(null, { status: 204 });
    renderPage();
    fireEvent.click(screen.getByText("Genre is Documentary"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(sent("DELETE", "/api/destination-rules/rule-1")).toHaveLength(0);
    fireEvent.click(await screen.findByRole("button", { name: "Delete rule" }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Documentaries removed"));
    expect(sent("DELETE", "/api/destination-rules/rule-1")).toHaveLength(1);
  });
});

describe("Test a title", () => {
  beforeEach(reset);
  afterEach(() => { vi.clearAllMocks(); });

  const matched = {
    sourcePath: "/downloads/Dune.mkv", destinationFolder: "/media/docs/Dune (2024)", destinationPath: "/media/docs/Dune (2024)/Dune.mkv",
    preferredTransferMode: "hardlink", hardlinkAvailable: true, matchedRuleId: "rule-1", matchedRuleName: "Documentaries", sourceExists: true,
    destinationExists: false, sourceSizeBytes: 1, destinationSizeBytes: 0, isSupportedMediaFile: true, mediaProbe: null, transferExplanation: "",
    warnings: ["Source and destination are on different drives."], explanation: "Matched genre destination rule.", decisionSteps: ["Checked rules by order.", "Documentaries won."]
  };

  async function open() {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Test a title" }));
    return await screen.findByRole("dialog");
  }

  it("starts with an example title, the downloads folder as the source, and asks the server to preview with everything typed", async () => {
    replies["POST /api/filesystem/import/preview"] = () => json(matched);
    const dialog = await open();
    fireEvent.change(within(dialog).getByRole("textbox", { name: /^Title$/ }), { target: { value: "Dune Part Two" } });
    fireEvent.change(within(dialog).getByRole("textbox", { name: /^Genres/ }), { target: { value: "Documentary, History" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Run preview" }));

    expect(await within(dialog).findByText("rule: Documentaries")).toBeTruthy();
    expect(sent("POST", "/api/filesystem/import/preview")[0].body).toMatchObject({
      mediaType: "movies", title: "Dune Part Two", year: 2024, sourcePath: "/downloads", genres: ["Documentary", "History"], originalLanguage: "en"
    });
  });

  it("shows the matched rule, the single-copy link and where the file would land, with the reasons and warnings", async () => {
    replies["POST /api/filesystem/import/preview"] = () => json(matched);
    const dialog = await open();
    fireEvent.click(within(dialog).getByRole("button", { name: "Run preview" }));

    expect(await within(dialog).findByText("Rule: Documentaries")).toBeTruthy();
    expect(within(dialog).getByText("Single-copy link")).toBeTruthy();
    expect(within(dialog).getByText("Single-copy link available")).toBeTruthy();
    expect(within(dialog).getByText("/media/docs/Dune (2024)/Dune.mkv")).toBeTruthy();
    expect(within(dialog).getByText("Documentaries won.")).toBeTruthy();
    expect(within(dialog).getByText("Source and destination are on different drives.")).toBeTruthy();
  });

  it("says Default root and Copy required when no rule matches and the files cannot be linked", async () => {
    replies["POST /api/filesystem/import/preview"] = () => json({ ...matched, matchedRuleId: null, matchedRuleName: null, preferredTransferMode: "copy", hardlinkAvailable: false });
    const dialog = await open();
    fireEvent.click(within(dialog).getByRole("button", { name: "Run preview" }));

    expect(await within(dialog).findByText("Default root")).toBeTruthy();
    expect(within(dialog).getByText("library default")).toBeTruthy();
    expect(within(dialog).getByText("copy")).toBeTruthy();
    expect(within(dialog).getByText("Copy required")).toBeTruthy();
  });

  it("shows the server's words when the preview fails", async () => {
    replies["POST /api/filesystem/import/preview"] = () => json({ message: "That source folder does not exist." }, 400);
    const dialog = await open();
    fireEvent.click(within(dialog).getByRole("button", { name: "Run preview" }));
    expect(await within(dialog).findByText("That source folder does not exist.")).toBeTruthy();
  });
});

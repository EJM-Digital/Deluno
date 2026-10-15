import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Settings > Auto-tagging, the parts the first test file does not reach: the value control each kind of condition
 * gets, editing a rule, the row switch, delete, Run the rules now, and reading again when something changes elsewhere.
 */

vi.setConfig({ testTimeout: 30_000 });
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../components/app/tag-picker", () => ({
  TagPicker: ({ label, value, onChange }: { label?: string; value: string; onChange: (value: string) => void }) => (
    <input aria-label={label} value={value} onChange={(event) => onChange(event.target.value)} />
  ),
  parseTagList: (raw: string | null | undefined) => (raw ?? "").split(",").map((item) => item.trim()).filter(Boolean),
  joinTagList: (tags: string[]) => tags.join(", ")
}));
const toastSuccess = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
vi.mock("../components/shell/toaster", () => ({ toast: { success: toastSuccess, error: toastError } }));
const liveRefresh = vi.hoisted(() => ({ spec: undefined as { events?: string[]; areas?: string[] } | undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events?: string[]; areas?: string[] }) => { liveRefresh.spec = spec; } }));

const rule = (over: Record<string, unknown> = {}) => ({
  id: "r1", name: "Anime", tags: ["anime"], removeAutomatically: false, appliesTo: "both", isEnabled: true,
  specifications: [{ type: "genre", negate: false, value: "Animation" }], createdUtc: "", updatedUtc: "", ...over
});
const loaderData = vi.hoisted(() => ({
  libraries: [{ id: "l1", name: "Anime", rootPath: "D:\\Anime", mediaType: "tv" }, { id: "l2", name: "Films", rootPath: "E:\\Films", mediaType: "movies" }],
  qualityProfiles: [{ id: "p1", name: "HD-1080p" }],
  rules: [] as unknown[]
}));
const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsAutoTaggingPage } = await import("./settings-auto-tagging-page");
const renderPage = () => render(<MemoryRouter><SettingsAutoTaggingPage /></MemoryRouter>);
const respond = (body: unknown = {}, status = 200) => ({ ok: status < 400, status, json: async () => body, text: async () => JSON.stringify(body), headers: new Headers() });

beforeEach(() => {
  cleanup();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue(respond({ id: "r1" }));
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidateMock.mockReset();
  loaderData.rules = [];
});

describe("a new rule", () => {
  it("starts with the Genre condition and offers the kinds that suit the chosen scope", () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    expect(screen.getByLabelText("Condition 1 kind")).toHaveValue("genre");
    fireEvent.click(screen.getByRole("radio", { name: "Movies" }));
    const kinds = [...(screen.getByLabelText("Condition 1 kind") as HTMLSelectElement).options].map((option) => option.textContent);
    expect(kinds).toContain("Genre");
    expect(kinds).not.toContain("Series type");
    fireEvent.click(screen.getByRole("radio", { name: "TV shows" }));
    expect([...(screen.getByLabelText("Condition 1 kind") as HTMLSelectElement).options].map((option) => option.textContent)).toContain("Series type");
  });

  it("gives each kind of condition its own value control: free text, yes or no, a from-to range, and pickers for profiles, libraries and folders", () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    const kind = screen.getByLabelText("Condition 1 kind");
    fireEvent.change(kind, { target: { value: "genre" } });
    expect(screen.getByLabelText("Condition 1 value").tagName).toBe("INPUT");
    fireEvent.change(kind, { target: { value: "monitored" } });
    expect([...(screen.getByLabelText("Condition 1 value") as HTMLSelectElement).options].map((o) => o.textContent)).toEqual(expect.arrayContaining(["Yes", "No"]));
    fireEvent.change(kind, { target: { value: "year" } });
    expect(screen.getByLabelText("Condition 1 from")).toBeInTheDocument();
    expect(screen.getByLabelText("Condition 1 to")).toBeInTheDocument();
    fireEvent.change(kind, { target: { value: "qualityProfile" } });
    expect([...(screen.getByLabelText("Condition 1 value") as HTMLSelectElement).options].map((o) => o.textContent)).toContain("HD-1080p");
    fireEvent.change(kind, { target: { value: "library" } });
    expect([...(screen.getByLabelText("Condition 1 value") as HTMLSelectElement).options].map((o) => o.textContent)).toEqual(expect.arrayContaining(["Anime", "Films"]));
    fireEvent.change(kind, { target: { value: "rootFolder" } });
    expect([...(screen.getByLabelText("Condition 1 value") as HTMLSelectElement).options].map((o) => o.textContent)).toEqual(expect.arrayContaining(["D:\\Anime", "E:\\Films"]));
  });
});

describe("editing, switching and deleting a rule", () => {
  it("opens a rule in its drawer and saves it with PUT, saying Saved just now, or 'The rule could not be saved.' on refusal", async () => {
    loaderData.rules = [rule()];
    renderPage();
    fireEvent.click(screen.getByText("Anime"));
    fireEvent.change(screen.getByLabelText("Condition 1 value"), { target: { value: "Animation, Family" } });
    fireEvent.click(screen.getByRole("button", { name: "Save rule" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    const [url, init] = authedFetchMock.mock.calls.find(([, o]) => (o as RequestInit | undefined)?.method === "PUT")!;
    expect(url).toBe("/api/auto-tag-rules/r1");
    expect(JSON.parse((init as RequestInit).body as string)).toMatchObject({ name: "Anime", tags: ["anime"], specifications: [{ type: "genre", negate: false, value: "Animation, Family" }] });

    authedFetchMock.mockResolvedValue(respond({}, 404));
    fireEvent.change(screen.getByLabelText("Condition 1 value"), { target: { value: "Drama" } });
    fireEvent.click(screen.getByRole("button", { name: "Save rule" }));
    expect(await screen.findByText("The rule could not be saved.")).toBeInTheDocument();
  });

  it("pauses or resumes a rule from its row switch by sending the whole rule with isEnabled flipped, and toasts the refusal", async () => {
    loaderData.rules = [rule({ removeAutomatically: true })];
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Anime" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    const [url, init] = authedFetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/auto-tag-rules/r1");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toMatchObject({ id: "r1", name: "Anime", removeAutomatically: true, isEnabled: false });
    expect(revalidateMock).toHaveBeenCalled();

    authedFetchMock.mockResolvedValue(respond({}, 500));
    fireEvent.click(screen.getByRole("switch", { name: "Pause Anime" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not pause Anime."));
  });

  it("deletes a rule after a confirmation that names it, says the tags it gave stay, toasts and closes", async () => {
    loaderData.rules = [rule()];
    renderPage();
    fireEvent.click(screen.getByText("Anime"));
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(await screen.findByText("Delete “Anime”?")).toBeInTheDocument();
    expect(screen.getByText(/Tags it already gave stay on their titles\. It will not tag anything new\./)).toBeInTheDocument();
    authedFetchMock.mockResolvedValue(respond({}, 204));
    fireEvent.click(screen.getByRole("button", { name: "Delete rule" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Anime deleted"));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/auto-tag-rules/r1", { method: "DELETE" });
  });
});

describe("Run the rules now", () => {
  it("asks first, posts to run, and says 'Nothing needed tagging.' or how many titles changed", async () => {
    loaderData.rules = [rule()];
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Run the rules now" }));
    expect(await screen.findByText("Run the rules over every title?")).toBeInTheDocument();
    expect(authedFetchMock).not.toHaveBeenCalled();
    authedFetchMock.mockResolvedValue(respond({ changedTitles: 0 }));
    fireEvent.click(screen.getByRole("button", { name: "Run the rules" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Nothing needed tagging."));
    expect(authedFetchMock.mock.calls[0][0]).toBe("/api/auto-tag-rules/run");

    toastSuccess.mockReset();
    authedFetchMock.mockResolvedValue(respond({ changedTitles: 3 }));
    fireEvent.click(screen.getByRole("button", { name: "Run the rules now" }));
    fireEvent.click(await screen.findByRole("button", { name: "Run the rules" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Changed the tags on 3 titles. Activity has the details."));
  });
});

describe("staying current", () => {
  it("reads again when rules, tags, libraries or quality profiles change elsewhere", () => {
    renderPage();
    expect(liveRefresh.spec?.events).toEqual(["LibraryChanged", "QualityProfileChanged"]);
    expect(liveRefresh.spec?.areas).toHaveLength(2);
  });
});

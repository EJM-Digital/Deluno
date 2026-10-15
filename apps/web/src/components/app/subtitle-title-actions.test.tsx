import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * A title's subtitle controls on its page (coverage register, areas movie-detail and show-detail): the language override card, the
 * by-hand search dialog for one language, and the Resync now / Out of sync buttons. The film page and the show's episode drawer
 * use the same three with a different base path, so each is driven here with both paths.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn() })
}));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast }));

const { SubtitleOverrideCard } = await import("./subtitle-override-card");
const { SubtitleManualSearchDialog } = await import("./subtitle-manual-search-dialog");
const { SubtitleTimingActions } = await import("./subtitle-timing-actions");

let calls: Array<{ url: string; method: string; body: unknown }>;
let reply: { status: number; body: unknown };

const OPTIONS = [{ code: "en", name: "English" }, { code: "fr", name: "French" }, { code: "de", name: "German" }];

beforeEach(() => {
  calls = [];
  reply = { status: 200, body: {} };
  vi.clearAllMocks();
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset().mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const body = init?.body instanceof FormData ? "form-data" : init?.body ? JSON.parse(String(init.body)) : undefined;
    calls.push({ url: String(input), method: init?.method ?? "GET", body });
    return new Response(JSON.stringify(reply.body), { status: reply.status, headers: { "Content-Type": "application/json" } });
  });
});

describe("the subtitle languages for this title card", () => {
  const card = (over: Partial<Parameters<typeof SubtitleOverrideCard>[0]> = {}) => (
    <SubtitleOverrideCard basePath="/api/movies/m1" languages={null} mode={null} libraryLanguages={["en", "fr"]} options={OPTIONS} {...over} />
  );

  it("says it follows the library and names the library's languages when no override is set", () => {
    render(card());

    expect(screen.getByText("Follows library")).toBeInTheDocument();
    expect(screen.getByText(/Following the library: English, French\./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Go back to the library default" })).toBeNull();
  });

  it("says the library asks for none when it has none", () => {
    render(card({ libraryLanguages: [] }));

    expect(screen.getByText(/The library asks for no subtitle languages\./)).toBeInTheDocument();
  });

  it("adding a language puts the override for this title and toasts", async () => {
    const onChanged = vi.fn();
    render(card({ onChanged }));

    fireEvent.change(screen.getByRole("combobox", { name: "Add a subtitle language" }), { target: { value: "de" } });

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("This title's own subtitle languages are set."));
    expect(calls).toEqual([{ url: "/api/movies/m1/subtitles/override", method: "PUT", body: { languages: ["de"], mode: "all" } }]);
    expect(onChanged).toHaveBeenCalled();
  });

  it("a show puts its override on the show, never on one episode", async () => {
    render(card({ basePath: "/api/series/s1" }));

    fireEvent.change(screen.getByRole("combobox", { name: "Add a subtitle language" }), { target: { value: "fr" } });

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].url).toBe("/api/series/s1/subtitles/override");
  });

  it("with an override set it says Overridden, lists the languages, and a second language offers All of them or First one found", async () => {
    render(card({ languages: ["en", "fr"], mode: "all" }));

    expect(screen.getByText("Overridden")).toBeInTheDocument();
    expect(screen.getByText("This title asks for its own subtitle languages instead of its library's.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "First one found" }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0].body).toEqual({ languages: ["en", "fr"], mode: "first" });
  });

  it("removing the last language clears the override and says the title follows its library again", async () => {
    render(card({ languages: ["en"], mode: "all" }));

    fireEvent.click(screen.getByRole("button", { name: "Stop wanting English subtitles" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("This title now follows its library's subtitle languages again."));
    expect(calls).toEqual([{ url: "/api/movies/m1/subtitles/override", method: "DELETE", body: undefined }]);
  });

  it("Go back to the library default deletes the override", async () => {
    render(card({ languages: ["en", "fr"], mode: "all" }));

    fireEvent.click(screen.getByRole("button", { name: "Go back to the library default" }));

    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.url === "/api/movies/m1/subtitles/override")).toBe(true));
  });

  it("a save the server refuses says so and does not claim the languages are set", async () => {
    reply = { status: 400, body: { message: "No." } };
    render(card());

    fireEvent.change(screen.getByRole("combobox", { name: "Add a subtitle language" }), { target: { value: "de" } });

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it("says the list of languages could not be loaded instead of showing a dead control", () => {
    render(card({ options: [] }));

    expect(screen.getByText(/Deluno could not load the list of languages\./)).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Add a subtitle language" })).toBeNull();
  });
});

describe("the by-hand subtitle search dialog", () => {
  const candidate = (token: string, over: Record<string, unknown> = {}) => ({
    providerKey: "opensubtitles", providerName: "OpenSubtitles", downloadToken: token, releaseName: `Big.Buck.Bunny.${token}`, matchDescription: "Matches the release", hearingImpaired: false, downloadCount: 120, ...over
  });
  const dialog = (over: Partial<Parameters<typeof SubtitleManualSearchDialog>[0]> = {}) => (
    <SubtitleManualSearchDialog open onOpenChange={vi.fn()} basePath="/api/movies/m1" language="en" {...over} />
  );

  it("says it is searching while the providers answer, then lists the ranked candidates", async () => {
    let resolve!: (rows: unknown[]) => void;
    mocks.fetchJson.mockReturnValue(new Promise((r) => (resolve = r)));
    render(dialog());

    expect(screen.getByText("Searching every provider…")).toBeInTheDocument();
    resolve([candidate("a", { hearingImpaired: true }), candidate("b")]);

    expect(await screen.findByText("Big.Buck.Bunny.a")).toBeInTheDocument();
    expect(screen.getByText("Big.Buck.Bunny.b")).toBeInTheDocument();
    expect(screen.getByText(/OpenSubtitles · Matches the release · Hearing-impaired · 120 downloads/)).toBeInTheDocument();
    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/movies/m1/subtitles/en/candidates");
  });

  it("a show's episode searches under the episode's own path", async () => {
    mocks.fetchJson.mockResolvedValue([]);
    render(dialog({ basePath: "/api/series/episodes/e1" }));

    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalledWith("/api/series/episodes/e1/subtitles/en/candidates"));
  });

  it("says so, and points to uploading, when no provider offered one", async () => {
    mocks.fetchJson.mockResolvedValue([]);
    render(dialog());

    expect(await screen.findByText("No provider offered a EN subtitle. Upload your own below.")).toBeInTheDocument();
  });

  it("says Deluno could not search when the load fails", async () => {
    mocks.fetchJson.mockRejectedValue(new Error("down"));
    render(dialog());

    expect(await screen.findByText(/Deluno could not search for candidates\./)).toBeInTheDocument();
  });

  it("Use this one downloads and holds the candidate, closes the dialog and refreshes the page", async () => {
    mocks.fetchJson.mockResolvedValue([candidate("a")]);
    reply = { status: 200, body: { message: "Written and held." } };
    const onOpenChange = vi.fn();
    const onChanged = vi.fn();
    render(dialog({ onOpenChange, onChanged }));

    fireEvent.click(await screen.findByRole("button", { name: "Use this one" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Written and held."));
    expect(calls[0]).toEqual({ url: "/api/movies/m1/subtitles/en/candidates/pick", method: "POST", body: { providerKey: "opensubtitles", downloadToken: "a" } });
    expect(onChanged).toHaveBeenCalled();
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("a pick the server refuses is explained and the dialog stays open", async () => {
    mocks.fetchJson.mockResolvedValue([candidate("a")]);
    reply = { status: 400, body: { message: "No." } };
    const onOpenChange = vi.fn();
    render(dialog({ onOpenChange }));

    fireEvent.click(await screen.findByRole("button", { name: "Use this one" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });

  it("Blacklist removes that candidate from the list without downloading it", async () => {
    mocks.fetchJson.mockResolvedValue([candidate("a"), candidate("b")]);
    render(dialog());
    await screen.findByText("Big.Buck.Bunny.a");

    fireEvent.click(screen.getByRole("button", { name: "Blacklist Big.Buck.Bunny.a" }));

    await waitFor(() => expect(screen.queryByText("Big.Buck.Bunny.a")).toBeNull());
    expect(screen.getByText("Big.Buck.Bunny.b")).toBeInTheDocument();
    expect(calls[0].url).toBe("/api/movies/m1/subtitles/en/blacklist");
    expect(calls[0].body).toMatchObject({ providerKey: "opensubtitles", downloadToken: "a", releaseName: "Big.Buck.Bunny.a" });
    expect(mocks.toast.success).toHaveBeenCalledWith("That candidate is blacklisted and will never be offered again for this title.");
  });

  it("uploading a file posts it to the upload route and closes the dialog", async () => {
    mocks.fetchJson.mockResolvedValue([]);
    reply = { status: 200, body: { message: "Uploaded." } };
    const onOpenChange = vi.fn();
    render(dialog({ onOpenChange }));
    await screen.findByText(/No provider offered/);

    fireEvent.change(screen.getByLabelText("Upload a EN subtitle file"), { target: { files: [new File(["1\n00:00:01,000 --> 00:00:02,000\nHi\n"], "my.srt", { type: "text/plain" })] } });

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Uploaded."));
    expect(calls[0]).toEqual({ url: "/api/movies/m1/subtitles/en/upload", method: "POST", body: "form-data" });
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("an upload the server refuses is explained", async () => {
    mocks.fetchJson.mockResolvedValue([]);
    reply = { status: 400, body: { message: "That is not a subtitle." } };
    render(dialog());
    await screen.findByText(/No provider offered/);

    fireEvent.change(screen.getByLabelText("Upload a EN subtitle file"), { target: { files: [new File(["x"], "not.txt", { type: "text/plain" })] } });

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
  });
});

describe("Resync now and Out of sync", () => {
  const actions = (over: Partial<Parameters<typeof SubtitleTimingActions>[0]> = {}) => (
    <SubtitleTimingActions basePath="/api/movies/m1" language="en" {...over} />
  );

  it("Resync now runs the timing check, toasts what it found and refreshes the page", async () => {
    reply = { status: 200, body: { message: "Moved 1.5 seconds earlier." } };
    const onChanged = vi.fn();
    render(actions({ onChanged }));

    fireEvent.click(screen.getByRole("button", { name: "Resync EN subtitle now" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Moved 1.5 seconds earlier."));
    expect(calls).toEqual([{ url: "/api/movies/m1/subtitles/en/resync", method: "POST", body: undefined }]);
    expect(onChanged).toHaveBeenCalled();
  });

  it("an episode resyncs under the episode's own path, and says when the file is missing", async () => {
    reply = { status: 404, body: { message: "That file is missing." } };
    render(actions({ basePath: "/api/series/episodes/e1" }));

    fireEvent.click(screen.getByRole("button", { name: "Resync EN subtitle now" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect(calls[0].url).toBe("/api/series/episodes/e1/subtitles/en/resync");
  });

  it("Out of sync asks first and sends nothing until confirmed", async () => {
    render(actions());

    fireEvent.click(screen.getByRole("button", { name: "Mark the EN subtitle as out of sync" }));

    const dialog = await screen.findByRole("dialog", { name: "This subtitle is out of sync?" });
    expect(within(dialog).getByText("This removes the subtitle, blocklists it for this file, and fetches the next best one.")).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it("when timing was not checked the question says Deluno could not tell either way", async () => {
    render(actions({ timingStatus: "not_checked" }));

    fireEvent.click(screen.getByRole("button", { name: "Mark the EN subtitle as out of sync" }));

    expect(await screen.findByText(/Deluno could not tell either way, and left it exactly as it was\./)).toBeInTheDocument();
  });

  it("confirming removes the subtitle and blocklists it, toasts and refreshes the page", async () => {
    reply = { status: 200, body: { message: "Removed and blocklisted; the next one is being fetched." } };
    const onChanged = vi.fn();
    render(actions({ onChanged }));
    fireEvent.click(screen.getByRole("button", { name: "Mark the EN subtitle as out of sync" }));

    fireEvent.click(await screen.findByRole("button", { name: "Yes, it's out of sync" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Removed and blocklisted; the next one is being fetched."));
    expect(calls).toEqual([{ url: "/api/movies/m1/subtitles/en/reject", method: "POST", body: undefined }]);
    expect(onChanged).toHaveBeenCalled();
  });
});

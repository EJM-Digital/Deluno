import { configure, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The less common turns of the subtitle controls: the server's own words in a toast, a refused blacklist, a pick that is still
 * going when the dialog is asked to close, a search that finishes after the dialog has gone, and a language the list does not know.
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

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const candidate = (token: string, over: Record<string, unknown> = {}) => ({
  providerKey: "opensubtitles", providerName: "OpenSubtitles", downloadToken: token, releaseName: `Rel.${token}`, matchDescription: "Matches", hearingImpaired: false, downloadCount: 3, ...over
});
const dialog = (over: Partial<Parameters<typeof SubtitleManualSearchDialog>[0]> = {}) => (
  <SubtitleManualSearchDialog open onOpenChange={vi.fn()} basePath="/api/movies/m1" language="en" {...over} />
);

beforeEach(() => {
  vi.clearAllMocks();
  mocks.fetchJson.mockReset();
  mocks.authedFetch.mockReset();
});

describe("the by-hand subtitle search dialog, edge turns", () => {
  it("searches nothing while closed", () => {
    render(dialog({ open: false }));
    expect(mocks.fetchJson).not.toHaveBeenCalled();
  });

  it("drops a search answer, or a failure, that arrives after the dialog went away", async () => {
    let resolve!: (rows: unknown[]) => void;
    mocks.fetchJson.mockReturnValueOnce(new Promise((r) => (resolve = r)));
    render(dialog()).unmount();
    resolve([candidate("a")]);

    let reject!: (error: Error) => void;
    mocks.fetchJson.mockReturnValueOnce(new Promise((_, r) => (reject = r)));
    render(dialog()).unmount();
    reject(new Error("offline"));
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByText(/could not search/)).toBeNull();
  });

  it("names a candidate by its provider when it has no release name, and uses the server's words for a pick", async () => {
    mocks.fetchJson.mockResolvedValue([candidate("a", { releaseName: null })]);
    mocks.authedFetch.mockResolvedValue(json(200, { message: "Written as English.srt." }));
    render(dialog());
    expect(await screen.findByRole("button", { name: "Blacklist OpenSubtitles" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Use this one" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Written as English.srt."));
  });

  it("says a refused blacklist did not happen and keeps the candidate", async () => {
    mocks.fetchJson.mockResolvedValue([candidate("a")]);
    mocks.authedFetch.mockResolvedValue(json(500, { title: "Broken" }));
    render(dialog());
    fireEvent.click(await screen.findByRole("button", { name: "Blacklist Rel.a" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect(screen.getByText("Rel.a")).toBeTruthy();
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it("copes with a blacklist that finishes after the list was reset by searching again", async () => {
    mocks.fetchJson.mockResolvedValueOnce([candidate("a")]);
    let finish!: (response: Response) => void;
    mocks.authedFetch.mockReturnValue(new Promise((r) => (finish = r)));
    const { rerender } = render(dialog());
    fireEvent.click(await screen.findByRole("button", { name: "Blacklist Rel.a" }));

    mocks.fetchJson.mockReturnValueOnce(new Promise(() => undefined));
    rerender(dialog({ language: "fr" }));
    finish(json(200, {}));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalled());
    expect(screen.getByText("Searching every provider…")).toBeTruthy();
  });

  it("does not close while a pick is still going", async () => {
    mocks.fetchJson.mockResolvedValue([candidate("a")]);
    let finish!: (response: Response) => void;
    mocks.authedFetch.mockReturnValue(new Promise((r) => (finish = r)));
    const onOpenChange = vi.fn();
    render(dialog({ onOpenChange }));
    fireEvent.click(await screen.findByRole("button", { name: "Use this one" }));

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    expect(onOpenChange).not.toHaveBeenCalled();
    finish(json(200, {}));
    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
  });

  it("uploads with the server's words, and ignores choosing no file", async () => {
    mocks.fetchJson.mockResolvedValue([]);
    mocks.authedFetch.mockResolvedValue(json(200, { message: "Uploaded as French." }));
    render(dialog());
    await screen.findByText(/No provider offered/);
    const input = screen.getByLabelText("Upload a EN subtitle file") as HTMLInputElement;

    fireEvent.change(input, { target: { files: [] } });
    expect(mocks.authedFetch).not.toHaveBeenCalled();

    fireEvent.change(input, { target: { files: [new File(["1"], "a.srt")] } });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Uploaded as French."));
  });
});

describe("the subtitle language card and timing buttons, edge turns", () => {
  it("starts on First one found when the title was saved that way, and names an unknown language by its code", () => {
    render(<SubtitleOverrideCard basePath="/api/movies/m1" languages={["xx", "en"]} mode="first" libraryLanguages={[]} options={[{ code: "en", name: "English" }]} />);
    expect(screen.getByText("XX")).toBeTruthy();
    expect(screen.getByRole("radio", { name: "First one found" }).getAttribute("aria-checked")).toBe("true");
  });

  it("does nothing when the language menu is set back to its empty choice", () => {
    render(<SubtitleOverrideCard basePath="/api/movies/m1" languages={null} mode={null} libraryLanguages={[]} options={[{ code: "en", name: "English" }]} />);
    fireEvent.change(screen.getByRole("combobox", { name: "Add a subtitle language" }), { target: { value: "" } });
    expect(mocks.authedFetch).not.toHaveBeenCalled();
  });

  it("says a refused clear did not happen", async () => {
    mocks.authedFetch.mockResolvedValue(json(500, { title: "Broken" }));
    render(<SubtitleOverrideCard basePath="/api/movies/m1" languages={["en"]} mode="all" libraryLanguages={[]} options={[{ code: "en", name: "English" }]} />);
    fireEvent.click(screen.getByRole("button", { name: "Go back to the library default" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it("uses the server's words after a resync, and explains a refused out-of-sync", async () => {
    mocks.authedFetch.mockResolvedValueOnce(json(200, { message: "Moved it 2 seconds." }));
    render(<SubtitleTimingActions basePath="/api/movies/m1" language="en" />);
    fireEvent.click(screen.getByRole("button", { name: "Resync EN subtitle now" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Moved it 2 seconds."));

    mocks.authedFetch.mockResolvedValueOnce(json(500, { title: "Broken" }));
    fireEvent.click(screen.getByRole("button", { name: "Mark the EN subtitle as out of sync" }));
    fireEvent.click(screen.getByRole("button", { name: "Yes, it's out of sync" }));
    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalled());
  });

  it("uses the server's words after an out-of-sync is confirmed", async () => {
    mocks.authedFetch.mockResolvedValueOnce(json(200, { message: "Gone for good." }));
    render(<SubtitleTimingActions basePath="/api/movies/m1" language="en" />);
    fireEvent.click(screen.getByRole("button", { name: "Mark the EN subtitle as out of sync" }));
    fireEvent.click(screen.getByRole("button", { name: "Yes, it's out of sync" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Gone for good."));
  });
});

describe("the subtitle controls with a server that sends no words of its own", () => {
  it("falls back to plain sentences for a resync, an out-of-sync and an upload", async () => {
    mocks.authedFetch.mockImplementation(async () => json(200, {}));
    const { unmount } = render(<SubtitleTimingActions basePath="/api/movies/m1" language="en" />);
    fireEvent.click(screen.getByRole("button", { name: "Resync EN subtitle now" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("The subtitle was resynced."));
    fireEvent.click(screen.getByRole("button", { name: "Mark the EN subtitle as out of sync" }));
    fireEvent.click(screen.getByRole("button", { name: "Yes, it's out of sync" }));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("That subtitle was removed and blocklisted."));
    unmount();

    mocks.fetchJson.mockResolvedValue([]);
    render(dialog());
    await screen.findByText(/No provider offered/);
    fireEvent.change(screen.getByLabelText("Upload a EN subtitle file"), { target: { files: [new File(["1"], "a.srt")] } });
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Your subtitle was uploaded and is now held."));
  });

  it("finishes an upload cleanly even when the dialog was closed from outside and the file box is gone", async () => {
    mocks.fetchJson.mockResolvedValue([]);
    let finish!: (response: Response) => void;
    mocks.authedFetch.mockReturnValue(new Promise((r) => (finish = r)));
    const { rerender } = render(dialog());
    await screen.findByText(/No provider offered/);
    fireEvent.change(screen.getByLabelText("Upload a EN subtitle file"), { target: { files: [new File(["1"], "a.srt")] } });

    rerender(dialog({ open: false }));
    expect(screen.queryByLabelText("Upload a EN subtitle file")).toBeNull();
    finish(json(200, {}));
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("Your subtitle was uploaded and is now held."));
  });
});

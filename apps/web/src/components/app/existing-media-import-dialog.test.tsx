import { configure, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LibraryItem } from "../../lib/api";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * "Review existing files" on a library (coverage register, area library): the dialog reads the library folder fifty items at a time,
 * lists each folder or file with title, year, relative path, quality and size, lets the person select what can be imported, and posts
 * only the chosen paths. What the server reads and writes on a real folder is proven by the real-disk backend tests named in the register.
 */
const mocks = vi.hoisted(() => ({
  fetchJson: vi.fn(),
  authedFetch: vi.fn(),
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), info: vi.fn() })
}));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));
vi.mock("../shell/toaster", () => ({ toast: mocks.toast }));

const { ExistingMediaImportDialog } = await import("./existing-media-import-dialog");

const library = { id: "lib1", name: "Films", mediaType: "movies", rootPath: "/films" } as unknown as LibraryItem;

const item = (name: string, over: Record<string, unknown> = {}) => ({
  sourcePath: `/films/${name}`, relativePath: name, title: name.replace(/ \(\d{4}\)$/, ""), year: 2016, isDirectory: true, canImport: true, issueDetail: null,
  detectedQuality: "Bluray 1080p", fileSizeBytes: 4_294_967_296, ...over
});

let pages: Array<{ items: unknown[]; hasMore: boolean; nextCursor: string | null }>;
let posted: Array<{ url: string; body: any }>;
let importReply: { status: number; body: unknown };

function renderDialog(onImported = vi.fn(), onOpenChange = vi.fn()) {
  render(<ExistingMediaImportDialog open library={library} onOpenChange={onOpenChange} onImported={onImported} />);
  return { onImported, onOpenChange };
}

beforeEach(() => {
  pages = [{ items: [item("Arrival (2016)"), item("Heat (1995)", { year: 1995, detectedQuality: null }), item("notes.txt", { title: "notes", isDirectory: false, canImport: false, issueDetail: "This file is not a supported video." })], hasMore: false, nextCursor: null }];
  posted = [];
  importReply = { status: 200, body: { importedCount: 2, issues: [] } };
  vi.clearAllMocks();
  mocks.fetchJson.mockReset().mockImplementation(async (path: string) => {
    const cursor = new URLSearchParams(path.split("?")[1]).get("cursor");
    return pages[cursor ? Number(cursor) : 0];
  });
  mocks.authedFetch.mockReset().mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    posted.push({ url: String(input), body: JSON.parse(String(init?.body)) });
    return new Response(JSON.stringify(importReply.body), { status: importReply.status, headers: { "Content-Type": "application/json" } });
  });
});

describe("Review existing media", () => {
  it("reads the library folder fifty items at a time and lists each with title, year, relative path, quality and size", async () => {
    renderDialog();

    expect(await screen.findByRole("checkbox", { name: "Select Arrival" })).toBeInTheDocument();
    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/libraries/lib1/import-existing/preview?take=50");
    expect(screen.getByRole("checkbox", { name: "Select Heat" })).toBeInTheDocument();
    expect(screen.getAllByText(/Bluray 1080p/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Quality unknown/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/4\.0 GB/).length).toBeGreaterThan(0);
    expect(screen.getByText("3 items shown")).toBeInTheDocument();
  });

  it("a row that cannot be imported is disabled and shows its issue", async () => {
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });

    expect(screen.getByRole("checkbox", { name: "Select notes" })).toBeDisabled();
    expect(screen.getByText("This file is not a supported video.")).toBeInTheDocument();
  });

  it("Select all on this page selects only the importable rows and the footer counts them", async () => {
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });

    fireEvent.click(screen.getByRole("checkbox", { name: "Select all on this page" }));

    expect(screen.getByRole("checkbox", { name: "Select Arrival" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Select Heat" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Select notes" })).not.toBeChecked();
    expect(screen.getByText("2 selected on this page")).toBeInTheDocument();
  });

  it("Import selected posts only the chosen paths, toasts how many were added and reads the page again", async () => {
    const { onImported } = renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Arrival" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Heat" }));
    const readsBefore = mocks.fetchJson.mock.calls.length;

    fireEvent.click(screen.getByRole("button", { name: "Import selected" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("2 movies reviewed and added."));
    expect(posted).toEqual([{ url: "/api/libraries/lib1/import-existing/selected", body: { sourcePaths: ["/films/Arrival (2016)", "/films/Heat (1995)"] } }]);
    expect(onImported).toHaveBeenCalled();
    await waitFor(() => expect(mocks.fetchJson.mock.calls.length).toBeGreaterThan(readsBefore));
  });

  it("Import selected is off until something is selected", async () => {
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });

    expect(screen.getByRole("button", { name: "Import selected" })).toBeDisabled();
  });

  it("lists up to five warnings or skipped items after an import", async () => {
    importReply = { status: 200, body: { importedCount: 1, issues: Array.from({ length: 7 }, (_, index) => ({ sourcePath: `/films/odd-${index}`, kind: "skipped", detail: `Skipped ${index}.` })) } };
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Arrival" }));

    fireEvent.click(screen.getByRole("button", { name: "Import selected" }));

    expect(await screen.findByText("1 selected item added")).toBeInTheDocument();
    expect(screen.getByText("7 warnings or skipped items need review.")).toBeInTheDocument();
    expect(screen.getByText(/Skipped 4\./)).toBeInTheDocument();
    expect(screen.queryByText(/Skipped 5\./)).toBeNull();
  });

  it("a TV library says TV show in its toast", async () => {
    importReply = { status: 200, body: { importedCount: 1, issues: [] } };
    render(<ExistingMediaImportDialog open library={{ ...library, mediaType: "tv" } as unknown as LibraryItem} onOpenChange={vi.fn()} />);
    await screen.findByRole("checkbox", { name: "Select Arrival" });
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Arrival" }));

    fireEvent.click(screen.getByRole("button", { name: "Import selected" }));

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith("1 TV show reviewed and added."));
  });

  it("an import the server refuses toasts the server's words and imports nothing", async () => {
    mocks.authedFetch.mockResolvedValue(new Response("That path climbs out of the library.", { status: 400 }));
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Arrival" }));

    fireEvent.click(screen.getByRole("button", { name: "Import selected" }));

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith("That path climbs out of the library."));
    expect(mocks.toast.success).not.toHaveBeenCalled();
  });

  it("Next and Previous move through the pages by cursor and each page starts with nothing selected", async () => {
    pages = [
      { items: [item("Arrival (2016)")], hasMore: true, nextCursor: "1" },
      { items: [item("Heat (1995)", { year: 1995 })], hasMore: false, nextCursor: null }
    ];
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });
    expect(screen.getByRole("button", { name: /Previous/ })).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: "Select Arrival" }));

    fireEvent.click(screen.getByRole("button", { name: /Next/ }));

    expect(await screen.findByRole("checkbox", { name: "Select Heat" })).toBeInTheDocument();
    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/libraries/lib1/import-existing/preview?take=50&cursor=1");
    expect(screen.getByText("0 selected on this page")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Next/ })).toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: /Previous/ }));

    expect(await screen.findByRole("checkbox", { name: "Select Arrival" })).toBeInTheDocument();
  });

  it("says no supported files or folders were found for an empty folder", async () => {
    pages = [{ items: [], hasMore: false, nextCursor: null }];
    renderDialog();

    expect(await screen.findByText("No supported files or folders were found in this library folder.")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Select all on this page" })).toBeDisabled();
  });

  it("says Reading folder… while the page loads", async () => {
    let release!: (page: unknown) => void;
    mocks.fetchJson.mockReturnValue(new Promise((resolve) => (release = resolve)));
    renderDialog();

    expect(await screen.findByText("Reading folder…")).toBeInTheDocument();
    release(pages[0]);
    expect(await screen.findByRole("checkbox", { name: "Select Arrival" })).toBeInTheDocument();
  });

  it("says the folder could not be reviewed when the read fails", async () => {
    mocks.fetchJson.mockRejectedValue(new Error("The folder could not be reviewed."));
    renderDialog();

    expect(await screen.findByText("The folder could not be reviewed.")).toBeInTheDocument();
  });

  it("names the library and its folder and says nothing is added until the person selects", async () => {
    renderDialog();
    await screen.findByRole("checkbox", { name: "Select Arrival" });
    const dialog = screen.getByRole("dialog", { name: "Review existing media" });

    expect(within(dialog).getByText("Films")).toBeInTheDocument();
    expect(within(dialog).getByText("/films")).toBeInTheDocument();
    expect(within(dialog).getByText(/Nothing is added until you select the files or folders you want to bring in\./)).toBeInTheDocument();
  });
});

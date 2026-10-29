import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import { LogFilesCard } from "./log-files-card";

/** System > Log files card (#1329): the states a person sees while it loads, fails, is empty, or a download or delete goes wrong. */

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: toastMock, Toaster: () => null }));

const today = { name: "deluno-2026-10-29.jsonl", sizeBytes: 2048, lastWrittenUtc: new Date().toISOString() };
const older = { name: "deluno-2026-10-28.jsonl", sizeBytes: 512, lastWrittenUtc: new Date(Date.now() - 86_400_000).toISOString() };

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function serve(list: unknown, folder: string | null = "/data/logs", other?: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/api/system/log-files") return list === "fail" ? json({}, 404) : json(list);
    if (url === "/api/system/log-files/folder") return folder === null ? json({}, 500) : json({ path: folder });
    return other ? other(url, init) : json({});
  });
}

const reads = (url: string) => vi.mocked(authedFetch).mock.calls.filter((call) => call[0] === url).length;

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => "blob:mock");
  URL.revokeObjectURL = vi.fn();
});

afterEach(() => {
  vi.clearAllMocks();
});

describe("Log files states", () => {
  it("says Loading… while the list is on its way, then 'No log files yet' with the folder path when there are none", async () => {
    serve([]);
    render(<LogFilesCard />);

    expect(screen.getByText("Loading…")).toBeInTheDocument();
    expect(await screen.findByText("No log files yet")).toBeInTheDocument();
    expect(screen.getByText("Files will appear under /data/logs.")).toBeInTheDocument();
    expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
  });

  it("says 'Log files could not be loaded' when the list cannot be read, and Refresh tries again", async () => {
    serve("fail");
    render(<LogFilesCard />);
    expect(await screen.findByText("Log files could not be loaded")).toBeInTheDocument();

    serve([older]);
    fireEvent.click(screen.getByRole("button", { name: /Refresh/ }));
    expect(await screen.findByText(older.name)).toBeInTheDocument();
    expect(screen.queryByText("Log files could not be loaded")).not.toBeInTheDocument();
  });

  it("Refresh reads the list and the folder again", async () => {
    serve([older]);
    render(<LogFilesCard />);
    await screen.findByText(older.name);
    expect(reads("/api/system/log-files")).toBe(1);
    expect(reads("/api/system/log-files/folder")).toBe(1);

    serve([older, today]);
    fireEvent.click(screen.getByRole("button", { name: /Refresh/ }));
    expect(await screen.findByText(today.name)).toBeInTheDocument();
    expect(reads("/api/system/log-files")).toBe(2);
    expect(reads("/api/system/log-files/folder")).toBe(2);
  });
});

describe("Open logs folder", () => {
  it("copies the folder path to the clipboard and says so", async () => {
    serve([older]);
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    render(<LogFilesCard />);
    await screen.findByText(older.name);

    fireEvent.click(screen.getByRole("button", { name: /Open logs folder/ }));

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith("Logs folder path copied"));
    expect(writeText).toHaveBeenCalledWith("/data/logs");
  });

  it("shows the path itself when the browser would not copy it", async () => {
    serve([older]);
    Object.defineProperty(navigator, "clipboard", { value: { writeText: vi.fn().mockRejectedValue(new Error("denied")) }, configurable: true });
    render(<LogFilesCard />);
    await screen.findByText(older.name);

    fireEvent.click(screen.getByRole("button", { name: /Open logs folder/ }));

    await waitFor(() => expect(toastMock.info).toHaveBeenCalledWith("/data/logs"));
    expect(toastMock.success).not.toHaveBeenCalled();
  });
});

describe("A download or delete that goes wrong", () => {
  it("a failed download shows an error toast, and the buttons are off while it runs and on again after", async () => {
    let finish: (response: Response) => void = () => undefined;
    serve([older, today], "/data/logs", (url) => (url.endsWith("/download") ? new Promise<Response>((resolve) => { finish = resolve; }) : json({})));
    render(<LogFilesCard />);
    await screen.findByText(older.name);

    fireEvent.click(screen.getByRole("button", { name: `Download ${older.name}` }));
    await waitFor(() => expect(screen.getByRole("button", { name: `Download ${today.name}` })).toBeDisabled());
    expect(screen.getByRole("button", { name: `Delete ${older.name}` })).toBeDisabled();

    finish(new Response(null, { status: 500 }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("The file could not be downloaded."));
    await waitFor(() => expect(screen.getByRole("button", { name: `Download ${today.name}` })).not.toBeDisabled());
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });

  it("a delete the server refuses shows its words, keeps the file listed and does not say it was deleted", async () => {
    serve([older], "/data/logs", (_url, init) => (init?.method === "DELETE" ? json({ errors: { name: ["Something else is holding the file."] } }, 400) : json({})));
    render(<LogFilesCard />);
    await screen.findByText(older.name);

    fireEvent.click(screen.getByRole("button", { name: `Delete ${older.name}` }));
    fireEvent.click(screen.getByRole("button", { name: "Delete file" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Something else is holding the file."));
    expect(toastMock.success).not.toHaveBeenCalled();
    expect(screen.getByText(older.name)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: `Delete ${older.name}` })).not.toBeDisabled());
  });

  it("today's log, still being written, is refused with the server's reason and stays listed", async () => {
    serve([today], "/data/logs", (_url, init) => (init?.method === "DELETE"
      ? json({ errors: { name: ["Today's log file is still being written to and cannot be deleted."] } }, 400)
      : json({})));
    render(<LogFilesCard />);
    await screen.findByText(today.name);

    fireEvent.click(screen.getByRole("button", { name: `Delete ${today.name}` }));
    fireEvent.click(screen.getByRole("button", { name: "Delete file" }));

    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Today's log file is still being written to and cannot be deleted."));
    expect(screen.getByText(today.name)).toBeInTheDocument();
  });
});

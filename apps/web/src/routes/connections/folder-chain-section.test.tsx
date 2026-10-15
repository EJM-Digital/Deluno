import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { FolderChainSection, CHECK_TIMEOUT_MS } = await import("./folder-chain-section");

const mismatch = {
  libraryId: "tv",
  libraryName: "TV",
  usesProcessor: true,
  processorName: "Weir",
  allMatch: false,
  clients: [{
    clientId: "t",
    clientName: "Transmission",
    protocol: "transmission",
    category: "TV",
    links: [
      { step: "category", status: "ok", message: "Deluno sends each TV download to C:\\Deluno\\Downloads-Complete\\Transmission\\TV." },
      { step: "folder", status: "problem", message: "Transmission saves TV to C:\\Deluno\\Downloads-Complete\\Transmission\\TV, but the TV library and Weir expect C:\\Downloads\\Completed\\TV." }
    ]
  }],
  libraryLinks: [{ step: "ready", status: "ok", message: "E:\\Weir\\Ready\\TV is the folder Weir leaves finished files in, and Deluno can reach it." }]
};

describe("the folder check (#616)", () => {
  afterEach(() => fetchJson.mockReset());

  it("names a client that saves where the library does not look, and says so up top", async () => {
    fetchJson.mockResolvedValue(mismatch);

    render(<FolderChainSection libraryId="tv" />);

    expect(await screen.findByText("Something doesn't match")).toBeTruthy();
    expect(screen.getByText("Doesn't match")).toBeTruthy();
    expect(screen.getByText(/but the TV library and Weir expect C:\\Downloads\\Completed\\TV/)).toBeTruthy();
    expect(screen.getByText("Ready folder")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledWith("/api/libraries/tv/folder-chain", expect.anything());
  });

  it("shows where a client downloads beside where it finishes, and flags the two being the same (#1164)", async () => {
    fetchJson.mockResolvedValue({
      ...mismatch,
      clients: [{
        clientId: "d", clientName: "Deluge", protocol: "deluge", category: "movies",
        links: [
          { step: "incomplete", status: "problem", message: "Deluge downloads movies into /downloads/completed/movies, which is the same as the folder they finish in. Half-finished files sit where the processor and the library look." },
          { step: "folder", status: "ok", message: "Deluge saves movies to /downloads/completed/movies." }
        ]
      }]
    });

    render(<FolderChainSection libraryId="tv" />);

    expect(await screen.findByText("Deluge · where it downloads")).toBeTruthy();
    expect(screen.getByText("Deluge · where it finishes")).toBeTruthy();
    expect(screen.getByText(/Half-finished files sit where the processor and the library look/)).toBeTruthy();
  });

  it("says in plain words that a client saves inside a library (#1328)", async () => {
    fetchJson.mockResolvedValue({
      ...mismatch,
      clients: [{
        clientId: "q", clientName: "qBittorrent", protocol: "qbittorrent", category: "movies",
        links: [
          { step: "in-library", status: "problem", message: "qBittorrent saves movies to /media/movies, which is inside the Movies library, or holds it. Point the movies folder in qBittorrent somewhere outside every library." }
        ]
      }]
    });

    render(<FolderChainSection libraryId="tv" />);

    expect(await screen.findByText("qBittorrent · saves inside a library")).toBeTruthy();
    expect(screen.getByText(/somewhere outside every library/)).toBeTruthy();
  });

  it("checks again on request", async () => {
    fetchJson.mockResolvedValue({ ...mismatch, allMatch: true, clients: [], libraryLinks: [] });

    render(<FolderChainSection libraryId="tv" />);
    expect(await screen.findByText("Everything matches")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
  });

  it("never shows the last check's result beside a new check's error", async () => {
    fetchJson.mockResolvedValueOnce(mismatch);
    render(<FolderChainSection libraryId="tv" />);
    expect(await screen.findByText("Doesn't match")).toBeTruthy();

    fetchJson.mockRejectedValueOnce(new Error("Deluno could not reach the server."));
    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect((await screen.findByRole("alert")).textContent).toContain("could not reach");
    expect(screen.queryByText("Doesn't match")).toBeNull();
  });

  it("says when the folders could not be checked", async () => {
    fetchJson.mockRejectedValue(new Error("Deluno could not reach the server."));

    render(<FolderChainSection libraryId="tv" />);

    expect((await screen.findByRole("alert")).textContent).toContain("could not reach");
  });

  it("stops checking, and says so, when the request never settles (#1079)", async () => {
    vi.useFakeTimers();
    try {
      fetchJson.mockReturnValue(new Promise(() => {}));

      render(<FolderChainSection libraryId="tv" />);
      expect(screen.getByText("Checking…")).toBeTruthy();

      await act(async () => { await vi.advanceTimersByTimeAsync(CHECK_TIMEOUT_MS + 100); });

      expect(screen.queryByText("Checking…")).toBeNull();
      expect(screen.getByRole("alert").textContent).toContain("took too long");
      expect((screen.getByRole("button", { name: "Check again" }) as HTMLButtonElement).disabled).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("says a plain sentence when the request is aborted", async () => {
    fetchJson.mockRejectedValue(Object.assign(new Error("The operation was aborted."), { name: "AbortError" }));

    render(<FolderChainSection libraryId="tv" />);

    expect((await screen.findByRole("alert")).textContent).toContain("took too long");
  });
});

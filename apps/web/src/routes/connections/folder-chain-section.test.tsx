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

  it("checks again on request", async () => {
    fetchJson.mockResolvedValue({ ...mismatch, allMatch: true, clients: [], libraryLinks: [] });

    render(<FolderChainSection libraryId="tv" />);
    expect(await screen.findByText("Everything matches")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    await vi.waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(2));
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

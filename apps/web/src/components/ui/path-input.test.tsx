import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());

vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: fetchJsonMock
}));

import { ApiRequestError } from "../../lib/api";
import { PathInput, browserIsOnServerMachine } from "./path-input";

const roots = {
  currentPath: null,
  parentPath: null,
  entries: [{ name: "C:\\", path: "C:\\", kind: "root", description: null }]
};

describe("PathInput: Choose folder (#1170)", () => {
  beforeEach(() => {
    fetchJsonMock.mockReset();
    fetchJsonMock.mockImplementation(async (path: string) => {
      if (path.startsWith("/api/filesystem/directories")) return roots;
      throw new Error(`unexpected ${path}`);
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("knows which browsers are on the server's own machine", () => {
    expect(browserIsOnServerMachine("localhost")).toBe(true);
    expect(browserIsOnServerMachine("127.0.0.1")).toBe(true);
    expect(browserIsOnServerMachine("[::1]")).toBe(true);
    expect(browserIsOnServerMachine("192.168.1.10")).toBe(false);
    expect(browserIsOnServerMachine("deluno.home")).toBe(false);
  });

  it("opens the in-app folder browser, and never calls the Windows picker, from another device", async () => {
    vi.stubGlobal("location", { hostname: "192.168.1.10" });
    render(<PathInput value="" onChange={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));

    await waitFor(() => expect(screen.getByText("Use typed path")).toBeInTheDocument());
    expect(fetchJsonMock.mock.calls.some(([path]) => path === "/api/filesystem/native-folder-picker")).toBe(false);
    expect(fetchJsonMock.mock.calls.some(([path]) => String(path).startsWith("/api/filesystem/directories"))).toBe(true);
  });

  it("falls back to the in-app browser when the server refuses the Windows picker", async () => {
    vi.stubGlobal("location", { hostname: "localhost" });
    fetchJsonMock.mockImplementation(async (path: string) => {
      if (path === "/api/filesystem/native-folder-picker") throw new ApiRequestError("Not from here.", 403, path, "");
      return roots;
    });
    render(<PathInput value="" onChange={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));

    await waitFor(() => expect(screen.getByText(/Windows picker is unavailable/)).toBeInTheDocument());
  });
});

describe("PathInput: example folders follow the server (#1138)", () => {
  async function openWith(windows: boolean) {
    vi.stubGlobal("location", { hostname: "192.168.1.10" });
    fetchJsonMock.mockImplementation(async () => ({ ...roots, windows }));
    render(<PathInput value="" onChange={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));
    await waitFor(() => expect(screen.getByText("Use typed path")).toBeInTheDocument());
  }

  afterEach(() => vi.unstubAllGlobals());

  it("offers only Windows examples, and prints the network path with single backslashes, on a Windows server", async () => {
    await openWith(true);
    await waitFor(() => expect(screen.getByRole("button", { name: "D:\\Downloads" })).toBeInTheDocument());
    expect(screen.queryByRole("button", { name: "/downloads" })).toBeNull();
    expect(screen.getByText(/Use a path such as \\\\nas\\media/)).toBeInTheDocument();
    expect(screen.queryByText(/\\\\\\\\nas/)).toBeNull();
    expect(screen.queryByText(/review details and take related actions/)).toBeNull();
  });

  it("offers only container examples on a Linux server", async () => {
    await openWith(false);
    await waitFor(() => expect(screen.getByRole("button", { name: "/downloads" })).toBeInTheDocument());
    expect(screen.queryByRole("button", { name: "D:\\Downloads" })).toBeNull();
  });
});

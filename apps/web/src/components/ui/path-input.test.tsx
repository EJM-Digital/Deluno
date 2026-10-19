import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());

vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: fetchJsonMock
}));

import { ApiRequestError } from "../../lib/api";
import { PathInput, browserEntries, browserIsOnServerMachine } from "./path-input";

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

describe("PathInput: drives show only in the list of drives", () => {
  const drive = { name: "Media (Z:)", path: "Z:\\", kind: "root" as const, description: "Network drive" };
  const folder = { name: "Movies", path: "/media/Movies", kind: "directory" as const, description: null };

  it("keeps drives in the top list and out of a folder's listing", () => {
    expect(browserEntries({ currentPath: null, parentPath: null, entries: [drive, folder] })).toEqual([drive, folder]);
    expect(browserEntries({ currentPath: "/media", parentPath: "/", entries: [drive, folder] })).toEqual([folder]);
  });

  it("does not draw a drive at the top of a folder's listing", async () => {
    vi.stubGlobal("location", { hostname: "192.168.1.10" });
    fetchJsonMock.mockImplementation(async () => ({ currentPath: "/media", parentPath: "/", entries: [drive, folder] }));
    render(<PathInput value="/media" onChange={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));
    await waitFor(() => expect(screen.getByText("Movies")).toBeInTheDocument());
    expect(screen.queryByText("Media (Z:)")).toBeNull();
    expect(screen.queryByText("Network drive")).toBeNull();
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

describe("PathInput: the Windows folder window and the Check button", () => {
  const diagnostic = {
    normalizedPath: "/downloads/movies", exists: true, isDirectory: true, readable: true, writable: false,
    isUncPath: false, isLikelyDockerPath: true, message: "This folder is readable but Deluno cannot write to it.", warnings: ["Give Deluno write access."]
  };

  beforeEach(() => {
    fetchJsonMock.mockReset();
    vi.stubGlobal("location", { hostname: "localhost" });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("applies the folder the Windows window on the server machine returns, after asking the server to open it at the current path", async () => {
    fetchJsonMock.mockResolvedValue({ path: "/srv/media/Films", cancelled: false });
    const onChange = vi.fn();
    render(<PathInput value={"/srv/media"} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));

    await waitFor(() => expect(onChange).toHaveBeenCalledWith("/srv/media/Films"));
    const [path, init] = fetchJsonMock.mock.calls[0];
    expect(path).toBe("/api/filesystem/native-folder-picker");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toEqual({ initialPath: "/srv/media" });
  });

  it("changes nothing when the Windows window is closed without choosing", async () => {
    fetchJsonMock.mockResolvedValue({ path: null, cancelled: true });
    const onChange = vi.fn();
    render(<PathInput value={"/srv/media"} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));

    await waitFor(() => expect(fetchJsonMock).toHaveBeenCalled());
    await waitFor(() => expect((screen.getByRole("button", { name: /Choose folder/ }) as HTMLButtonElement).disabled).toBe(false));
    expect(onChange).not.toHaveBeenCalled();
  });

  it("Check asks the server about the typed path and shows its message with Exists, Directory, Readable, Docker path and the warnings", async () => {
    fetchJsonMock.mockResolvedValue(diagnostic);
    render(<PathInput value="/downloads/movies" onChange={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "Check" }));

    expect(await screen.findByText("This folder is readable but Deluno cannot write to it.")).toBeInTheDocument();
    const [path, init] = fetchJsonMock.mock.calls[0];
    expect(path).toBe("/api/filesystem/path-diagnostics");
    expect(JSON.parse(init.body)).toEqual({ path: "/downloads/movies" });
    for (const badge of ["Exists", "Directory", "Readable", "Writable", "Docker path"]) expect(screen.getByText(badge)).toBeInTheDocument();
    expect(screen.getByText("Give Deluno write access.")).toBeInTheDocument();
  });

  it("will not Check an empty path, so nothing is sent", () => {
    render(<PathInput value="" onChange={() => {}} />);
    expect((screen.getByRole("button", { name: "Check" }) as HTMLButtonElement).disabled).toBe(true);
    expect(fetchJsonMock).not.toHaveBeenCalled();
  });

  it("shows the server's own words when the check itself fails", async () => {
    fetchJsonMock.mockRejectedValue(new Error("The check could not run."));
    render(<PathInput value="/x" onChange={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: "Check" }));
    expect(await screen.findByText("The check could not run.")).toBeInTheDocument();
  });
});

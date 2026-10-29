import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: fetchJsonMock
}));

import { ApiRequestError } from "../../lib/api";
import { PathInput } from "./path-input";

const roots = { windows: false, currentPath: null, parentPath: null, entries: [{ name: "media", path: "/media", kind: "directory", description: null }, { name: "tv", path: "/tv", kind: "directory", description: null }] };
const media = { windows: false, currentPath: "/media", parentPath: "/", entries: [{ name: "films", path: "/media/films", kind: "directory", description: null }] };
const rootOfDisk = { windows: false, currentPath: "/", parentPath: null, entries: [{ name: "media", path: "/media", kind: "directory", description: null }] };

const listing = (map: Record<string, unknown>) =>
  fetchJsonMock.mockImplementation(async (path: string) => {
    if (typeof path !== "string") return undefined;
    if (path.startsWith("/api/filesystem/directories")) {
      const query = path.includes("?path=") ? decodeURIComponent(path.split("?path=")[1]) : "";
      const reply = map[query];
      if (reply instanceof Error) throw reply;
      if (reply === undefined) throw new Error(`unexpected ${path}`);
      return reply;
    }
    throw new Error(`unexpected ${path}`);
  });

beforeEach(() => fetchJsonMock.mockReset());
afterEach(() => vi.unstubAllGlobals());

async function openBrowser(onChange = vi.fn(), value = "") {
  vi.stubGlobal("location", { hostname: "192.168.1.10" });
  render(<PathInput value={value} onChange={onChange} />);
  fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));
  await screen.findByText("Use typed path");
  return onChange;
}

describe("PathInput: the server folder browser (low rows)", () => {
  it("Open goes into a folder, Up returns to its parent, Roots returns to the top, and Select chooses that row", async () => {
    listing({ "": roots, "/media": media, "/": rootOfDisk });
    const onChange = await openBrowser();
    expect(await screen.findByText("Computer roots")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Up" })).toBeDisabled();

    fireEvent.click(screen.getAllByRole("button", { name: "Open" })[0]);
    expect(await screen.findByText("/media")).toBeInTheDocument();
    expect(screen.getByText("films")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Up" }));
    await waitFor(() => expect(fetchJsonMock).toHaveBeenCalledWith("/api/filesystem/directories?path=%2F"));
    expect(await screen.findByText("/")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Roots" }));
    expect(await screen.findByText("Computer roots")).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("button", { name: "Select" })[1]);
    expect(onChange).toHaveBeenCalledWith("/tv");
  });

  it("Use this folder chooses the folder being shown and closes the browser", async () => {
    listing({ "": roots, "/media": media });
    const onChange = await openBrowser(vi.fn(), "/media");
    expect(await screen.findByText("films")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Use this folder" }));

    expect(onChange).toHaveBeenCalledWith("/media");
    await waitFor(() => expect(screen.queryByText("Use typed path")).toBeNull());
  });

  it("a path the server cannot open (400, 403 or 404) shows the drive list with a notice saying which path", async () => {
    for (const status of [400, 403, 404]) {
      fetchJsonMock.mockReset();
      listing({ "": roots, "/gone": new ApiRequestError("nope", status) });
      const { unmount } = render(<div />);
      unmount();
      vi.stubGlobal("location", { hostname: "192.168.1.10" });
      const view = render(<PathInput value="/gone" onChange={() => {}} />);
      fireEvent.click(within(view.container).getByRole("button", { name: /Choose folder/ }));

      expect(await screen.findByText('"/gone" could not be opened. Showing available drives instead.')).toBeInTheDocument();
      expect(screen.getByText("Computer roots")).toBeInTheDocument();
      expect(screen.getByText("media")).toBeInTheDocument();
      view.unmount();
    }
  });

  it("any other failure shows the error instead of the list", async () => {
    listing({ "": new ApiRequestError("The server did not answer.", 500) });
    await openBrowser().catch(() => undefined);
    expect(await screen.findByText("The server did not answer.")).toBeInTheDocument();
  });

  it("a path typed by hand is used with Use typed path, and an example button fills the box first", async () => {
    listing({ "": roots });
    const onChange = await openBrowser();
    const box = screen.getByPlaceholderText(/Type or paste a path/);
    const use = screen.getByRole("button", { name: "Use typed path" });
    expect(use).toBeDisabled();

    fireEvent.click(await screen.findByRole("button", { name: "/downloads" }));
    expect(box).toHaveValue("/downloads");
    fireEvent.change(box, { target: { value: "  /data/films  " } });
    fireEvent.click(use);

    expect(onChange).toHaveBeenCalledWith("/data/films");
  });
});

describe("PathInput: Stop waiting (low rows)", () => {
  it("while the server's folder window is open Stop waiting cancels the wait and shows the in-app browser", async () => {
    vi.stubGlobal("location", { hostname: "localhost" });
    fetchJsonMock.mockImplementation((path: string, init?: { signal?: AbortSignal }) => {
      if (path === "/api/filesystem/native-folder-picker") {
        return new Promise((_resolve, reject) => init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))));
      }
      if (typeof path === "string" && path.startsWith("/api/filesystem/directories")) return Promise.resolve(roots);
      return Promise.resolve(undefined);
    });
    render(<PathInput value="" onChange={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: /Choose folder/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Stop waiting" }));

    expect(await screen.findByText(/Closed the folder window on the server's screen\?/)).toBeInTheDocument();
    expect(await screen.findByText("Use typed path")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Stop waiting" })).toBeNull();
  });
});

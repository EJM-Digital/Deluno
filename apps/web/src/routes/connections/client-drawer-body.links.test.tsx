import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { DownloadClientItem } from "../../lib/api";
import { ClientDrawerBody } from "./client-drawer-body";
import { emptyClientForm } from "./forms";

// #1329: a saved download client's version, warnings and file-location links, and the route-on-add switch.

const fetchJson = vi.hoisted(() => vi.fn(async (_url: string): Promise<unknown> => []));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(async () => new Response("[]", { status: 200 })) }));

afterEach(cleanup);

const saved = () => ({
  id: "c1", name: "My qBittorrent", protocol: "qbittorrent", host: "localhost", port: 8080, endpointUrl: "http://localhost:8080", useSsl: false, addPaused: false, moviesCategory: "movies", tvCategory: "tv",
  categoryTemplate: null, priority: 1, isEnabled: true, healthStatus: "healthy", lastHealthMessage: "Reached it.", createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z",
  lastHealthTestUtc: new Date(Date.now() - 4 * 60_000).toISOString(), lastHealthLatencyMs: 12
}) as unknown as DownloadClientItem;

type Extra = Partial<React.ComponentProps<typeof ClientDrawerBody>>;
function open(editing: DownloadClientItem | null, extra: Extra = {}, form = emptyClientForm()) {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <ClientDrawerBody
        form={form} setForm={() => {}} editing={editing} errors={{}} clearError={() => {}} mappings={[]} newMapping={{ remotePath: "", localPath: "" }} setNewMapping={() => {}}
        busy={null} onAddMapping={() => {}} onRemoveMapping={() => {}} onTestMapping={() => {}} onRemove={() => {}} {...extra}
      />
    </QueryClientProvider>
  );
}
const mapping = { id: "m1", downloadClientId: "c1", remotePath: "/downloads", localPath: "D:\\Downloads", isEnabled: true, priority: 10, createdUtc: "", updatedUtc: "" };
const warningsButton = () => screen.getByRole("button", { name: "Clear all of My qBittorrent's warnings" }) as HTMLButtonElement;

describe("a saved client's version and warnings", () => {
  it("shows the version line only when there is one", () => {
    open(saved(), { versionLine: "qBittorrent 5.0.1" });
    expect(screen.getByText("qBittorrent 5.0.1")).toBeTruthy();
    cleanup();
    open(saved());
    expect(screen.queryByText("Version")).toBeNull();
  });

  it("lists the client's warnings, and Clear warnings asks the screen to clear them unless something else is busy", () => {
    const onClearWarnings = vi.fn();
    open(saved(), { warnings: ["Disk nearly full."], onClearWarnings });
    expect(screen.getByText("Disk nearly full.")).toBeTruthy();
    fireEvent.click(warningsButton());
    expect(onClearWarnings).toHaveBeenCalledTimes(1);
    cleanup();
    open(saved(), { warnings: ["Disk nearly full."] });
    expect(warningsButton().disabled).toBe(true);
    cleanup();
    open(saved(), { warnings: ["Disk nearly full."], onClearWarnings, busy: "warnings:clear:c1" });
    expect(warningsButton().disabled).toBe(true);
    expect(document.querySelector(".animate-spin")).not.toBeNull();
    cleanup();
    open(saved(), { warnings: [] });
    expect(screen.queryByText("Warnings from the client")).toBeNull();
  });
});

describe("the form of a new client", () => {
  it("the route-on-add switch asks to change, and works with nothing listening", () => {
    const onRouteOnAddChange = vi.fn();
    open(null, { routeOnAdd: true, onRouteOnAddChange });
    fireEvent.click(screen.getByRole("switch", { name: /Use it for libraries with no client yet/ }));
    expect(onRouteOnAddChange).toHaveBeenCalledWith(false);
    cleanup();
    open(null);
    fireEvent.click(screen.getByRole("switch", { name: /Use it for libraries with no client yet/ }));
  });

  it("warns when both categories are the same, and otherwise says they should differ", () => {
    open(null, {}, { ...emptyClientForm(), moviesCategory: "all", tvCategory: "all" });
    expect(screen.getByText("Same as movies — downloads will mix in the client.")).toBeTruthy();
    cleanup();
    open(null, {}, { ...emptyClientForm(), moviesCategory: "movies", tvCategory: "tv" });
    expect(screen.getByText("Should differ from the movies category.")).toBeTruthy();
  });
});

describe("a saved client's file-location links", () => {
  it("lists the saved links, tests and removes each, and links new paths only when both are typed", () => {
    const onAddMapping = vi.fn();
    const onTestMapping = vi.fn();
    const onRemoveMapping = vi.fn();
    const setNewMapping = vi.fn();
    const props = { mappings: [mapping], onAddMapping, onTestMapping, onRemoveMapping, setNewMapping };
    open(saved(), { ...props, newMapping: { remotePath: "/x", localPath: "E:\\x" } });
    fireEvent.click(screen.getByText("Advanced path mapping"));
    expect(screen.getByText("1 saved path mapping")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Test" }));
    expect(onTestMapping).toHaveBeenCalledWith("m1");
    fireEvent.click(screen.getByRole("button", { name: "Remove /downloads link" }));
    expect(onRemoveMapping).toHaveBeenCalledWith("m1");
    fireEvent.change(screen.getByLabelText("Path reported by My qBittorrent"), { target: { value: "/y" } });
    expect(setNewMapping).toHaveBeenCalledWith({ remotePath: "/y", localPath: "E:\\x" });
    fireEvent.change(screen.getByLabelText("Same path as Deluno sees it"), { target: { value: "E:\\y" } });
    expect(setNewMapping).toHaveBeenCalledWith({ remotePath: "/x", localPath: "E:\\y" });
    fireEvent.click(screen.getByRole("button", { name: "Link paths" }));
    expect(onAddMapping).toHaveBeenCalledTimes(1);

    for (const newMapping of [{ remotePath: "/x", localPath: "  " }, { remotePath: "", localPath: "E:\\x" }]) {
      cleanup();
      open(saved(), { ...props, newMapping });
      fireEvent.click(screen.getByText("Advanced path mapping"));
      expect((screen.getByRole("button", { name: "Link paths" }) as HTMLButtonElement).disabled).toBe(true);
    }
  });

  it("with no saved links it says when a mapping is needed and lists none", () => {
    open(saved(), { mappings: [] });
    fireEvent.click(screen.getByText("Advanced path mapping"));
    expect(screen.getByText("Only when the client and Deluno see different paths")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Remove /downloads link" })).toBeNull();
  });

  it("shows a spinner on the link being tested and on a link being added, and disables the buttons while busy", () => {
    open(saved(), { mappings: [mapping], busy: "mapping:test:m1", newMapping: { remotePath: "/x", localPath: "/y" } });
    fireEvent.click(screen.getByText("Advanced path mapping"));
    expect((screen.getByRole("button", { name: "Test" }) as HTMLButtonElement).disabled).toBe(true);
    expect(document.querySelector(".animate-spin")).not.toBeNull();
    cleanup();
    open(saved(), { busy: "mapping:add", newMapping: { remotePath: "/x", localPath: "/y" } });
    fireEvent.click(screen.getByText("Advanced path mapping"));
    expect(document.querySelector(".animate-spin")).not.toBeNull();
    expect((screen.getByRole("button", { name: "Link paths" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("the advanced summary and the seeding rules that arrive late", () => {
  it("a blank priority reads as 1", () => {
    open(saved(), {}, { ...emptyClientForm(), priority: "" });
    expect(screen.getByRole("button", { name: /^Advanced.*Priority 1/ })).toBeTruthy();
  });

  it("rules that arrive, or fail to, after the drawer was closed change nothing", async () => {
    let resolve!: (rules: unknown[]) => void;
    fetchJson.mockImplementation(() => new Promise((done) => { resolve = done as (rules: unknown[]) => void; }));
    open(saved());
    fireEvent.click(screen.getByRole("button", { name: /^Seeding rules/ }));
    cleanup();
    resolve([]);
    await new Promise((done) => setTimeout(done, 0));

    let reject!: (error: Error) => void;
    fetchJson.mockImplementation(() => new Promise((_, fail) => { reject = fail; }));
    open(saved());
    fireEvent.click(screen.getByRole("button", { name: /^Seeding rules/ }));
    cleanup();
    reject(new Error("down"));
    await new Promise((done) => setTimeout(done, 0));
    expect(screen.queryByText("Deluno could not load the seeding-rule matrix.")).toBeNull();
  });
});

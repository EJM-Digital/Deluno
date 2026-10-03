import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson } from "../../lib/api";
import { queryClient } from "../../lib/route-cache";
import type { ConnectionUpdate } from "../../lib/connection-updates";
import { ConnectionUpdatePanel } from "./connection-update-panel";

vi.mock("../../lib/api", () => ({ fetchJson: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const base: ConnectionUpdate = {
  kind: "downloadClient",
  id: "qb1",
  name: "qBittorrent",
  project: "qbittorrent",
  appName: "qBittorrent",
  version: "4.6.7",
  latestVersion: "5.0.2",
  releaseUrl: "https://github.com/qbittorrent/qBittorrent/releases/tag/release-5.0.2",
  dismissedVersion: null,
  notice: true,
  mode: "updateNow",
  targetVersion: "5.0.2",
  message: "Deluno installs qBittorrent 5.0.2, a build it has checked, and restarts qBittorrent."
};

function renderPanel(updates: ConnectionUpdate[], rest: (path: string | undefined, init?: RequestInit) => unknown = () => undefined) {
  queryClient.clear();
  vi.mocked(fetchJson).mockImplementation(((path: string, init?: RequestInit) => {
    if (path === "/api/connections/updates") return Promise.resolve(updates);
    return Promise.resolve(rest(path as string, init));
  }) as never);
  return render(
    <QueryClientProvider client={queryClient}>
      <ConnectionUpdatePanel kind="downloadClient" id="qb1" />
    </QueryClientProvider>
  );
}

async function confirmUpdate(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("button", { name: "Update now" }));
  expect(screen.getByText("Update qBittorrent now?")).toBeInTheDocument();
  const dialog = screen.getByRole("dialog");
  await user.click(Array.from(dialog.querySelectorAll("button")).find((button) => button.textContent === "Update now")!);
}

describe("a connection's new version in its drawer (#1118)", () => {
  beforeEach(() => vi.mocked(fetchJson).mockReset());

  it("shows nothing when the connection is up to date", async () => {
    const { container } = renderPanel([]);
    await waitFor(() => expect(fetchJson).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("shows the release notes and Update now where Deluno installed the app and can update it", async () => {
    renderPanel([base]);

    expect(await screen.findByText("qBittorrent 5.0.2 is out · you have 4.6.7")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Release notes/ })).toHaveAttribute("href", base.releaseUrl);
    expect(screen.getByRole("button", { name: "Update now" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Dismiss this version" })).toBeInTheDocument();
  });

  it("asks before it updates, then runs the update and reports the result in plain words", async () => {
    const user = userEvent.setup();
    renderPanel([base], (path) => (path?.endsWith("/update") ? { outcome: "updated", message: "Updated to 5.0.2.", version: "5.0.2" } : undefined));

    await user.click(await screen.findByRole("button", { name: "Update now" }));
    expect(screen.getByText("Update qBittorrent now?")).toBeInTheDocument();
    expect(vi.mocked(fetchJson).mock.calls.some(([path]) => String(path).endsWith("/update"))).toBe(false);
    await user.click(Array.from(screen.getByRole("dialog").querySelectorAll("button")).find((button) => button.textContent === "Update now")!);

    expect(await screen.findByText("Updated to 5.0.2.")).toBeInTheDocument();
    expect(vi.mocked(fetchJson)).toHaveBeenCalledWith("/api/connections/downloadClient/qb1/update", { method: "POST" });
  });

  it("says Failed with the reason when the update does not go through", async () => {
    const user = userEvent.setup();
    renderPanel([base], (path) => (path?.endsWith("/update") ? { outcome: "failed", message: "Failed: not the build Deluno expects.", version: "4.6.7" } : undefined));

    await confirmUpdate(user);

    expect(await screen.findByText("Failed: not the build Deluno expects.")).toBeInTheDocument();
  });

  it("gives release notes and a how-to, and no Update now, for an app Deluno did not install", async () => {
    renderPanel([{ ...base, mode: "manual", targetVersion: null, message: "Pull the new image and restart the container." }]);

    expect(await screen.findByText("Pull the new image and restart the container.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Release notes/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Update now" })).not.toBeInTheDocument();
  });

  it("says Deluno has not checked a version yet and links the notes, with no Update now", async () => {
    renderPanel([{ ...base, mode: "notChecked", targetVersion: null, latestVersion: "5.3.0", message: "Deluno hasn't checked 5.3.0 yet. Read the release notes." }]);

    expect(await screen.findByText("Deluno hasn't checked 5.3.0 yet. Read the release notes.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Update now" })).not.toBeInTheDocument();
  });

  it("dismisses this version through the server", async () => {
    const user = userEvent.setup();
    renderPanel([base]);

    await user.click(await screen.findByRole("button", { name: "Dismiss this version" }));

    expect(vi.mocked(fetchJson)).toHaveBeenCalledWith("/api/connections/downloadClient/qb1/version/dismiss", { method: "POST" });
  });

  it("says so when the version was dismissed, and offers no second Dismiss", async () => {
    renderPanel([{ ...base, notice: false, dismissedVersion: "5.0.2" }]);

    expect(await screen.findByText(/You dismissed 5\.0\.2\. The notice comes back only for a newer version\./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Dismiss this version" })).not.toBeInTheDocument();
  });
});

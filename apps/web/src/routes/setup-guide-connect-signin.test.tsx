import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));

const { ConnectAppsStep } = await import("./setup-guide-page");
const { ConnectInstalledApps } = await import("../components/app/connect-installed-apps");

const qbittorrent = { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 8081 };
const refused = { kind: "qbittorrent", name: "qBittorrent", healthy: false, needsSignIn: true, message: "qBittorrent is on this computer, but Deluno could not sign in to it. Nothing was saved." };

function answer() {
  fetchJson.mockImplementation(async (url: string) => (url === "/api/system/bundled-components" ? [qbittorrent] : refused));
}

afterEach(() => { cleanup(); fetchJson.mockReset(); });

describe("an installed app that needs its own login", () => {
  it("in the guided setup (no handler) the sentence is a link to Find & Download, Download Clients", async () => {
    answer();
    render(<MemoryRouter><ConnectAppsStep skipped={false} onSkip={vi.fn()} onChange={vi.fn()} hasInstalled /></MemoryRouter>);

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));

    expect(await screen.findByText(/Nothing was saved\./)).toBeTruthy();
    const link = screen.getByRole("link", { name: "Enter your own login for qBittorrent" });
    expect(link.getAttribute("href")).toBe("/indexers/download-clients");
  });

  it("where a screen gives a handler the sentence is a button that calls it with the app and the answer, not a link", async () => {
    answer();
    const onNeedsSignIn = vi.fn();
    render(<MemoryRouter><ConnectInstalledApps onNeedsSignIn={onNeedsSignIn} /></MemoryRouter>);

    fireEvent.click(await screen.findByRole("button", { name: "Connect qBittorrent" }));
    const button = await screen.findByRole("button", { name: "Enter your own login for qBittorrent" });
    expect(screen.queryByRole("link", { name: "Enter your own login for qBittorrent" })).toBeNull();
    fireEvent.click(button);

    expect(onNeedsSignIn).toHaveBeenCalled();
    const [appArgument, answerArgument] = onNeedsSignIn.mock.calls.at(-1)!;
    expect(appArgument).toMatchObject({ kind: "qbittorrent" });
    expect(answerArgument).toMatchObject({ needsSignIn: true, healthy: false });
  });
});

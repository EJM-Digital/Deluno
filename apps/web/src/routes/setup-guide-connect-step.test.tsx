import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));

const { ConnectAppsStep, STEPS } = await import("./setup-guide-page");

const qbittorrent = { kind: "qbittorrent", displayName: "qBittorrent", category: "download-client", installed: true, reachable: true, connected: false, defaultHost: "localhost", defaultPort: 8081 };

describe("setup guide Connect installed apps step (#1061)", () => {
  afterEach(() => fetchJson.mockReset());

  it("sits in Stage 2 right after Find & Download, before Automation", () => {
    const ids = STEPS.map((step) => step.id);
    expect(ids.indexOf("connectApps")).toBe(ids.indexOf("services") + 1);
    expect(STEPS.find((step) => step.id === "connectApps")?.stage).toBe(2);
  });

  it("lists the installed apps and lets the person skip without blocking setup", async () => {
    fetchJson.mockResolvedValue([qbittorrent]);
    const onSkip = vi.fn();
    render(<MemoryRouter><ConnectAppsStep skipped={false} onSkip={onSkip} onChange={vi.fn()} hasInstalled /></MemoryRouter>);

    expect(await screen.findByRole("button", { name: "Connect qBittorrent" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Skip connecting for now" }));
    expect(onSkip).toHaveBeenCalled();
  });

  it("says plainly that there is nothing to connect when nothing was installed", async () => {
    fetchJson.mockResolvedValue([]);
    render(<MemoryRouter><ConnectAppsStep skipped={false} onSkip={vi.fn()} onChange={vi.fn()} hasInstalled={false} /></MemoryRouter>);

    expect(await screen.findByText(/nothing to connect here/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Connect all" })).toBeNull();
  });
});

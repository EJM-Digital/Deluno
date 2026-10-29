import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { SourcesCard } from "./sources-card";
import { sortConnections } from "./connections";

afterEach(cleanup);

const NOW = new Date("2026-10-01T10:00:00");
const indexer = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, isEnabled: true, priority: 0, healthStatus: "healthy", lastHealthLatencyMs: 320, rateLimitedUntilUtc: null, ...extra }) as never;
const row = (id: string, extra: Record<string, unknown> = {}) => ({ id, name: id, protocol: "torrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "healthy", active: 0, queued: 0, ...extra }) as never;
const connection = (kind: string, name: string, countToday: number, extra: object = {}) => ({ kind, id: name, name, enabled: true, healthy: true, countToday, busy: 0, ...extra }) as never;
const all = [
  connection("lists", "Trakt", 2),
  connection("processor", "Weir", 6, { busy: 1 }),
  connection("subtitles", "OpenSubtitles", 12),
  connection("requests", "Seerr", 3),
  connection("mediaServer", "Plex", 5)
];

function Where() {
  return <span data-testid="where">{useLocation().pathname}</span>;
}

function draw() {
  return render(
    <MemoryRouter initialEntries={["/"]}>
      <SourcesCard
        indexers={[indexer("NZBgeek")]}
        scoreboard={{ indexers: [] } as never}
        summary={undefined}
        now={NOW.getTime()}
        clientRows={[row("qBittorrent")]}
        clients={[{ id: "qBittorrent", name: "qBittorrent", isEnabled: true }] as never}
        speeds={new Map([["qBittorrent", 6.2]])}
        seeding={new Map([["qBittorrent", 8]])}
        connections={sortConnections(all)}
      />
      <Where />
    </MemoryRouter>
  );
}

describe("Live Sources card", () => {
  it("sends the card header's \"Manage\" link to Find and Download's indexers", () => {
    draw();
    const link = screen.getByRole("link", { name: "Manage: Sources" });
    expect(link).toHaveAttribute("href", "/indexers/indexers");
  });

  it("opens each connection pill on the screen where that kind is managed", () => {
    const { container } = draw();
    const hrefOf = (name: string) => container.querySelector<HTMLAnchorElement>(`a.ls-pill[aria-label^="${name}:"]`)?.getAttribute("href");
    expect(hrefOf("Weir")).toBe("/indexers/companion-tools");
    expect(hrefOf("Plex")).toBe("/indexers/media-servers");
    expect(hrefOf("Seerr")).toBe("/indexers/media-servers");
    expect(hrefOf("OpenSubtitles")).toBe("/indexers/subtitle-providers");
    expect(hrefOf("Trakt")).toBe("/settings/lists");
  });

  it("navigates in-app on a plain click, and leaves a ctrl-click to the browser", () => {
    const { container } = draw();
    const pill = container.querySelector<HTMLAnchorElement>("a.ls-pill")!;
    const plain = fireEvent.click(pill);
    expect(plain).toBe(false);
    expect(screen.getByTestId("where").textContent).not.toBe("/");
    const modified = fireEvent.click(pill, { ctrlKey: true });
    expect(modified).toBe(true);
  });
});

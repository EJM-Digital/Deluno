import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { BlockedRelease, PlatformSettingsSnapshot } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { BlockedReleasesConsole } from "./blocked-releases-console";

/**
 * The blocklist's row states (coverage register, area search-grab): the Un-refuse button while one is busy, the refuse and
 * un-refuse failure toasts, and a row opening its drawer. Only the network is answered.
 */
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), warning: vi.fn(), error: vi.fn() }
}));
const { toast: toasts } = (await import("../shell/toaster")) as unknown as {
  toast: { success: ReturnType<typeof vi.fn>; warning: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
};

function schedules() {
  return { settings: { libraryFileCheckHours: 6 } as PlatformSettingsSnapshot, recycleBin: { retentionDays: 7, maxSizeMb: 10_000 } };
}

function release(overrides: Partial<BlockedRelease> = {}): BlockedRelease {
  return {
    id: "block-1", releaseKey: "arrival.2016.2160p|nebula", releaseName: "Arrival.2016.2160p", indexerName: "Nebula", mediaType: "movies", entityId: "movie-1",
    title: "Arrival", reasonCode: "noVideoStream", reason: "No video stream was detected in this file.", torrentHashOrItemId: "hash-1", downloadClientId: "qbittorrent-main",
    downloadClientName: "qBittorrent", blockedUtc: "2026-09-05T12:00:00Z", state: "refused", ...overrides
  };
}

describe("the blocklist's row states", () => {
  beforeEach(() => vi.clearAllMocks());

  it("while one Un-refuse is sent it reads Un-refusing and every other row's Un-refuse is disabled", async () => {
    let finish!: () => void;
    const gate = new Promise<void>((resolve) => { finish = resolve; });
    vi.mocked(authedFetch).mockImplementation(async () => { await gate; return { ok: true } as Response; });
    render(
      <BlockedReleasesConsole {...schedules()} releases={[release(), release({ id: "block-2", releaseName: "Dune.2021.1080p" })]} rules={[]} onChanged={vi.fn()} />
    );

    await userEvent.click(screen.getAllByRole("button", { name: "Un-refuse" })[0]);

    expect(await screen.findByRole("button", { name: "Un-refusing…" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Un-refuse" })).toBeDisabled();
    finish();
  });

  it("a refuse that fails says the release could not be refused", async () => {
    vi.mocked(authedFetch).mockRejectedValue(new Error("down"));
    render(<BlockedReleasesConsole {...schedules()} releases={[release({ state: "proposed" })]} rules={[]} onChanged={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: /refuse it/i }));

    expect(toasts.error).toHaveBeenCalledWith("That release could not be refused.");
  });

  it("an un-refuse that fails says the release could not be un-refused", async () => {
    vi.mocked(authedFetch).mockRejectedValue(new Error("down"));
    render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);

    await userEvent.click(screen.getByRole("button", { name: "Un-refuse" }));

    expect(toasts.error).toHaveBeenCalledWith("That release could not be un-refused.");
  });

  it("clicking a row opens its drawer with the release, what found it, why, and when it was refused", async () => {
    render(<BlockedReleasesConsole {...schedules()} releases={[release()]} rules={[]} onChanged={vi.fn()} />);

    await userEvent.click(screen.getByText("Arrival.2016.2160p"));

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Found on");
    expect(dialog).toHaveTextContent("Nebula");
    expect(dialog).toHaveTextContent("No video in the file");
    expect(dialog).toHaveTextContent("Refused");
  });
});

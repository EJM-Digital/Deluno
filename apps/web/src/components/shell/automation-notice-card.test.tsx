import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  AutomationNoticeCard,
  computeOffAutomationNotices,
  computeVisibleAutomationNotices
} from "./automation-notice-card";
import { emptyPlatformSettingsSnapshot, type LibraryItem, type PlatformSettingsSnapshot } from "../../lib/api";

const ALL_AUTOMATION_ON: Partial<PlatformSettingsSnapshot> = {
  autoStartJobs: true,
  renameOnImport: true,
  cleanupEmptyFolders: true,
  cleanupRemoveClientEntryAfterThreshold: true,
  cleanupPurgePayloadAfterThreshold: true
};

function settings(overrides: Partial<PlatformSettingsSnapshot> = {}): PlatformSettingsSnapshot {
  return { ...emptyPlatformSettingsSnapshot, ...ALL_AUTOMATION_ON, ...overrides };
}

const NO_LIBRARIES: LibraryItem[] = [];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("computeOffAutomationNotices (#936)", () => {
  it("shows nothing once every automatic behaviour is on", () => {
    const allOn = settings({
      autoStartJobs: true,
      renameOnImport: true,
      cleanupRemoveClientEntryAfterThreshold: true,
      cleanupPurgePayloadAfterThreshold: true,
      cleanupEmptyFolders: true
    });
    expect(computeOffAutomationNotices(allOn, NO_LIBRARIES)).toEqual([]);
  });

  it("uses #936's own example wording for automatic searching", () => {
    const notices = computeOffAutomationNotices(settings({ autoStartJobs: false }), NO_LIBRARIES);
    const searching = notices.find((item) => item.key === "searching");
    expect(searching?.text).toBe("Automatic searching is off, so nothing will download until you turn it on.");
  });

  it("lists a notice per off behaviour, each with a settings PATCH that turns just it on", () => {
    const allOff = settings({
      autoStartJobs: false,
      renameOnImport: false,
      cleanupRemoveClientEntryAfterThreshold: false,
      cleanupPurgePayloadAfterThreshold: false,
      cleanupEmptyFolders: false
    });
    const notices = computeOffAutomationNotices(allOff, NO_LIBRARIES);
    const keys = notices.map((item) => item.key).sort();
    expect(keys).toEqual(["deleteBadDownloadFiles", "leftoverCleanup", "removeBadDownloads", "renameOnImport", "searching"]);
    for (const notice of notices) {
      expect(Object.keys(notice.turnOnPatch ?? {})).toHaveLength(1);
    }
  });

  it("adds a subtitle-fetching notice only once a library exists with no subtitle language set", () => {
    const library = { id: "1", subtitleLanguages: [] } as unknown as LibraryItem;
    expect(computeOffAutomationNotices(settings(), NO_LIBRARIES).some((item) => item.key === "subtitleFetching")).toBe(false);
    expect(computeOffAutomationNotices(settings(), [library]).some((item) => item.key === "subtitleFetching")).toBe(true);
  });

  it("does not show subtitle fetching once any library has a subtitle language configured", () => {
    const library = { id: "1", subtitleLanguages: ["en"] } as unknown as LibraryItem;
    expect(computeOffAutomationNotices(settings(), [library]).some((item) => item.key === "subtitleFetching")).toBe(false);
  });
});

describe("computeVisibleAutomationNotices (#936 'Keep off' stays dismissed)", () => {
  it("hides a notice whose key is in the stored dismissed list", () => {
    const off = settings({ autoStartJobs: false, dismissedAutomationNotices: "searching" });
    expect(computeVisibleAutomationNotices(off, NO_LIBRARIES)).toEqual([]);
  });

  it("keeps showing an off behaviour that was never dismissed", () => {
    const off = settings({ autoStartJobs: false, renameOnImport: false, dismissedAutomationNotices: "renameOnImport" });
    const visible = computeVisibleAutomationNotices(off, NO_LIBRARIES);
    expect(visible.map((item) => item.key)).toEqual(["searching"]);
  });
});

describe("AutomationNoticeCard", () => {
  it("renders nothing when every automatic behaviour is on", () => {
    const { container } = render(
      <MemoryRouter>
        <AutomationNoticeCard settings={settings({ autoStartJobs: true, renameOnImport: true, cleanupEmptyFolders: true, cleanupRemoveClientEntryAfterThreshold: true, cleanupPurgePayloadAfterThreshold: true })} libraries={NO_LIBRARIES} />
      </MemoryRouter>
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("groups every off behaviour into one card, not one banner each", () => {
    render(
      <MemoryRouter>
        <AutomationNoticeCard settings={settings({ autoStartJobs: false, renameOnImport: false })} libraries={NO_LIBRARIES} />
      </MemoryRouter>
    );
    expect(screen.getAllByRole("region", { name: "Off by itself" })).toHaveLength(1);
    expect(screen.getByText(/Automatic searching is off/)).toBeVisible();
    expect(screen.getByText(/Renaming files on import is off/)).toBeVisible();
  });

  it("sends a settings PATCH that turns on only the one behaviour clicked", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);

    render(
      <MemoryRouter>
        <AutomationNoticeCard settings={settings({ autoStartJobs: false })} libraries={NO_LIBRARIES} />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("button", { name: "Turn on" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ autoStartJobs: true });
  });

  it("sends the dismissed key when 'Keep off' is clicked, without touching the real setting", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
    vi.stubGlobal("fetch", fetchMock);

    render(
      <MemoryRouter>
        <AutomationNoticeCard settings={settings({ renameOnImport: false })} libraries={NO_LIBRARIES} />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("button", { name: "Keep off" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ dismissedAutomationNotices: "renameOnImport" });
  });
});

import { render, screen } from "@testing-library/react";
import { fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { IndexerItem, ReleaseProfileItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * An enable/disable switch and indexer scoping for Release Profiles (#770):
 * a disabled profile, or one scoped to indexers that do not include the
 * release's own indexer, is skipped entirely rather than matched.
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const indexers: IndexerItem[] = [
  { id: "idx-1", name: "NZBgeek", protocol: "usenet" } as IndexerItem,
  { id: "idx-2", name: "TorrentLeech", protocol: "torrent" } as IndexerItem
];

const scopedProfile: ReleaseProfileItem = {
  id: "profile-1",
  name: "Kids rule",
  tagName: "Kids",
  preferredProtocol: "any",
  usenetDelayMinutes: 0,
  torrentDelayMinutes: 0,
  mustContain: "",
  mustNotContain: "",
  preferredTerms: [],
  createdUtc: new Date(0).toISOString(),
  updatedUtc: new Date(0).toISOString(),
  enabled: false,
  indexerIds: ["idx-1"]
};

const loaderData = { profiles: [scopedProfile], indexers, settings: emptyPlatformSettingsSnapshot };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsReleaseRulesPage } = await import("./settings-release-rules-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsReleaseRulesPage />
    </MemoryRouter>
  );
}

describe("release profile enable/disable and indexer scoping (#770)", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows a disabled profile as Disabled and its indexer scope by name in the list", () => {
    renderPage();

    expect(screen.getByText("Disabled")).toBeTruthy();
    expect(screen.getByText("NZBgeek")).toBeTruthy();
  });

  it("opens the drawer with an enabled switch defaulted on for a new rule, and a checkbox per configured indexer", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New acquisition rule" }));

    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("switch", { name: /^Rule enabled/ }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("checkbox", { name: "NZBgeek" })).toBeTruthy();
    expect(screen.getByRole("checkbox", { name: "TorrentLeech" })).toBeTruthy();
  });

  it("loads an existing scoped, disabled rule with its switch off and its indexer checked", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));

    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("switch", { name: /^Rule enabled/ }).getAttribute("aria-checked")).toBe("false");
    expect((screen.getByRole("checkbox", { name: "NZBgeek" }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: "TorrentLeech" }) as HTMLInputElement).checked).toBe(false);
  });

  it("checking a second indexer adds it without unchecking the first", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");

    fireEvent.click(screen.getByRole("checkbox", { name: "TorrentLeech" }));

    expect((screen.getByRole("checkbox", { name: "NZBgeek" }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: "TorrentLeech" }) as HTMLInputElement).checked).toBe(true);
  });
});

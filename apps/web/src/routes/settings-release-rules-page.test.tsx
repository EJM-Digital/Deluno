import { render, screen, waitFor } from "@testing-library/react";
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
  indexerIds: ["idx-1"],
  bypassIfHighestQuality: false,
  tagNames: ["Kids", "Family"],
  position: 0
};

const defaultRule: ReleaseProfileItem = { ...scopedProfile, id: "profile-2", name: "Everything else", tagName: "", tagNames: [], position: 1, enabled: true, indexerIds: [] };
const secondRule: ReleaseProfileItem = { ...scopedProfile, id: "profile-3", name: "Anime rule", tagName: "Anime", tagNames: ["Anime"], position: 2, enabled: true, indexerIds: [] };
const loaderData: { profiles: ReleaseProfileItem[]; indexers: IndexerItem[]; settings: typeof emptyPlatformSettingsSnapshot; ruleMode: string } = { profiles: [scopedProfile], indexers, settings: emptyPlatformSettingsSnapshot, ruleMode: "combine" };
const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

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

describe("release rules apply to several tags, in an order, with patterns (#994 #995 #996)", () => {
  afterEach(() => {
    vi.clearAllMocks();
    loaderData.profiles = [scopedProfile];
    loaderData.ruleMode = "combine";
  });

  it("lists every tag a rule applies to and loads them into the tag picker", async () => {
    renderPage();

    expect(screen.getByText("Kids, Family")).toBeTruthy();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");
    expect(screen.getByRole("button", { name: /Remove.*Kids/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Remove.*Family/ })).toBeTruthy();
  });

  it("tells the user patterns between slashes work in must contain and must not contain", async () => {
    renderPage();
    fireEvent.click(screen.getByText("Kids rule"));
    await screen.findByRole("dialog");

    expect(screen.getAllByText(/between slashes/).length).toBe(2);
  });

  it("does not show an order column while every matching rule is used", () => {
    loaderData.profiles = [scopedProfile, defaultRule];
    renderPage();

    expect(screen.queryByRole("button", { name: "Move Kids rule up" })).toBeNull();
    expect(screen.getByText("Rule for every title")).toBeTruthy();
  });

  it("in first-match mode lists the default rule last and saves a new order from the move buttons", async () => {
    loaderData.ruleMode = "first-match";
    loaderData.profiles = [defaultRule, scopedProfile, secondRule];
    authedFetchMock.mockResolvedValue(new Response(JSON.stringify([]), { status: 200 }));
    renderPage();

    expect(screen.getByText("Default rule")).toBeTruthy();
    expect(screen.getByText("Titles no other rule matches")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Move Kids rule up" }) as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Move Anime rule up" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/release-profiles/order",
      expect.objectContaining({ method: "PUT", body: JSON.stringify({ ids: ["profile-3", "profile-1"] }) })
    ));
  });

  it("saves the choice of how matching rules are used", async () => {
    authedFetchMock.mockResolvedValue(new Response(JSON.stringify({ mode: "first-match" }), { status: 200 }));
    renderPage();

    fireEvent.change(screen.getByRole("combobox", { name: "When more than one rule matches" }), { target: { value: "first-match" } });

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/release-profiles/mode",
      expect.objectContaining({ method: "PUT", body: JSON.stringify({ mode: "first-match" }) })
    ));
  });
});

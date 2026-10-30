import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * Settings > Media management > Naming (#1329): the custom pattern drawer's preview line (sample data, and a Movies\ or TV Shows\
 * prefix for the two folder kinds), and the Live preview panel that lists what Deluno will call a movie folder, a show folder,
 * a movie file, an episode file, a multi-episode file, a daily and an anime episode, and what it does with illegal characters and colons.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const loaderData = { settings: {} as Record<string, unknown>, libraries: [], processorConnections: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");
const renderPage = () => render(<MemoryRouter initialEntries={["/settings/media-management"]}><SettingsMediaManagementPage /></MemoryRouter>);
const panel = (label: string) => screen.getByText(label, { selector: "span" }).closest("div")!;

beforeEach(() => {
  cleanup();
  loaderData.settings = {
    ...emptyPlatformSettingsSnapshot,
    movieFolderFormat: "{Movie Title} ({Release Year})",
    seriesFolderFormat: "{Series Title} ({Series Year})",
    movieFileFormat: "{Movie Title} ({Release Year}) [{Quality}]",
    episodeFileFormat: "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
    seasonFolderFormat: "",
    specialsFolderFormat: "",
    replaceIllegalCharacters: true,
    colonReplacementMode: "dash"
  };
});

describe("the custom pattern drawer's preview line", () => {
  async function openCustom(group: string) {
    renderPage();
    const styles = screen.getByRole("radiogroup", { name: group });
    fireEvent.click(within(styles).getByRole("radio", { name: "Custom pattern" }));
    return await screen.findByRole("dialog");
  }

  it("shows a movie folder pattern with sample data behind the Movies prefix", async () => {
    const drawer = await openCustom("movie-folder naming style");

    fireEvent.change(within(drawer).getByLabelText("Custom pattern"), { target: { value: "{Movie Title} ({Release Year}) [{Quality}]" } });

    expect(within(drawer).getByText("Movies\\Arrival (2016) [WEB 1080p]")).toBeInTheDocument();
  });

  it("shows a show folder pattern behind the TV Shows prefix and an episode file pattern with no prefix", async () => {
    const folder = await openCustom("series-folder naming style");
    fireEvent.change(within(folder).getByLabelText("Custom pattern"), { target: { value: "{Series Title} ({Series Year}) {TVDb ID}" } });
    expect(within(folder).getByText("TV Shows\\Severance (2022) 371980")).toBeInTheDocument();

    cleanup();
    const file = await openCustom("episode-file naming style");
    fireEvent.change(within(file).getByLabelText("Custom pattern"), { target: { value: "{Series Title} - S{Season:00}E{Episode:00}" } });
    expect(within(file).getByText("Severance - S01E01")).toBeInTheDocument();
  });

  it("with the pattern left empty it previews the placeholder pattern", async () => {
    const drawer = await openCustom("movie-folder naming style");

    expect(within(drawer).getByText("Movies\\Arrival (2016)")).toBeInTheDocument();
  });
});

describe("the Live preview panel", () => {
  it("lists a movie folder, a show folder, a movie file, an episode file, a multi-episode file, a daily and an anime episode, illegal characters and colons", () => {
    renderPage();

    for (const label of ["Movie folder", "TV show folder", "Movie file", "Episode file", "Multi-episode file", "Daily episode file", "Anime episode file", "Illegal characters", "Colon replacement"]) {
      expect(screen.getByText(label, { selector: "span" })).toBeInTheDocument();
    }

    expect(within(panel("Movie folder")).getByText("Movies\\Arrival (2016)")).toBeInTheDocument();
    expect(within(panel("TV show folder")).getByText("TV Shows\\Severance (2022)")).toBeInTheDocument();
    expect(within(panel("Movie file")).getByText("Arrival (2016) [WEB 1080p]")).toBeInTheDocument();
    expect(within(panel("Episode file")).getByText("Severance - S01E01 - Good News About Hell")).toBeInTheDocument();
    expect(within(panel("Illegal characters")).getByText("Show- Special Edition")).toBeInTheDocument();
    expect(within(panel("Colon replacement")).getByText("Series- Title")).toBeInTheDocument();
    expect(screen.queryByText("Specials folder", { selector: "span" })).toBeNull();
  });

  it("adds a Specials folder line, and the season folder to the show line, once season subfolders are on", () => {
    loaderData.settings = { ...loaderData.settings, seasonFolderFormat: "Season {Season:00}", specialsFolderFormat: "Specials" };
    renderPage();

    expect(within(panel("TV show folder")).getByText("TV Shows\\Severance (2022)\\Season 01")).toBeInTheDocument();
    expect(within(panel("Specials folder")).getByText("TV Shows\\Severance (2022)\\Specials")).toBeInTheDocument();
  });

  it("with illegal characters switched off the character is removed, not replaced", () => {
    loaderData.settings = { ...loaderData.settings, replaceIllegalCharacters: false };
    renderPage();

    expect(within(panel("Illegal characters")).getByText("Show Special Edition")).toBeInTheDocument();
  });
});

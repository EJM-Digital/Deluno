import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Media management > Naming: each format's presets, the custom pattern drawer, the multi-episode style, the season
 * folders, the colon choice and what the live preview says. Saving is read from the one PATCH the page sends.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const revalidate = vi.fn();
const loaderData = { settings: {} as Record<string, unknown>, libraries: [], processorConnections: [] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const page = () => <MemoryRouter initialEntries={["/settings/file-handling"]}><SettingsMediaManagementPage /></MemoryRouter>;

let server: Record<string, unknown>;
function patches() {
  return vi.mocked(authedFetch).mock.calls
    .filter(([url, init]) => url === "/api/settings" && (init as RequestInit).method === "PATCH")
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>);
}

beforeEach(() => {
  cleanup();
  server = { ...emptyPlatformSettingsSnapshot };
  loaderData.settings = { ...server };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (_url, init) => {
    server = { ...server, ...JSON.parse(String((init as RequestInit).body)) };
    return json(server);
  });
  revalidate.mockReset();
});

/** Picks each preset of one format in turn, saves, and returns what the page sent for the format's field. */
async function savedAfterPicking(group: string, label: string, field: string) {
  const radio = within(screen.getByRole("radiogroup", { name: group })).getByRole("radio", { name: label });
  fireEvent.click(radio);
  expect(radio.getAttribute("aria-checked")).toBe("true");
  const before = patches().length;
  fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
  await waitFor(() => expect(patches().length).toBe(before + 1));
  return patches()[before][field];
}

describe("naming presets", () => {
  it("movie folders: Title + year, Title + ID (IMDb) and Title only are each chosen and saved", async () => {
    render(page());
    expect(await savedAfterPicking("movie-folder naming style", "Title + ID", "movieFolderFormat")).toBe("{Movie Title} ({Release Year}) [{IMDb ID}]");
    expect(await savedAfterPicking("movie-folder naming style", "Title only", "movieFolderFormat")).toBe("{Movie Title}");
    expect(await savedAfterPicking("movie-folder naming style", "Title + year", "movieFolderFormat")).toBe("{Movie Title} ({Release Year})");
  });

  it("movie files: Title, year and what it is / Title and year / With the release group are each chosen and saved", async () => {
    render(page());
    expect(await savedAfterPicking("movie-file naming style", "Title + year", "movieFileFormat")).toBe("{Movie Title} ({Release Year})");
    expect(await savedAfterPicking("movie-file naming style", "With the release group", "movieFileFormat")).toBe("{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]-{Release Group}");
    expect(await savedAfterPicking("movie-file naming style", "Title, year and what it is", "movieFileFormat")).toBe("{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]");
  });

  it("TV show folders: Title + year, Title + TVDb and Title only are each chosen and saved", async () => {
    render(page());
    expect(await savedAfterPicking("series-folder naming style", "Title + ID", "seriesFolderFormat")).toBe("{Series Title} ({Series Year}) [tvdb-{TVDb ID}]");
    expect(await savedAfterPicking("series-folder naming style", "Title only", "seriesFolderFormat")).toBe("{Series Title}");
    expect(await savedAfterPicking("series-folder naming style", "Title + year", "seriesFolderFormat")).toBe("{Series Title} ({Series Year})");
  });

  it("episode files: Standard / With quality / Episode code only are each chosen and saved", async () => {
    render(page());
    expect(await savedAfterPicking("episode-file naming style", "Standard episode", "episodeFileFormat")).toBe("{Series Title} - S{Season:00}E{Episode:00} - {Episode Title}");
    expect(await savedAfterPicking("episode-file naming style", "With quality", "episodeFileFormat")).toBe("{Series Title} - S{Season:00}E{Episode:00} - {Episode Title} [{Quality}]");
    expect(await savedAfterPicking("episode-file naming style", "Episode code only", "episodeFileFormat")).toBe("S{Season:00}E{Episode:00} - {Episode Title}");
  });

  it("daily episode files: Standard / With quality / Date only are each chosen and saved", async () => {
    render(page());
    expect(await savedAfterPicking("daily-episode-file naming style", "Standard daily episode", "dailyEpisodeFileFormat")).toBe("{Series Title} - {Air-Date} - {Episode Title}");
    expect(await savedAfterPicking("daily-episode-file naming style", "Date only", "dailyEpisodeFileFormat")).toBe("{Series Title} - {Air-Date}");
    expect(await savedAfterPicking("daily-episode-file naming style", "With quality", "dailyEpisodeFileFormat")).toBe("{Series Title} - {Air-Date} - {Episode Title} [{Quality}]");
  });

  it("anime episode files: Standard / With quality / Season and absolute are each chosen and saved", async () => {
    render(page());
    expect(await savedAfterPicking("anime-episode-file naming style", "Standard anime episode", "animeEpisodeFileFormat")).toBe("{Series Title} - {absolute:000} - {Episode Title}");
    expect(await savedAfterPicking("anime-episode-file naming style", "Season and absolute", "animeEpisodeFileFormat")).toBe("{Series Title} - S{Season:00}E{Episode:00} - {absolute:000} - {Episode Title}");
    expect(await savedAfterPicking("anime-episode-file naming style", "With quality", "animeEpisodeFileFormat")).toBe("{Series Title} - {absolute:000} - {Episode Title} [{Quality}]");
  });
});

describe("custom pattern drawer", () => {
  it("writes the typed pattern into the form when applied, which makes the page changed and saves it", async () => {
    render(page());
    fireEvent.click(within(screen.getByRole("radiogroup", { name: "movie-folder naming style" })).getByRole("radio", { name: "Custom pattern" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Custom pattern" }), { target: { value: "{Movie Title} - {Release Year}" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Apply pattern" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.getByRole("button", { name: "Save changes" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(String(patches()[0].movieFolderFormat)).toMatch(/^\{Movie Title\} - \{Release Year\}/);
  });

  it("shows the live result of the pattern while typing, and Cancel puts the previous format back", async () => {
    render(page());
    const group = screen.getByRole("radiogroup", { name: "movie-folder naming style" });
    fireEvent.click(within(group).getByRole("radio", { name: "Custom pattern" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Custom pattern" }), { target: { value: "{Movie Title}" } });
    expect(within(dialog).getByText(/^Movies\\/)).toBeTruthy();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(within(group).getByRole("radio", { name: "Title + year" }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByRole("button", { name: "Save changes" })).toBeDisabled();
  });

  it("offers insertable tokens for the format being built", async () => {
    render(page());
    fireEvent.click(within(screen.getByRole("radiogroup", { name: "movie-file naming style" })).getByRole("radio", { name: "Custom pattern" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Release group" }));
    expect((within(dialog).getByRole("textbox", { name: "Custom pattern" }) as HTMLInputElement).value).toBe("{Release Group}");
    fireEvent.click(within(dialog).getByRole("button", { name: "Quality" }));
    expect((within(dialog).getByRole("textbox", { name: "Custom pattern" }) as HTMLInputElement).value).toBe("{Release Group} {Quality}");
  });
});

describe("multi-episode style, season folders and colons", () => {
  it("offers Extend, Duplicate, Repeat, Scene, Range and Prefixed Range and changes the 'Multi-episode file' preview with each", () => {
    render(page());
    const select = screen.getByRole("combobox", { name: /Multi-episode style/ }) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual(["extend", "duplicate", "repeat", "scene", "range", "prefixed-range"]);
    const preview = () => screen.getByText("Multi-episode file").nextElementSibling!.textContent!;
    const seen: Record<string, string> = {};
    for (const [style, token] of [["extend", "S01E01E02"], ["duplicate", "S01E01S01E02"], ["repeat", "S01E01-S01E02"], ["scene", "S01E01-E02"], ["range", "S01E01-E02"], ["prefixed-range", "S01E01-02"]]) {
      fireEvent.change(select, { target: { value: style } });
      seen[style] = preview();
      expect(seen[style]).toContain(token);
      expect(seen[style]).toContain("Good News About Hell + The Grim Barrow");
    }
  });

  it("saves the chosen multi-episode style", async () => {
    render(page());
    fireEvent.change(screen.getByRole("combobox", { name: /Multi-episode style/ }), { target: { value: "prefixed-range" } });
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].multiEpisodeStyle).toBe("prefixed-range");
  });

  it("the season-folder switch shows Season folder and Specials folder with their defaults, and off clears both when saved", async () => {
    render(page());
    expect(screen.queryByRole("textbox", { name: /Season folder/ })).toBeNull();
    fireEvent.click(screen.getByRole("switch", { name: /Split shows into season subfolders/ }));
    expect((screen.getByPlaceholderText("Season {Season:00}") as HTMLInputElement).value).toBe("Season {Season:00}");
    expect((screen.getByPlaceholderText("Specials") as HTMLInputElement).value).toBe("Specials");
    expect(screen.getAllByText("Specials folder").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0]).toMatchObject({ seasonFolderFormat: "Season {Season:00}", specialsFolderFormat: "Specials" });

    fireEvent.click(screen.getByRole("switch", { name: /Split shows into season subfolders/ }));
    expect(screen.queryByPlaceholderText("Season {Season:00}")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(patches()).toHaveLength(2));
    expect(patches()[1]).toMatchObject({ seasonFolderFormat: "", specialsFolderFormat: "" });
  });

  it("shows the colon choice only while illegal characters are replaced, and previews each of its five modes", () => {
    render(page());
    const select = screen.getByRole("combobox", { name: /Colon replacement/ }) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual(["smart", "delete", "dash", "space-dash", "space-dash-space"]);
    const preview = () => screen.getAllByText("Colon replacement").map((node) => node.nextElementSibling).find((node) => node?.tagName === "CODE")!.textContent;
    const expected: Record<string, string> = { smart: "Series - Title", delete: "Series Title", dash: "Series- Title", "space-dash": "Series - Title", "space-dash-space": "Series -  Title" };
    for (const mode of Object.keys(expected)) {
      fireEvent.change(select, { target: { value: mode } });
      expect(preview()).toBe(expected[mode]);
    }
    fireEvent.click(screen.getByRole("switch", { name: /Replace illegal characters/ }));
    expect(screen.queryByRole("combobox", { name: /Colon replacement/ })).toBeNull();
  });

  it("saves the chosen colon replacement", async () => {
    render(page());
    fireEvent.change(screen.getByRole("combobox", { name: /Colon replacement/ }), { target: { value: "space-dash-space" } });
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(patches()).toHaveLength(1));
    expect(patches()[0].colonReplacementMode).toBe("space-dash-space");
  });
});

describe("a naming setting saved elsewhere", () => {
  it("shows in the open form once the page reads the settings again (the form copies the loader's settings once and is never re-synced)", () => {
    const view = render(page());
    expect((within(screen.getByRole("radiogroup", { name: "movie-folder naming style" })).getByRole("radio", { name: "Title + year" })).getAttribute("aria-checked")).toBe("true");
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, movieFolderFormat: "{Movie Title}" };
    view.rerender(page());
    expect((within(screen.getByRole("radiogroup", { name: "movie-folder naming style" })).getByRole("radio", { name: "Title only" })).getAttribute("aria-checked")).toBe("true");
  });
});

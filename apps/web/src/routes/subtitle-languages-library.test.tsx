import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import type { LibraryItem } from "../lib/api/types/resources";
import { authedFetch } from "../lib/use-auth";

/**
 * Settings > Subtitles, the per-library part: the libraries table, the language picker, "What counts as having one",
 * timing repair, "Which releases to take", the "After download" cleanups, and Save with its toast and its refusal.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
const toastSuccess = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
vi.mock("sonner", () => ({ toast: { success: toastSuccess, error: toastError } }));
const revalidate = vi.fn();
const loaderData = { libraries: [] as LibraryItem[], languages: [] as { code: string; name: string }[], settings: emptyPlatformSettingsSnapshot as object };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SubtitleLanguagesPage } = await import("./subtitle-languages-screen");
const LANGUAGES = [{ code: "en", name: "English" }, { code: "ja", name: "Japanese" }, { code: "de", name: "German" }];
const library = (over: Record<string, unknown> = {}) => ({ id: "lib-1", name: "Movies", mediaType: "movies", subtitleLanguages: [], subtitleLanguageMode: "all", ...over }) as unknown as LibraryItem;
const renderPage = () => render(<MemoryRouter><SubtitleLanguagesPage /></MemoryRouter>);
const ok = () => new Response("{}", { status: 200 });
const open = (name: RegExp | string) => fireEvent.click(screen.getByRole("row", { name: new RegExp(typeof name === "string" ? name : name.source) }));
const putBody = () => {
  const call = vi.mocked(authedFetch).mock.calls.find(([url]) => String(url).endsWith("/subtitles"));
  expect(call, "no PUT to the library's subtitles").toBeDefined();
  expect((call![1] as RequestInit).method).toBe("PUT");
  return JSON.parse(String((call![1] as RequestInit).body));
};

beforeEach(() => {
  cleanup();
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(ok());
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidate.mockReset();
  loaderData.languages = LANGUAGES;
  loaderData.settings = emptyPlatformSettingsSnapshot;
  loaderData.libraries = [library({ subtitleLanguages: ["en"] })];
});

describe("the libraries table", () => {
  it("shows one row per library with its languages, how many per file, unnamed-subtitle handling and the N of M heading", () => {
    loaderData.libraries = [
      library({ id: "a", name: "Films", subtitleLanguages: ["en", "ja"], subtitleLanguageMode: "first", subtitleUnknownLanguage: "en" }),
      library({ id: "b", name: "Shows", mediaType: "tv", subtitleLanguages: ["de"], subtitleLanguageMode: "all", subtitleEmbeddedCounts: false }),
      library({ id: "c", name: "Anime", mediaType: "tv", subtitleLanguages: ["ja"], subtitlePictureBasedCounts: false }),
      library({ id: "d", name: "Home videos" })
    ];
    renderPage();
    expect(screen.getByText("3 of 4 libraries asking")).toBeInTheDocument();
    const films = screen.getByRole("row", { name: /Films/ });
    expect(within(films).getByText("English, Japanese")).toBeInTheDocument();
    expect(within(films).getByText("Any one of them")).toBeInTheDocument();
    expect(within(screen.getByRole("row", { name: /Shows/ })).getByText("All of them")).toBeInTheDocument();
    expect(within(screen.getByRole("row", { name: /Shows/ })).getByText("Embedded tracks do not count")).toBeInTheDocument();
    expect(within(screen.getByRole("row", { name: /Anime/ })).getByText("Picture-based embedded tracks do not count")).toBeInTheDocument();
    const none = screen.getByRole("row", { name: /Home videos/ });
    expect(within(none).getByText("None")).toBeInTheDocument();
    expect(within(none).getByText("Nothing is fetched for this shelf")).toBeInTheDocument();
    expect(within(none).getByText("Off")).toBeInTheDocument();
    expect(within(none).getByText("Left unknown")).toBeInTheDocument();
  });

  it("says there are no libraries yet and where to make one", () => {
    loaderData.libraries = [];
    renderPage();
    expect(screen.getByText("No libraries yet")).toBeInTheDocument();
    expect(screen.getByText("Subtitle languages are set per library, so make one under Media Management first.")).toBeInTheDocument();
  });
});

describe("the language picker", () => {
  it("adds a language from the list, names each remove button after its language, and numbers the order once it is first-one-found", () => {
    loaderData.libraries = [library({ subtitleLanguages: ["en"] })];
    renderPage();
    open("Movies");
    expect(screen.getByText("Every file on this shelf gets English subtitles.")).toBeInTheDocument();
    expect(screen.queryByRole("radiogroup", { name: "How many of these languages a file needs" })).toBeNull();
    fireEvent.change(screen.getByLabelText("Add a subtitle language"), { target: { value: "ja" } });
    expect(screen.getByRole("button", { name: "Stop wanting Japanese subtitles" })).toBeInTheDocument();
    expect(screen.getByRole("radiogroup", { name: "How many of these languages a file needs" })).toBeInTheDocument();
    expect(screen.getByText(/all of them, on every file/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "First one found" }));
    expect(screen.getByText(/Just one, never two\./)).toBeInTheDocument();
    expect(screen.getAllByText("1").length).toBeGreaterThan(0);
    expect(screen.getAllByText("2").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Stop wanting English subtitles" }));
    expect(screen.queryByRole("button", { name: "Stop wanting English subtitles" })).toBeNull();
    expect(screen.queryByRole("radiogroup", { name: "How many of these languages a file needs" })).toBeNull();
  });

  it("says the list of languages could not be loaded when the server sent none", () => {
    loaderData.languages = [];
    renderPage();
    open("Movies");
    expect(screen.getByText(/Deluno could not load the list of languages\./)).toBeInTheDocument();
  });

  it("shows no further sections for a shelf that wants no language", () => {
    loaderData.libraries = [library({ subtitleLanguages: [] })];
    renderPage();
    open("Movies");
    expect(screen.queryByText("What counts as having one")).toBeNull();
    expect(screen.queryByText("Timing repair")).toBeNull();
    expect(screen.getByText(/No subtitles wanted here\./)).toBeInTheDocument();
  });
});

describe("what is sent when a library is saved", () => {
  it("sends the unnamed-subtitle language, the name-after-video switch, the embedded switches and the dependent picture switch's state", async () => {
    renderPage();
    open("Movies");
    fireEvent.click(screen.getByRole("combobox", { name: "Unknown subtitle language" }));
    fireEvent.click((await screen.findAllByRole("option", { name: /^Japanese/ })).find((node) => node.tagName === "BUTTON")!);
    fireEvent.click(screen.getByRole("switch", { name: "Name subtitles after the video only" }));
    const picture = screen.getByRole("switch", { name: "Picture-based subtitles (PGS/VobSub) don't count" });
    expect(picture).toBeEnabled();
    fireEvent.click(picture);
    fireEvent.click(screen.getByRole("switch", { name: "Count subtitles inside the video" }));
    expect(screen.getByRole("switch", { name: "Picture-based subtitles (PGS/VobSub) don't count" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
    expect(putBody()).toMatchObject({ unknownLanguage: "ja", omitLanguageCode: true, embeddedCounts: false, pictureBasedCounts: false });
  });

  it("sends the timing repair switches, threshold, offsets and the providers left out", async () => {
    renderPage();
    open("Movies");
    const repair = screen.getByRole("switch", { name: "Repair subtitle timing automatically" });
    const framerate = screen.getByRole("switch", { name: "Repair a subtitle written for another framerate" });
    expect(repair).toBeChecked();
    expect(framerate).toBeEnabled();
    fireEvent.click(framerate);
    fireEvent.click(screen.getByRole("combobox", { name: "Timing threshold" }));
    fireEvent.click(await screen.findByRole("option", { name: /Same source/ }));
    fireEvent.change(screen.getByLabelText("Maximum offset"), { target: { value: "120" } });
    fireEvent.change(screen.getByLabelText("Confidence required"), { target: { value: "5.5" } });
    fireEvent.change(screen.getByPlaceholderText("For example: opensubtitles, subdl"), { target: { value: "opensubtitles, subdl ," } });
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
    expect(putBody().timingPolicy).toEqual({ enabled: true, syncOnlyBelow: "same-source", maxOffsetSeconds: 120, requiredPeakSigma: 5.5, excludedProviders: ["opensubtitles", "subdl"], repairFramerate: false });
  });

  it("dims the framerate repair while timing repair is off", () => {
    renderPage();
    open("Movies");
    fireEvent.click(screen.getByRole("switch", { name: "Repair subtitle timing automatically" }));
    expect(screen.getByRole("switch", { name: "Repair a subtitle written for another framerate" })).toBeDisabled();
  });

  it("saves the release-name lists as arrays split on commas, keeping words that hold spaces together", async () => {
    renderPage();
    open("Movies");
    fireEvent.change(screen.getByPlaceholderText("e.g. NTb, FLUX"), { target: { value: " NTb, Blu-ray Remux ,, FLUX " } });
    fireEvent.change(screen.getByPlaceholderText("e.g. HDTV, CAM"), { target: { value: "CAM" } });
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
    expect(putBody().namePolicy).toEqual({ mustContain: ["NTb", "Blu-ray Remux", "FLUX"], mustNotContain: ["CAM"] });
  });

  it("saves each of the seven cleanups and the subtitle colour in the content policy", async () => {
    renderPage();
    open("Movies");
    for (const label of ["Remove hearing-impaired annotations", "Remove style tags", "Remove emoji", "Normalize whitespace", "Fix all-uppercase text", "Fix OCR mistakes", "Move right-to-left punctuation"]) {
      fireEvent.click(screen.getByRole("switch", { name: label }));
    }
    fireEvent.change(screen.getByPlaceholderText("e.g. yellow or #ffd400"), { target: { value: "  #ffd400 " } });
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalled());
    expect(putBody().contentPolicy).toEqual({
      stripHearingImpairedAnnotations: true, removeStyleTags: true, removeEmoji: true, normalizeWhitespace: true, fixAllUppercase: true, fixOcrErrors: true,
      cueColour: "#ffd400", reverseRightToLeftPunctuation: true
    });
  });
});

describe("Save", () => {
  it("toasts '<library> saved. Deluno will read and fetch on its next cycle.', closes the drawer and reads the list again", async () => {
    renderPage();
    open("Movies");
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Movies saved. Deluno will read and fetch on its next cycle."));
    expect(vi.mocked(authedFetch).mock.calls.find(([url]) => String(url).endsWith("/subtitles"))![0]).toBe("/api/libraries/lib-1/subtitles");
    expect(revalidate).toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByText("Which subtitles this shelf wants, and what already counts as having one.")).toBeNull());
  });

  it("toasts 'Those subtitle settings could not be saved.' and leaves the drawer open when the server refuses", async () => {
    vi.mocked(authedFetch).mockResolvedValue(new Response("{}", { status: 500 }));
    renderPage();
    open("Movies");
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Those subtitle settings could not be saved."));
    expect(screen.getByText("Which subtitles this shelf wants, and what already counts as having one.")).toBeInTheDocument();
    expect(revalidate).not.toHaveBeenCalled();
  });
});

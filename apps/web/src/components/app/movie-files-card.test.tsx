import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson, type MovieFileItem, type SubtitleLanguageOption } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { MovieFilesCard } from "./movie-files-card";

vi.mock("../../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../lib/api")>(),
  fetchJson: vi.fn()
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const files: MovieFileItem[] = [
  {
    path: "/library/movies-main/Dune Part Two (2024)/Dune.mkv",
    sizeBytes: 21_474_836_480,
    kind: "video",
    isPrimary: true,
    quality: "WEB 1080p",
    videoCodec: "HEVC",
    audioCodec: "EAC3",
    audioChannels: "5.1",
    language: null,
    hearingImpaired: null
  },
  {
    path: "/library/movies-main/Dune Part Two (2024)/Dune.srt",
    sizeBytes: 45_000,
    kind: "subtitle",
    isPrimary: false,
    quality: null,
    videoCodec: null,
    audioCodec: null,
    audioChannels: null,
    language: "und",
    hearingImpaired: false
  }
];

const languages: SubtitleLanguageOption[] = [
  { code: "en", name: "English" },
  { code: "fr", name: "French" }
];

function mockLoads(list: MovieFileItem[] = files) {
  vi.mocked(fetchJson).mockImplementation(async (url: string) => {
    if (url === "/api/movies/movie-1/files") return list as never;
    if (url === "/api/subtitle-languages") return languages as never;
    throw new Error(`unexpected fetchJson: ${url}`);
  });
}

describe("MovieFilesCard (#782)", () => {
  beforeEach(() => {
    vi.mocked(fetchJson).mockReset();
    vi.mocked(authedFetch).mockReset();
    mockLoads();
  });

  it("lists every file on disk with its size, quality and language", async () => {
    render(<MovieFilesCard movieId="movie-1" />);

    expect(await screen.findByText("Dune.mkv")).toBeVisible();
    expect(screen.getByText("Tracked file")).toBeVisible();
    expect(screen.getByText("Dune.srt")).toBeVisible();
    expect(screen.getByText("WEB 1080p")).toBeVisible();
    expect(screen.getByText("und")).toBeVisible();
    expect(screen.getByText("HEVC · EAC3")).toBeVisible();
    expect(screen.getByText("2 files")).toBeVisible();
  });

  it("edits the tracked file's quality", async () => {
    const user = userEvent.setup();
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ quality: "Remux 2160p" }));
    render(<MovieFilesCard movieId="movie-1" />);

    await screen.findByText("Dune.mkv");
    await user.click(screen.getByRole("button", { name: "Edit Dune.mkv" }));

    const qualityInput = screen.getByRole("textbox", { name: "Quality" });
    await user.clear(qualityInput);
    await user.type(qualityInput, "Remux 2160p");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/movie-1/files/quality",
      expect.objectContaining({ method: "PUT", body: JSON.stringify({ quality: "Remux 2160p" }) })
    ));
  });

  it("edits a subtitle's language", async () => {
    const user = userEvent.setup();
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ path: "renamed", language: "en" }));
    render(<MovieFilesCard movieId="movie-1" />);

    await screen.findByText("Dune.srt");
    await user.click(screen.getByRole("button", { name: "Edit Dune.srt" }));
    await user.selectOptions(screen.getByRole("combobox", { name: "Language" }), "en");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/movie-1/files/language",
      expect.objectContaining({
        method: "PUT",
        body: JSON.stringify({ path: "/library/movies-main/Dune Part Two (2024)/Dune.srt", language: "en", hearingImpaired: false })
      })
    ));
  });

  it("deletes a file after a second confirming click", async () => {
    const user = userEvent.setup();
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ deleted: true, wasPrimary: false }));
    render(<MovieFilesCard movieId="movie-1" />);

    await screen.findByText("Dune.srt");
    await user.click(screen.getByRole("button", { name: "Delete Dune.srt" }));

    // The first click only asks for confirmation.
    expect(authedFetch).not.toHaveBeenCalled();
    const confirmButton = await screen.findByRole("button", { name: "Confirm delete" });
    await user.click(confirmButton);

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/movie-1/files",
      expect.objectContaining({
        method: "DELETE",
        body: JSON.stringify({ path: "/library/movies-main/Dune Part Two (2024)/Dune.srt" })
      })
    ));
  });

  it("shows an empty state when nothing is on disk", async () => {
    mockLoads([]);
    render(<MovieFilesCard movieId="movie-1" />);

    expect(await screen.findByText("No files on disk")).toBeVisible();
  });

  it("labels each library's file and deletes exactly that library's copy (#1065)", async () => {
    const user = userEvent.setup();
    const video = (path: string, libraryId: string, libraryName: string, isPrimary: boolean): MovieFileItem => ({
      path, sizeBytes: 1, kind: "video", isPrimary, quality: null, videoCodec: null, audioCodec: null,
      audioChannels: null, language: null, hearingImpaired: null, libraryId, libraryName
    });
    mockLoads([
      video("/hd/Heat/Heat.mkv", "lib-hd", "Movies", true),
      video("/4k/Heat/Heat.mkv", "lib-4k", "Movies 4K", true)
    ]);
    vi.mocked(authedFetch).mockResolvedValue(Response.json({ deleted: true, wasPrimary: true }));
    render(<MovieFilesCard movieId="movie-1" />);

    expect(await screen.findByText(/In Movies 4K/)).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Delete Heat.mkv in Movies 4K" }));
    await user.click(await screen.findByRole("button", { name: "Confirm delete" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/movie-1/files",
      expect.objectContaining({ body: JSON.stringify({ path: "/4k/Heat/Heat.mkv", libraryId: "lib-4k" }) })
    ));
  });
});

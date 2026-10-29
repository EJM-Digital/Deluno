import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchJson, type MovieFileItem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { MovieFilesCard } from "./movie-files-card";

vi.mock("../../lib/api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../lib/api")>(),
  fetchJson: vi.fn()
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const files: MovieFileItem[] = [{
  path: "/library/movies-main/Dune Part Two (2024)/Dune.mkv", sizeBytes: 21_474_836_480, kind: "video", isPrimary: true, quality: "WEB 1080p",
  videoCodec: "HEVC", audioCodec: "EAC3", audioChannels: "5.1", language: null, hearingImpaired: null
}];

describe("the film's Files card (low rows)", () => {
  beforeEach(() => {
    vi.mocked(fetchJson).mockReset();
    vi.mocked(authedFetch).mockReset();
  });

  it("says 'Reading files from disk…' while the files load", () => {
    vi.mocked(fetchJson).mockImplementation(() => new Promise(() => undefined));
    render(<MovieFilesCard movieId="movie-1" />);

    expect(screen.getByText(/Reading files from disk/)).toBeVisible();
  });

  it("says 'Could not load files' when reading the files fails", async () => {
    vi.mocked(fetchJson).mockRejectedValue(new Error("down"));
    render(<MovieFilesCard movieId="movie-1" />);

    expect(await screen.findByText("Could not load files")).toBeVisible();
    expect(screen.getByText("Could not read this title's files from disk.")).toBeVisible();
  });

  it("Cancel on the inline edit closes it without saving, and Cancel on the delete confirmation closes it without deleting", async () => {
    const user = userEvent.setup();
    vi.mocked(fetchJson).mockImplementation(async (url: string) => (url === "/api/movies/movie-1/files" ? files as never : [] as never));
    render(<MovieFilesCard movieId="movie-1" />);
    await screen.findByText("Dune.mkv");

    await user.click(screen.getByRole("button", { name: "Edit Dune.mkv" }));
    expect(screen.getByRole("textbox", { name: "Quality" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("textbox", { name: "Quality" })).toBeNull();

    await user.click(screen.getByRole("button", { name: "Delete Dune.mkv" }));
    expect(await screen.findByRole("button", { name: "Confirm delete" })).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Confirm delete" })).toBeNull());
    expect(authedFetch).not.toHaveBeenCalled();
  });
});

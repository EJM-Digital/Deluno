import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import { TitleFolderActions } from "./title-folder-actions";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() }
}));

const fetchMock = vi.mocked(authedFetch);

function json(body: unknown) {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}

describe("TitleFolderActions", () => {
  beforeEach(() => fetchMock.mockReset());

  it("offers rescan and rename preview as two separate, labelled buttons", () => {
    render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);
    expect(screen.getByRole("button", { name: "Rescan the folder for Arrival" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Preview the folder rename for Arrival" })).toBeTruthy();
  });

  it("rescans one title and says in plain words what it found", async () => {
    fetchMock.mockImplementation(() => json({
      outcome: "ok", title: "Arrival", filesChecked: 1, filesPresent: 1, missing: [],
      untracked: [{ fileName: "Arrival.extended.mkv", path: "/m/Arrival.extended.mkv", sizeBytes: 1, issueId: "tok" }],
      summary: "Rescanned Arrival. Its file is where Deluno expects it."
    }));
    render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);

    await userEvent.click(screen.getByRole("button", { name: "Rescan the folder for Arrival" }));
    await userEvent.click(screen.getByRole("button", { name: "Rescan now" }));

    await waitFor(() => expect(screen.getByText(/where Deluno expects it/)).toBeTruthy());
    expect(fetchMock.mock.calls[0][0]).toBe("/api/filesystem/rescan-title");
    expect(screen.getByText("Arrival.extended.mkv")).toBeTruthy();
  });

  it("asks first, in plain words, and does nothing until confirmed", async () => {
    fetchMock.mockImplementation(() => json({ outcome: "ok", title: "Arrival", filesChecked: 0, filesPresent: 0, missing: [], untracked: [], summary: "Rescanned." }));
    render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);

    await userEvent.click(screen.getByRole("button", { name: "Rescan the folder for Arrival" }));

    expect(screen.getByText(/any that are gone are marked missing and it stops wanting them\. Nothing is deleted\./)).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Rescan now" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
  });

  it("confirms that an unmounted or unknown library changed nothing", async () => {
    for (const [outcome, line] of [["library-unreachable", /Mount the drive/], ["library-unknown", /Put the title back in a library/]] as const) {
      fetchMock.mockImplementation(() => json({
        outcome, title: "Arrival", filesChecked: 0, filesPresent: 0, missing: [], untracked: [], summary: "Deluno changed nothing."
      }));
      const { unmount } = render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);

      await userEvent.click(screen.getByRole("button", { name: "Rescan the folder for Arrival" }));
      await userEvent.click(screen.getByRole("button", { name: "Rescan now" }));

      await waitFor(() => expect(screen.getByText(/no file was marked missing/)).toBeTruthy());
      expect(screen.getByText(line)).toBeTruthy();
      unmount();
    }
  });

  it("previews a rename without changing anything, and only then offers to rename", async () => {
    fetchMock.mockImplementation(() => json({
      outcome: "will-rename", currentFolder: "Arrival", proposedFolder: "Arrival (2016)", willChange: true,
      message: "Deluno would rename the folder."
    }));
    render(<TitleFolderActions variant="series" titleId="s1" titleName="Severance" />);

    await userEvent.click(screen.getByRole("button", { name: "Preview the folder rename for Severance" }));

    await waitFor(() => expect(screen.getByText("Arrival (2016)")).toBeTruthy());
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe("/api/series/s1/rename-preview");
    expect(screen.getByRole("button", { name: "Rename folder" })).toBeTruthy();
  });

  it("offers no rename when the folder is already right", async () => {
    fetchMock.mockImplementation(() => json({
      outcome: "already-named", currentFolder: "Arrival (2016)", proposedFolder: "Arrival (2016)", willChange: false,
      message: "Nothing would change."
    }));
    render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);

    await userEvent.click(screen.getByRole("button", { name: "Preview the folder rename for Arrival" }));

    await waitFor(() => expect(screen.getByText("Nothing would change.")).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Rename folder" })).toBeNull();
  });
});

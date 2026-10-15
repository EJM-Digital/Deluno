import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
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
    for (const [outcome, line] of [["library-unreachable", /Reconnect the drive/], ["library-unknown", /Put the title back in a library/]] as const) {
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

  describe("Rename folder", () => {
    const willRename = { outcome: "will-rename", currentFolder: "Arrival", proposedFolder: "Arrival (2016)", willChange: true, message: "Deluno would rename the folder." };

    async function previewThenRename(renameReply: unknown, onChanged = vi.fn()) {
      fetchMock.mockImplementation((input) => String(input).includes("rename-preview") ? json(willRename) : json(renameReply));
      render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" onChanged={onChanged} />);
      await userEvent.click(screen.getByRole("button", { name: "Preview the folder rename for Arrival" }));
      await userEvent.click(await screen.findByRole("button", { name: "Rename folder" }));
      return onChanged;
    }

    it("shows the folder now and the folder after, then renames it, says so and tells the page", async () => {
      const onChanged = await previewThenRename({ renamedCount: 1, failures: [] });

      await waitFor(() => expect(toast.success).toHaveBeenCalledWith("The folder was renamed."));
      const apply = fetchMock.mock.calls.find((call) => String(call[0]) === "/api/movies/bulk/rename-apply")!;
      expect(JSON.parse(String((apply[1] as RequestInit).body))).toEqual({ movieIds: ["m1"] });
      expect(onChanged).toHaveBeenCalled();
    });

    it("says why a rename was refused and keeps that on screen", async () => {
      await previewThenRename({ renamedCount: 0, failures: [{ reason: "Another folder already has that name." }] });

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The folder could not be renamed", { duration: Infinity, description: "Another folder already has that name." }));
    });

    it("says nothing needed renaming when nothing was changed and nothing failed", async () => {
      const onChanged = await previewThenRename({ renamedCount: 0, failures: [] });

      await waitFor(() => expect(toast.info).toHaveBeenCalledWith("Nothing needed renaming."));
      expect(onChanged).not.toHaveBeenCalled();
    });

    it("says the folder could not be renamed when the request itself fails", async () => {
      fetchMock.mockImplementation((input) => String(input).includes("rename-preview") ? json(willRename) : Promise.resolve(new Response("{}", { status: 500 })));
      render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);
      await userEvent.click(screen.getByRole("button", { name: "Preview the folder rename for Arrival" }));
      await userEvent.click(await screen.findByRole("button", { name: "Rename folder" }));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The folder could not be renamed."));
    });
  });

  it("Review for import adds an untracked video to Import issues, and says so when that fails", async () => {
    const untracked = { outcome: "ok", title: "Arrival", filesChecked: 1, filesPresent: 1, missing: [], untracked: [{ fileName: "Arrival.extended.mkv", path: "/m/Arrival.extended.mkv", sizeBytes: 1, issueId: "tok" }], summary: "Rescanned Arrival." };
    fetchMock.mockImplementation((input) => String(input).includes("reconciliation/repair") ? json({}) : json(untracked));
    render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);
    await userEvent.click(screen.getByRole("button", { name: "Rescan the folder for Arrival" }));
    await userEvent.click(screen.getByRole("button", { name: "Rescan now" }));

    await userEvent.click(await screen.findByRole("button", { name: /Review/ }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Added to Import issues so you can review it."));
    const repair = fetchMock.mock.calls.find((call) => String(call[0]) === "/api/filesystem/reconciliation/repair")!;
    expect(JSON.parse(String((repair[1] as RequestInit).body))).toEqual({ issueId: "tok", action: "queue-import-review" });
  });

  it("Review for import says the file could not be added when the repair is refused", async () => {
    const untracked = { outcome: "ok", title: "Arrival", filesChecked: 1, filesPresent: 1, missing: [], untracked: [{ fileName: "Arrival.extended.mkv", path: "/m/Arrival.extended.mkv", sizeBytes: 1, issueId: "tok" }], summary: "Rescanned Arrival." };
    fetchMock.mockImplementation((input) => String(input).includes("reconciliation/repair") ? Promise.resolve(new Response("{}", { status: 400 })) : json(untracked));
    render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" />);
    await userEvent.click(screen.getByRole("button", { name: "Rescan the folder for Arrival" }));
    await userEvent.click(screen.getByRole("button", { name: "Rescan now" }));

    await userEvent.click(await screen.findByRole("button", { name: /Review/ }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That file could not be added for review."));
  });
});

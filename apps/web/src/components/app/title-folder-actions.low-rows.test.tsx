import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { TitleFolderActions } from "./title-folder-actions";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));

const fetchMock = vi.mocked(authedFetch);
const json = (body: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));

async function rescan(onChanged?: () => void) {
  render(<TitleFolderActions variant="movies" titleId="m1" titleName="Arrival" onChanged={onChanged} />);
  await userEvent.click(screen.getByRole("button", { name: "Rescan the folder for Arrival" }));
  await userEvent.click(screen.getByRole("button", { name: "Rescan now" }));
}

describe("rescanning a title's folder (low rows)", () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.mocked(toast.error).mockReset();
  });

  it("a rescan that changed something tells the page to read again", async () => {
    fetchMock.mockImplementation(() => json({ outcome: "changed", title: "Arrival", filesChecked: 1, filesPresent: 0, missing: ["Arrival.mkv"], untracked: [], summary: "Rescanned Arrival. Its file is gone." }));
    const onChanged = vi.fn();

    await rescan(onChanged);

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1));
  });

  it("a rescan that changed nothing does not", async () => {
    fetchMock.mockImplementation(() => json({ outcome: "ok", title: "Arrival", filesChecked: 1, filesPresent: 1, missing: [], untracked: [], summary: "Rescanned Arrival." }));
    const onChanged = vi.fn();

    await rescan(onChanged);

    await screen.findByText("Rescanned Arrival.");
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("a rescan that errors says 'The folder could not be rescanned.'", async () => {
    fetchMock.mockImplementation(() => json({ message: "no" }, 500));

    await rescan();

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The folder could not be rescanned."));
  });
});

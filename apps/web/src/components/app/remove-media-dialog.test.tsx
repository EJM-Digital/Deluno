import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { removalDownloadSentence } from "../../lib/removal-download-words";
import { RemoveMediaDialog } from "./remove-media-dialog";

/**
 * #1160: the remove dialog said deleted files "cannot be undone" while Deluno
 * moves them to its recycle bin. It now says where they go, for how long, and
 * where to get them back.
 */
describe("remove dialog", () => {
  afterEach(() => vi.unstubAllGlobals());

  function renderDialog(filePaths: string[]) {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ retentionDays: 30, maxSizeMb: 1000 }), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    })));
    render(
      <MemoryRouter>
        <RemoveMediaDialog
          open
          onOpenChange={() => {}}
          title="Nosferatu"
          mediaLabel="movie"
          removalPreview={{ filePaths, folderPaths: [], warnings: [] }}
          importListCount={0}
          onConfirm={() => {}}
        />
      </MemoryRouter>
    );
  }

  it("says the files go to the recycle bin, names the keep period and links to it", async () => {
    renderDialog(["D:/Movies/Nosferatu (1922)/Nosferatu.mkv"]);

    await waitFor(() => expect(screen.getByText(/kept for 30 days/)).toBeTruthy());
    expect(screen.getByText(/Deluno's recycle bin/)).toBeTruthy();
    expect(screen.queryByText(/cannot be undone/i)).toBeNull();
    expect(screen.getByRole("link", { name: "Open the recycle bin" }).getAttribute("href")).toBe("/system/recycle-bin");
  });

  it("says what happens to a download in progress, in the shared words, not that the client is untouched", () => {
    renderDialog([]);
    expect(screen.getByText(new RegExp(removalDownloadSentence.slice(0, 40)))).toBeTruthy();
    expect(screen.queryByText(/never changed here/)).toBeNull();
  });

  it("does not talk about files when the title has none", () => {
    renderDialog([]);
    expect(screen.getByText(/no imported files recorded/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Open the recycle bin" })).toBeNull();
  });
});

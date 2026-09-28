import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const tag = (id: string, name: string) => ({ id, name, color: "slate", description: "", createdUtc: "2026-09-29T00:00:00Z", updatedUtc: "2026-09-29T00:00:00Z" });

const loaderData = {
  libraries: [],
  qualityProfiles: [],
  settings: {},
  tags: [tag("t1", "Kids"), tag("t2", "Spare")],
  usage: [
    {
      id: "t1",
      name: "Kids",
      movieCount: 2,
      seriesCount: 1,
      totalCount: 3,
      isUnused: false,
      users: [
        { kind: "indexer", id: "i1", name: "NZBGeek", link: "/indexers/indexers" },
        { kind: "downloadClient", id: "c1", name: "Lounge qBittorrent", link: "/indexers/download-clients" },
        { kind: "acquisitionRule", id: "r1", name: "Kids protocol", link: "/settings/release-rules" }
      ]
    },
    { id: "t2", name: "Spare", movieCount: 0, seriesCount: 0, totalCount: 0, isUnused: true, users: [] }
  ]
};

const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsTagsPage } = await import("./settings-tags-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsTagsPage />
    </MemoryRouter>
  );
}

/** #1039: every user of a tag is listed, delete says what it changes, unused tags go on request. */
describe("Tags page (#1039)", () => {
  beforeEach(() => {
    authedFetchMock.mockReset();
    authedFetchMock.mockResolvedValue({ ok: true, status: 204, json: async () => ({}) });
  });

  it("shows what each tag is used by, and marks an unused tag as not used", () => {
    renderPage();
    expect(screen.getByText("3 titles, 3 settings")).toBeInTheDocument();
    expect(screen.getByText("Not used")).toBeInTheDocument();
  });

  it("lists every connection and rule using a tag, each linking to where it is set", async () => {
    renderPage();
    await userEvent.click(screen.getByText("Kids"));
    const list = await screen.findByRole("list", { name: "Settings that use this tag" });
    expect(within(list).getByRole("link", { name: "NZBGeek" })).toHaveAttribute("href", "/indexers/indexers");
    expect(within(list).getByRole("link", { name: "Lounge qBittorrent" })).toHaveAttribute("href", "/indexers/download-clients");
    expect(within(list).getByRole("link", { name: "Kids protocol" })).toHaveAttribute("href", "/settings/release-rules");
    expect(within(list).getByText("Release rule")).toBeInTheDocument();
  });

  it("names what deleting will change and removes the tag from all of them on confirm", async () => {
    renderPage();
    await userEvent.click(screen.getByText("Kids"));
    await userEvent.click(await screen.findByRole("button", { name: "Delete" }));
    const changes = await screen.findByRole("list", { name: "What deleting this tag changes" });
    expect(within(changes).getByText(/This rule is deleted/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Remove it from all of these and delete" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/tags/t1", { method: "DELETE" }));
  });

  it("deletes unused tags only when asked", async () => {
    renderPage();
    expect(authedFetchMock).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Delete unused tags (1)" }));
    await userEvent.click(await screen.findByRole("button", { name: "Delete unused tags" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/tags/delete-unused", { method: "POST" }));
  });
});

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

const toastSuccess = vi.fn();
vi.mock("../components/shell/toaster", () => ({ toast: { success: (...args: unknown[]) => toastSuccess(...args), error: vi.fn() } }));

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
    toastSuccess.mockReset();
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

  it("offers no Delete for a tag in use and says what still uses it", async () => {
    renderPage();
    await userEvent.click(screen.getByText("Kids"));
    await screen.findByRole("list", { name: "Settings that use this tag" });
    expect(screen.getByText(/In use by 3 titles, 1 indexer, 1 download client and 1 release rule — remove it from those first/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
  });

  it("still lets an unused tag be deleted", async () => {
    renderPage();
    await userEvent.click(screen.getByText("Spare"));
    await userEvent.click(await screen.findByRole("button", { name: "Delete" }));
    await userEvent.click(await screen.findByRole("button", { name: "Delete tag" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/tags/t2", { method: "DELETE" }));
  });

  it("tells the person the server's reason when a repeated tag name is refused (#1341)", async () => {
    const reason = "Another tag already has that name.";
    const body = JSON.stringify({ title: "One or more validation errors occurred.", status: 400, errors: { name: [reason] } });
    authedFetchMock.mockResolvedValue({ ok: false, status: 400, url: "/api/tags", json: async () => JSON.parse(body), text: async () => body });
    renderPage();
    await userEvent.click(screen.getAllByRole("button", { name: "New tag" })[0]);
    const drawer = await screen.findByRole("dialog");
    await userEvent.type(within(drawer).getByLabelText("Name"), "KIDS");
    await userEvent.click(within(drawer).getByRole("button", { name: "Create tag" }));

    expect(await within(drawer).findByText(reason)).toBeInTheDocument();
    expect(within(drawer).queryByText("Tag could not be created.")).toBeNull();
  });

  it("falls back to its own sentence when the server gives no reason (#1341)", async () => {
    authedFetchMock.mockResolvedValue({ ok: false, status: 500, url: "/api/tags", json: async () => ({}), text: async () => "" });
    renderPage();
    await userEvent.click(screen.getAllByRole("button", { name: "New tag" })[0]);
    const drawer = await screen.findByRole("dialog");
    await userEvent.type(within(drawer).getByLabelText("Name"), "Lounge");
    await userEvent.click(within(drawer).getByRole("button", { name: "Create tag" }));

    expect(await within(drawer).findByText("Tag could not be created.")).toBeInTheDocument();
  });

  it("sorts by a column header and remembers it, with Reset columns to go back (#1119)", async () => {
    window.localStorage.clear();
    const { unmount } = renderPage();
    const names = () => screen.getAllByRole("row").slice(1).map((row) => row.textContent ?? "");
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    await userEvent.click(within(header).getByRole("button"));
    await userEvent.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "descending");
    expect(names()[0]).toContain("Spare");
    unmount();

    renderPage();
    expect(screen.getByRole("columnheader", { name: /^Name/ })).toHaveAttribute("aria-sort", "descending");
    await userEvent.click(screen.getByRole("button", { name: "Columns" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));
    expect(screen.getByRole("columnheader", { name: /^Name/ })).not.toHaveAttribute("aria-sort");
    window.localStorage.clear();
  });

  it("deletes unused tags only when asked", async () => {
    renderPage();
    expect(authedFetchMock).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Delete unused tags (1)" }));
    await userEvent.click(await screen.findByRole("button", { name: "Delete unused tags" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/tags/delete-unused", { method: "POST" }));
  });

  it("names the unused tags in the confirmation and says how many were deleted", async () => {
    renderPage();
    await userEvent.click(screen.getByRole("button", { name: "Delete unused tags (1)" }));
    const title = await screen.findByText("Delete 1 unused tag?");
    const confirm = title.closest("[role='dialog'], [role='alertdialog']") as HTMLElement;
    expect(confirm).not.toBeNull();
    expect(within(confirm).getByText("Spare")).toBeInTheDocument();
    await userEvent.click(within(confirm).getByRole("button", { name: "Delete unused tags" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Deleted 1 unused tag"));
  });

  it("asks for a name when a new tag is created blank and sends nothing", async () => {
    renderPage();
    await userEvent.click(screen.getAllByRole("button", { name: "New tag" })[0]);
    await userEvent.click(await screen.findByRole("button", { name: "Create tag" }));
    expect(await screen.findByText("Give the tag a name.")).toBeInTheDocument();
    expect(authedFetchMock).not.toHaveBeenCalled();
  });

  it("creates a tag with its name and note, then shows Tag created and stays on the saved tag", async () => {
    authedFetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => tag("t9", "Family") });
    renderPage();
    await userEvent.click(screen.getAllByRole("button", { name: "New tag" })[0]);
    await userEvent.type(await screen.findByPlaceholderText("Name this tag, for example Kids"), "Family");
    await userEvent.click(screen.getByRole("button", { name: "Create tag" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = authedFetchMock.mock.calls[0] as [string, { method: string; body: string }];
    expect(url).toBe("/api/tags");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toMatchObject({ name: "Family", description: "" });
    expect(await screen.findByText("Tag created")).toBeInTheDocument();
    expect(revalidateMock).toHaveBeenCalled();
  });
});

import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { KeepSeedingView } from "../lib/api";

/**
 * #1240: the Keep seeding screen says which sites are private, which category a struck private torrent is moved to
 * (per client, per site, per tag), checks a category with the client, and lists a move that did not work with a way to
 * clear it. Nothing is set on a new install.
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const revalidateMock = vi.fn();
let loaderData: KeepSeedingView;

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsKeepSeedingPage } = await import("./settings-keep-seeding-page");

function freshInstall(): KeepSeedingView {
  return {
    clients: [
      { clientId: "q1", name: "My qBittorrent", protocol: "qbittorrent", canKeepSeeding: true, reason: null, category: null },
      { clientId: "s1", name: "My SABnzbd", protocol: "sabnzbd", canKeepSeeding: false, reason: "My SABnzbd is a Usenet client, and only a torrent can keep seeding.", category: null }
    ],
    indexers: [
      { indexerId: "i1", name: "Example Tracker", privacy: "private", privateOverride: null, isPrivate: true, tags: [], category: null },
      { indexerId: "i2", name: "Open Index", privacy: "unknown", privateOverride: null, isPrivate: false, tags: [], category: null }
    ],
    tagCategories: [],
    problems: []
  };
}

function json(body: unknown, ok = true) {
  return Promise.resolve({ ok, status: ok ? 200 : 400, json: () => Promise.resolve(body) } as Response);
}

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsKeepSeedingPage />
    </MemoryRouter>
  );
}

function lastPutBody() {
  const call = authedFetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "PUT").at(-1);
  return JSON.parse((call?.[1] as RequestInit).body as string);
}

describe("Keep seeding screen (#1240)", () => {
  beforeEach(() => {
    loaderData = freshInstall();
  });

  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("starts empty on a new install and says nothing is on until a rule is told to", () => {
    renderPage();

    expect(screen.getByText(/Nothing here is on until a rule is told to/i)).toBeInTheDocument();
    expect(screen.getByText(/Nothing is set yet/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Automation & Recovery, Download Health" })).toHaveAttribute("href", "/search-cycles/download-health");
    expect(screen.queryByText(/moves that did not work/i)).not.toBeInTheDocument();
  });

  it("shows what Deluno knows about each site and offers no category for a Usenet client, with the reason", () => {
    renderPage();

    expect(screen.getByText("Deluno knows: private")).toBeInTheDocument();
    // The column heading and the one private site.
    expect(screen.getAllByText("Private")).toHaveLength(2);
    expect(screen.getByText("Not private")).toBeInTheDocument();
    expect(screen.getByText("Not offered")).toBeInTheDocument();
    expect(screen.getByText(/only a torrent can keep seeding/i)).toBeInTheDocument();
  });

  it("checks a category with the client, says it is missing, and saves the client's category", async () => {
    authedFetchMock.mockImplementation((url: string) =>
      url.endsWith("/check")
        ? json({ clientId: "q1", clientName: "My qBittorrent", category: "keep-seeding", status: "missing", message: "qBittorrent does not have a category named keep-seeding. Create it in qBittorrent first; Deluno does not create categories.", supported: true, found: false })
        : json({ ...freshInstall(), clients: [{ ...freshInstall().clients[0], category: "keep-seeding" }, freshInstall().clients[1]] }));
    renderPage();

    fireEvent.click(screen.getByText("My qBittorrent"));
    fireEvent.change(await screen.findByLabelText("Category"), { target: { value: "keep-seeding" } });
    fireEvent.click(screen.getByRole("button", { name: "Check it" }));

    expect(await screen.findByText("Not in the client")).toBeInTheDocument();
    expect(screen.getByText(/Deluno does not create categories/i)).toBeInTheDocument();
    expect(authedFetchMock).toHaveBeenCalledWith("/api/download-health/keep-seeding/check", expect.objectContaining({ method: "POST", body: JSON.stringify({ clientId: "q1", category: "keep-seeding" }) }));

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(lastPutBody().clientCategories).toEqual({ q1: "keep-seeding" }));
    expect(lastPutBody().tagCategories).toEqual({});
    expect(lastPutBody().indexers).toEqual({});
  });

  it("saves a site's own answer, tags and category", async () => {
    authedFetchMock.mockImplementation(() => json(freshInstall()));
    renderPage();

    fireEvent.click(screen.getByText("Open Index"));
    const drawer = within(await screen.findByRole("dialog"));
    fireEvent.change(drawer.getByLabelText("Treat this site as"), { target: { value: "yes" } });
    fireEvent.change(drawer.getByLabelText("Tags"), { target: { value: "ratio-sensitive, quiet" } });
    fireEvent.change(drawer.getByLabelText("Category"), { target: { value: "site-keep" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(lastPutBody().indexers).toEqual({ i2: { privateOverride: true, tags: ["ratio-sensitive", "quiet"], category: "site-keep" } }));
  });

  it("lets a person say a site is not private even when its definition says it is", async () => {
    authedFetchMock.mockImplementation(() => json(freshInstall()));
    renderPage();

    fireEvent.click(screen.getByText("Example Tracker"));
    const choice = await screen.findByLabelText("Treat this site as");
    expect(within(choice).getByRole("option", { name: "Follow what Deluno knows (private)" })).toBeInTheDocument();
    fireEvent.change(choice, { target: { value: "no" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(lastPutBody().indexers).toEqual({ i1: { privateOverride: false, tags: [], category: null } }));
  });

  it("shows the server's own reason when it refuses a category", async () => {
    authedFetchMock.mockImplementation(() => json({ errors: { keepSeeding: ["Movies is the category Deluno uses for downloads on My qBittorrent. Choose a different one."] } }, false));
    renderPage();

    fireEvent.click(screen.getByText("My qBittorrent"));
    fireEvent.change(await screen.findByLabelText("Category"), { target: { value: "Movies" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText(/is the category Deluno uses for downloads on My qBittorrent/i)).toBeInTheDocument();
  });

  it("adds and removes a tag's category", async () => {
    authedFetchMock.mockImplementation(() => json(freshInstall()));
    renderPage();

    fireEvent.change(screen.getByLabelText("Tag name"), { target: { value: "ratio-sensitive" } });
    fireEvent.change(screen.getByLabelText("Category for this tag"), { target: { value: "tag-keep" } });
    fireEvent.click(screen.getByRole("button", { name: "Add tag" }));

    await waitFor(() => expect(lastPutBody().tagCategories).toEqual({ "ratio-sensitive": "tag-keep" }));
  });

  it("asks for both a tag and a category before adding one", () => {
    renderPage();

    fireEvent.change(screen.getByLabelText("Tag name"), { target: { value: "ratio-sensitive" } });
    fireEvent.click(screen.getByRole("button", { name: "Add tag" }));

    expect(authedFetchMock).not.toHaveBeenCalled();
  });

  it("removes a tag by saving the rest", async () => {
    loaderData = { ...freshInstall(), tagCategories: [{ tag: "ratio-sensitive", category: "tag-keep" }, { tag: "quiet", category: "quiet-keep" }] };
    authedFetchMock.mockImplementation(() => json(loaderData));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Remove tag ratio-sensitive" }));

    await waitFor(() => expect(lastPutBody().tagCategories).toEqual({ quiet: "quiet-keep" }));
  });

  it("lists a move that did not work with its reason and clears it on request", async () => {
    loaderData = {
      ...freshInstall(),
      problems: [{
        key: "q1:abc", clientId: "q1", clientName: "My qBittorrent", itemId: "abc", releaseName: "Example.Movie.2026.1080p", kind: "refused",
        message: "Deluno could not move Example.Movie.2026.1080p to keep-seeding in My qBittorrent: qBittorrent does not have a category named keep-seeding. It left the download alone and will try again at the next check.",
        category: "keep-seeding", firstSeenUtc: "2026-10-11T10:00:00Z", lastTriedUtc: "2026-10-11T10:10:00Z", attempts: 3
      }]
    };
    authedFetchMock.mockImplementation(() => json({ key: "q1:abc", cleared: true }));
    renderPage();

    expect(screen.getByText(/moves that did not work/i)).toBeInTheDocument();
    expect(screen.getByText(/does not have a category named keep-seeding/i)).toBeInTheDocument();
    expect(screen.getByText(/Tried 3 times. Deluno tries again at every check and has deleted nothing./i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Clear Example.Movie.2026.1080p" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith("/api/download-health/keep-seeding/problems/q1%3Aabc/clear", expect.objectContaining({ method: "POST" })));
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("says a torrent with no category is left alone until one is set", () => {
    loaderData = {
      ...freshInstall(),
      problems: [{
        key: "q1:def", clientId: "q1", clientName: "My qBittorrent", itemId: "def", releaseName: "Other.Movie.2026", kind: "needs-category",
        message: "Other.Movie.2026 is from a private tracker and its rule moves it to keep seeding, but no keep-seeding category is set for it.",
        category: null, firstSeenUtc: "2026-10-11T10:00:00Z", lastTriedUtc: "2026-10-11T10:10:00Z", attempts: 1
      }]
    };
    renderPage();

    expect(screen.getByText(/Left alone until a category is set/i)).toBeInTheDocument();
  });
});

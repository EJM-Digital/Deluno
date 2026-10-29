import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { OnboardingBanner } from "../../components/shell/onboarding-banner";
import { SetupProgressLadder } from "../../components/shell/setup-progress-ladder";
import { queryClient } from "../../lib/route-cache";
import { buildSetupStatus, type SetupStatusInput } from "../../lib/setup-status";
import type { PlatformSettingsSnapshot } from "../../lib/api";
import { fullSummary } from "./downloads-fixtures";
import { LiveTab } from "./live-tab";

const authedFetchMock = vi.fn();
vi.mock("../../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const read = async (path: string) => {
    if (path === "/api/settings") return original.emptyPlatformSettingsSnapshot;
    if (path === "/api/setup/progress") return { lastCompletedStep: 3, isSkipped: false, isCompleted: false, updatedUtc: "2026-10-15T00:00:00Z" };
    return [];
  };
  return { ...original, fetchJson: read, fetchAllPages: read, fetchPageItems: read };
});

beforeEach(() => {
  queryClient.clear();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue({ ok: true, json: async () => ({}) });
});

describe("the Dashboard's setup banner and ladder (low rows)", () => {
  it("'Build my setup' opens the setup guide and 'Open library setup' opens Settings", () => {
    render(<MemoryRouter><OnboardingBanner isSetupSuppressed={false} onDismiss={() => undefined} /></MemoryRouter>);

    expect(screen.getByRole("link", { name: /Build my setup/ })).toHaveAttribute("href", "/setup-guide");
    expect(screen.getByRole("link", { name: "Open library setup" })).toHaveAttribute("href", "/settings");
  });

  it("each ladder step links to the screen that does it", () => {
    const input: SetupStatusInput = {
      libraries: [], downloadClients: [], indexers: [], intakeSources: [], policySets: [], qualityProfiles: [],
      settings: { autoStartJobs: false, workflowVerified: false } as PlatformSettingsSnapshot
    };
    const status = buildSetupStatus(input);
    render(<MemoryRouter><SetupProgressLadder status={status} /></MemoryRouter>);

    const hrefs = status.steps.map((step) => step.to);
    expect(hrefs).toEqual(expect.arrayContaining(["/settings/libraries", "/settings/profiles", "/indexers", "/search-cycles", "/settings/lists"]));
    const rendered = screen.getAllByRole("link").map((link) => link.getAttribute("href"));
    for (const step of status.steps.filter((item) => !item.optional)) expect(rendered).toContain(step.to);
  });

  it("the empty-library row shows while the library is empty and is gone once it has a title", async () => {
    const empty = { ...fullSummary, library: { ...fullSummary.library!, movies: { ...fullSummary.library!.movies, total: 0 }, shows: { ...fullSummary.library!.shows, total: 0 } } };
    const view = render(<QueryClientProvider client={queryClient}><MemoryRouter><LiveTab summary={empty as never} /></MemoryRouter></QueryClientProvider>);
    expect(await screen.findByText("Nothing in your library yet.")).toBeVisible();
    expect(within(view.container).getByRole("link", { name: "Add a movie" })).toHaveAttribute("href", "/movies?add=true");
    view.unmount();

    const held = { ...fullSummary, library: { ...fullSummary.library!, movies: { ...fullSummary.library!.movies, total: 5 } } };
    render(<QueryClientProvider client={queryClient}><MemoryRouter><LiveTab summary={held as never} /></MemoryRouter></QueryClientProvider>);
    await waitFor(() => expect(screen.getByRole("region", { name: /pipeline/i })).toBeInTheDocument());
    expect(screen.queryByText("Nothing in your library yet.")).toBeNull();
  });
});

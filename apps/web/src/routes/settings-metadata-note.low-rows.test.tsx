import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * Settings > Metadata: the note about the ratings region.
 */

vi.setConfig({ testTimeout: 30_000 });
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const liveRefresh = vi.hoisted(() => ({ spec: undefined as { events?: string[] } | undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events?: string[] }) => { liveRefresh.spec = spec; } }));

const loaderData = vi.hoisted(() => ({ settings: {} as Record<string, unknown>, metadataStatus: null as { mode: string } | null }));
const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsMetadataPage } = await import("./settings-metadata-page");
const renderPage = () => render(<MemoryRouter><SettingsMetadataPage /></MemoryRouter>);
const respond = (body: unknown = {}, status = 200) => ({ ok: status < 400, status, json: async () => body, text: async () => JSON.stringify(body), headers: new Headers() });
const optionLabels = (select: HTMLElement) => [...(select as HTMLSelectElement).options].map((option) => option.textContent);

beforeEach(() => {
  cleanup();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue(respond({}));
  revalidateMock.mockReset();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "en", metadataCertificationCountry: "AU" };
  loaderData.metadataStatus = null;
});

describe("the managed metadata service note", () => {
  const note = /The ratings region applies when Deluno looks titles up on TMDb directly/;

  it("is shown only when the managed metadata service answers lookups, in broker and hybrid mode", () => {
    for (const mode of ["broker", "hybrid"]) {
      cleanup();
      loaderData.metadataStatus = { mode };
      renderPage();
      expect(screen.getByText(note)).toBeInTheDocument();
      expect(screen.getByText(/The language does apply\./)).toBeInTheDocument();
    }
  });

  it("claims nothing for a direct TMDb install or when the status is unknown", () => {
    loaderData.metadataStatus = { mode: "direct" };
    const direct = renderPage();
    expect(screen.queryByText(note)).toBeNull();
    direct.unmount();
    loaderData.metadataStatus = null;
    renderPage();
    expect(screen.queryByText(note)).toBeNull();
  });
});

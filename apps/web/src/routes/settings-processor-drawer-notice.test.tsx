import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * C.11.9: a processor connection whose saved secret was stored on another computer says so in its drawer, in plain words,
 * with the server's reason; a readable one says nothing.
 */
vi.mock("../lib/use-api-mutation", () => ({ useApiMutation: () => ({ mutate: vi.fn(), state: "idle", fieldErrors: {} }) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
vi.mock("../lib/connection-versions", () => ({ useConnectionVersions: () => ({ versions: new Map() }), versionKey: (kind: string, id: string) => `${kind}:${id}` }));
vi.mock("../components/app/connection-update-panel", () => ({ ConnectionUpdatePanel: () => null }));
vi.mock("../components/app/connection-version", () => ({ ConnectionVersionBeside: () => null }));

const connection = (over: Record<string, unknown> = {}) => ({
  id: "p1", provider: "weir", name: "Weir", submissionUrl: "http://localhost:9347/api/v1/jobs", healthCheckUrl: null, authHeaderName: "X-Deluno-Secret",
  isEnabled: true, secretConfigured: true, secretUnreadable: false, secretProblem: null, healthStatus: "healthy", ...over
});
const loaderData = vi.hoisted(() => ({ value: {} as Record<string, unknown> }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData.value,
  useRevalidator: () => ({ revalidate: vi.fn(), state: "idle" })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");
const open = (item: unknown) => {
  loaderData.value = { settings: emptyPlatformSettingsSnapshot, libraries: [], qualityProfiles: [], processorConnections: [item] };
  render(<MemoryRouter initialEntries={["/settings/processing?connection=p1"]}><SettingsMediaManagementPage /></MemoryRouter>);
};

afterEach(cleanup);

describe("the processor drawer's unreadable saved secret notice", () => {
  it("says to enter the saved key again, with the server's reason", () => {
    open(connection({ secretUnreadable: true, secretProblem: "The secret was saved on another computer." }));
    const notice = screen.getByText("Enter the saved key again").closest("[role='alert']")!;
    expect(notice).toHaveTextContent("The secret was saved on another computer.");
  });

  it("says nothing for a connection whose secret can be read", () => {
    open(connection());
    expect(screen.queryByText("Enter the saved key again")).toBeNull();
  });
});

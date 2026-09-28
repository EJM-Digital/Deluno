import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";
import { SettingsDownloadHealthPage } from "./settings-download-health-page";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const revalidateMock = vi.fn();
const loaderData = {
  rules: [],
  ignoreEntries: [],
  settings: emptyPlatformSettingsSnapshot,
  connectivityCheckSkips: []
};

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

function jsonResponse(body: unknown, ok = true) {
  return Promise.resolve({
    ok,
    json: () => Promise.resolve(body)
  } as Response);
}

/**
 * #826: "Purge all strikes" must call nothing until the confirm dialog is
 * actually confirmed, and nothing at all when the person backs out of it.
 */
describe("Purge all strikes (#826)", () => {
  afterEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("calls nothing until the confirm dialog is confirmed", async () => {
    authedFetchMock.mockImplementation(() => jsonResponse({ purgedCount: 3 }));

    render(
      <MemoryRouter>
        <SettingsDownloadHealthPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("button", { name: "Purge all strikes" }));

    // The dialog is open, but nothing has been sent to the server yet.
    await waitFor(() => expect(screen.getByText("Purge every recorded strike?")).toBeInTheDocument());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/download-health/purge-strikes", expect.anything());

    // Confirming is the dialog's own button, not the row action that opened it.
    const confirmButtons = screen.getAllByRole("button", { name: "Purge all strikes" });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);

    await waitFor(() =>
      expect(authedFetchMock).toHaveBeenCalledWith("/api/download-health/purge-strikes", expect.objectContaining({ method: "POST" }))
    );
  });

  it("calls nothing when the dialog is cancelled", async () => {
    authedFetchMock.mockImplementation(() => jsonResponse({ purgedCount: 0 }));

    render(
      <MemoryRouter>
        <SettingsDownloadHealthPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("button", { name: "Purge all strikes" }));
    await waitFor(() => expect(screen.getByText("Purge every recorded strike?")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByText("Purge every recorded strike?")).not.toBeInTheDocument());
    expect(authedFetchMock).not.toHaveBeenCalledWith("/api/download-health/purge-strikes", expect.anything());
  });
});

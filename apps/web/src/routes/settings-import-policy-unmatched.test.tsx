import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

/**
 * #949 (Phase 4 re-check of #888): The owner decided the unmatched-import choice
 * -- hold for manual import (default), give up after N days, or retry the
 * match once then treat it as bad -- should be a setting. The backend
 * already held UnmatchedImportPolicy/UnmatchedImportGiveUpDays and persisted
 * them, but no screen offered the choice, so PATCH /api/platform/settings
 * was the only way to change it. This puts it on Settings -> Import Policy,
 * in plain words, saving and reloading through the existing platform
 * settings API.
 */
const mutate = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-api-mutation", () => ({
  useApiMutation: () => ({ mutate, state: "idle", fieldErrors: {} })
}));

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const settings = {
  ...emptyPlatformSettingsSnapshot,
  unmatchedImportPolicy: "hold",
  unmatchedImportGiveUpDays: 7
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLocation: () => ({ pathname: "/settings/import-policy" }),
  useLoaderData: () => ({ settings, libraries: [], qualityProfiles: [], processorConnections: [] })
}));

const { SettingsMediaManagementPage } = await import("./settings-media-management-page-v2");

describe("unmatched import policy (#949)", () => {
  afterEach(() => {
    mutate.mockReset();
  });

  it("shows the three choices in plain words, held by default, with no days field", () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    const group = screen.getByRole("radiogroup", { name: "When a download cannot be matched" });
    expect(screen.getByRole("radio", { name: "Hold for manual import" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "Give up after N days" })).toHaveAttribute("aria-checked", "false");
    expect(screen.getByRole("radio", { name: "Retry once, then treat as bad" })).toHaveAttribute("aria-checked", "false");
    expect(group).toBeInTheDocument();
    expect(screen.queryByLabelText("Give up after")).not.toBeInTheDocument();
  });

  it("reveals a days field only once give up after N days is chosen", () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("radio", { name: "Give up after N days" }));

    expect(screen.getByLabelText("Give up after")).toHaveValue(7);
  });

  it("saves the chosen policy and give-up days together", async () => {
    mutate.mockResolvedValueOnce({ ...settings, unmatchedImportPolicy: "give-up-after-days", unmatchedImportGiveUpDays: 14 });

    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("radio", { name: "Give up after N days" }));
    fireEvent.change(screen.getByLabelText("Give up after"), { target: { value: "14" } });
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(mutate).toHaveBeenCalledWith(expect.objectContaining({
      unmatchedImportPolicy: "give-up-after-days",
      unmatchedImportGiveUpDays: 14
    }));
  });

  it("reloads the saved policy after a save, through the same settings response", async () => {
    mutate.mockResolvedValueOnce({ ...settings, unmatchedImportPolicy: "retry-once-then-bad" });

    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("radio", { name: "Retry once, then treat as bad" }));
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    await vi.waitFor(() => expect(mutate).toHaveBeenCalled());
    expect(await screen.findByRole("radio", { name: "Retry once, then treat as bad" })).toHaveAttribute("aria-checked", "true");
  });

  it("refuses to save a give-up time outside 1-365 days", async () => {
    render(
      <MemoryRouter>
        <SettingsMediaManagementPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("radio", { name: "Give up after N days" }));
    fireEvent.change(screen.getByLabelText("Give up after"), { target: { value: "0" } });
    fireEvent.click(screen.getByRole("button", { name: "Save import settings" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Choose a give-up time between 1 and 365 days.");
    expect(mutate).not.toHaveBeenCalled();
  });
});

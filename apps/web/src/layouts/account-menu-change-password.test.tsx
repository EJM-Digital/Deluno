import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DesktopSidebar } from "./app-layout";

const changePassword = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-auth")>()),
  useAuth: () => ({ changePassword, token: "t" })
}));
vi.mock("../lib/use-host-name", () => ({ useHostName: () => "WORKSTATION", hostTitle: () => "Deluno" }));

const attention = { movieWanted: 0, tvWanted: 0, indexerAlerts: 0, failedJobs: 0, downloadsNeedingAction: 0 } as unknown as Parameters<typeof DesktopSidebar>[0]["attention"];
const user = { id: "1", username: "ann", displayName: "Ann Lee", avatarInitials: "AL" } as Parameters<typeof DesktopSidebar>[0]["user"];

function renderSidebar() {
  return render(
    <MemoryRouter>
      <DesktopSidebar attention={attention} user={user} onLogout={() => undefined} collapsed={false} onToggleCollapsed={() => undefined} />
    </MemoryRouter>
  );
}

function openDialog() {
  fireEvent.click(screen.getByRole("button", { name: "Change password" }));
}

function fill(current: string, next: string, confirm: string) {
  fireEvent.change(screen.getByLabelText("Current password"), { target: { value: current } });
  fireEvent.change(screen.getByLabelText("New password"), { target: { value: next } });
  fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: confirm } });
}

describe("the account menu's Change password dialog", () => {
  beforeEach(() => changePassword.mockReset());
  afterEach(() => { cleanup(); vi.useRealTimers(); });

  it("opens from the menu with Current, New and Confirm fields, and Cancel and Close both dismiss it", () => {
    renderSidebar();
    expect(screen.queryByRole("heading", { name: "Change password" })).toBeNull();

    openDialog();
    expect(screen.getByRole("heading", { name: "Change password" })).toBeTruthy();
    for (const label of ["Current password", "New password", "Confirm new password"]) expect(screen.getByLabelText(label)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("heading", { name: "Change password" })).toBeNull();

    openDialog();
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("heading", { name: "Change password" })).toBeNull();
  });

  it("mismatched new passwords say New passwords do not match and send nothing", async () => {
    renderSidebar();
    openDialog();
    fill("old-password-1", "new-password-1", "new-password-2");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByText("New passwords do not match.")).toBeTruthy();
    expect(changePassword).not.toHaveBeenCalled();
  });

  it("shows the server's reason when the current password is wrong or the new one is refused, and keeps the dialog open", async () => {
    changePassword.mockRejectedValueOnce(new Error("Current password is not correct."));
    renderSidebar();
    openDialog();
    fill("wrong", "new-password-1", "new-password-1");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByText("Current password is not correct.")).toBeTruthy();
    expect(changePassword).toHaveBeenCalledExactlyOnceWith("wrong", "new-password-1");
    expect(screen.getByRole("heading", { name: "Change password" })).toBeTruthy();
    expect((screen.getByLabelText("New password") as HTMLInputElement).value).toBe("new-password-1");
  });

  it("success says Password changed, clears the three fields and closes after about a second", async () => {
    changePassword.mockResolvedValueOnce(undefined);
    renderSidebar();
    openDialog();
    fill("old-password-1", "new-password-1", "new-password-1");

    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByText("Password changed.")).toBeTruthy();
    expect((screen.getByLabelText("Current password") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("New password") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("Confirm new password") as HTMLInputElement).value).toBe("");
    expect(screen.getByRole("heading", { name: "Change password" })).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Change password" })).toBeNull(), { timeout: 3000 });
  });
});

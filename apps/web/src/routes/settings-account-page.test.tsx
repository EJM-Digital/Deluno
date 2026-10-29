import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { systemSettingsNavItems } from "../components/app/settings-shell";
import { SettingsAccountPage } from "./settings-account-page";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Settings, Account (#1392)", () => {
  it("is a tab among the installation preferences and shows how many recovery codes are left", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ remaining: 6, made: true }), { status: 200 })));

    render(
      <MemoryRouter initialEntries={["/settings/account"]}>
        <SettingsAccountPage />
      </MemoryRouter>
    );

    expect(systemSettingsNavItems.map((item) => item.label)).toContain("Account");
    expect(systemSettingsNavItems.find((item) => item.label === "Account")?.to).toBe("/settings/account");
    expect(screen.getByText("Recovery Codes")).toBeTruthy();
    expect(await screen.findByText("6 of 10 recovery codes left.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Make new codes" })).toBeTruthy();
  });
});

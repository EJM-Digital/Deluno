import { matchRoutes } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

vi.mock("./bootstrap-status", () => ({ readRequiresSetup: async () => false }));

const { router } = await import("../router");
const { settingsCommands } = await import("./command-registry");

describe("the command palette's Settings entries", () => {
  const wanted = ["/settings/general", "/settings/ui", "/settings/metadata", "/settings/tags", "/settings/auto-tagging", "/settings/lists", "/settings/notifications", "/settings/migration", "/settings/account"];

  it("has an entry for General, Interface, Metadata, Tags, Auto-tagging, Import Lists, Notifications and Migration, each going to its own address", () => {
    const byTarget = new Map(settingsCommands.map((item) => [item.to, item.label]));
    for (const target of wanted) expect(byTarget.has(target), target).toBe(true);
    expect(byTarget.get("/settings/general")).toContain("General");
    expect(byTarget.get("/settings/ui")).toContain("Interface");
    expect(byTarget.get("/settings/metadata")).toContain("Metadata");
    expect(byTarget.get("/settings/tags")).toContain("Tags");
    expect(byTarget.get("/settings/auto-tagging")).toContain("Auto-tagging");
    expect(byTarget.get("/settings/lists")).toMatch(/Lists/);
    expect(byTarget.get("/settings/notifications")).toContain("Notifications");
    expect(byTarget.get("/settings/migration")).toContain("Migration");
    expect(byTarget.get("/settings/account")).toBe("System & Settings · Account");
  });

  it("finds the Account entry by what a person would type when they have lost their sign-in (#1392)", () => {
    const account = settingsCommands.find((item) => item.id === "settings.account")!;
    expect(account.group).toBe("navigation");
    expect(account.keywords).toEqual(["recovery", "codes", "password", "username", "sign in", "account", "forgot"]);
    expect(account.to).toBe("/settings/account");
  });

  it("goes to an address the router has a screen for, for every entry", () => {
    for (const item of settingsCommands.filter((entry) => entry.to)) {
      const matches = matchRoutes(router.routes, item.to!);
      expect(matches, item.to).not.toBeNull();
      const last = matches![matches!.length - 1].route;
      // A screen, or a redirect to one: it renders something rather than falling through to the not-found route.
      expect(last.path, item.to).not.toBe("*");
    }
  });
});

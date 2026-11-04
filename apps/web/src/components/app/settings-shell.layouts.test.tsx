import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConfigurationWorkspaceLayout, SettingsShell, SettingsWorkspaceLayout, SystemShell, configurationNavAreas, maintenanceNavItems, settingsPageMeta } from "./settings-shell";

afterEach(() => { vi.doUnmock("../../lib/configuration-areas"); vi.resetModules(); });

const MEDIA = "Manage where your media lives, how it is named, and how it moves through Deluno.";

describe("the name and words of every settings page", () => {
  const pages: Array<[string, string, string]> = [
    ["/settings", "Setup Overview", "Guided configuration for your media library, quality policy, automation, and runtime behaviour."],
    ["/settings/processing", "Media Management", MEDIA],
    ["/settings/import-policy", "Media Management", MEDIA],
    ["/settings/media-management", "Media Management", MEDIA],
    ["/settings/libraries", "Media Management", MEDIA],
    ["/settings/destination-rules", "Media Management", MEDIA],
    ["/settings/policy-sets", "Your Libraries", "What each library should want from a release, and what that means in practice."],
    ["/settings/profiles", "Quality Profiles", "The complete quality standard used by Library Profiles: tiers, sizes, release preferences, exclusions, and upgrades."],
    ["/settings/quality", "Size Rules", "File-size boundaries Library Profiles use to reject releases that are too small or too large."],
    ["/settings/custom-formats", "Release Preferences", "Preference rules for source, codec, HDR, language, group, and custom-format scoring used by Library Profiles."],
    ["/settings/release-rules", "Acquisition Rules", "Hold, prefer, or reject releases by tag, protocol, timing, and release terms before they reach a download client."],
    ["/settings/lists", "Discover Media", "Watchlists and curated lists that can add the movies or shows you want Deluno to manage."],
    ["/settings/account", "Account", "Recovery codes: the way back in if you forget your username or password."],
    ["/settings/migration", "Migration Assistant", "Preview and import external media automation configuration without overwriting existing Deluno setup."],
    ["/settings/metadata", "Media Management", MEDIA],
    ["/settings/subtitles", "Media Management", MEDIA],
    ["/settings/auto-tagging", "Media Management", MEDIA],
    ["/settings/tags", "Media Management", MEDIA],
    ["/settings/general", "General", "Network address, port, and reverse-proxy routing."],
    ["/settings/notifications", "Notifications", "Outbound webhook events for grabs, imports, upgrades, and health alerts."],
    ["/settings/ui", "Interface", "Theme, default views, and how Deluno should feel on your display."]
  ];

  it.each(pages)("%s is %s", (path, title, description) => {
    const meta = settingsPageMeta.find((item) => item.match(path));
    expect(meta?.title).toBe(title);
    expect(meta?.description).toBe(description);
  });

  it("names a page by its own entry, not by one before it", () => {
    for (const [path, title] of pages) expect(settingsPageMeta.find((item) => item.match(path))?.title).toBe(title);
    expect(pages).toHaveLength(settingsPageMeta.length);
  });

  it("names nothing that is not a settings page", () => {
    expect(settingsPageMeta.find((item) => item.match("/settings/unknown"))).toBeUndefined();
    expect(settingsPageMeta.find((item) => item.match("/system"))).toBeUndefined();
    expect(settingsPageMeta.find((item) => item.match("/settings/"))).toBeUndefined();
  });

  it("has every page but the overview without a page chrome of its own", () => {
    const withChrome = settingsPageMeta.filter((item) => !("chrome" in item));
    expect(withChrome.map((item) => item.title)).toEqual(["Setup Overview"]);
    expect(settingsPageMeta.filter((item) => "chrome" in item).every((item) => (item as { chrome: string }).chrome === "none")).toBe(true);
  });
});

describe("the sidebar areas", () => {
  it("lists Media Management, Quality & Release, Find & Download, Automation & Recovery and Discover Media with their first page", () => {
    expect(configurationNavAreas.map((area) => [area.label, area.icon, area.to, area.tabsInToolbar])).toEqual([
      ["Media Management", "library", "/settings/libraries", true],
      ["Quality & Release", "plans", "/settings/policy-sets", true],
      ["Find & Download", "connections", "/indexers/indexers", true],
      ["Automation & Recovery", "automation", "/search-cycles", true],
      ["Discover Media", "discover", "/settings/lists", true]
    ]);
  });

  it("lists each area's tabs in order", () => {
    const labels = (name: string) => configurationNavAreas.find((area) => area.label === name)!.items.map((item) => [item.label, item.to]);
    expect(labels("Quality & Release")).toEqual([
      ["Your Libraries", "/settings/policy-sets"],
      ["Quality Profiles", "/settings/profiles"],
      ["Size Rules", "/settings/quality"],
      ["Release Preferences", "/settings/custom-formats"],
      ["Acquisition Rules", "/settings/release-rules"]
    ]);
    expect(labels("Find & Download")).toEqual([
      ["Indexers", "/indexers/indexers"],
      ["Proxies", "/indexers/proxies"],
      ["History", "/indexers/history"],
      ["Scoreboard", "/indexers/scoreboard"],
      ["Download Clients", "/indexers/download-clients"],
      ["Remote Path Mappings", "/indexers/path-mappings"],
      ["Library Routing", "/indexers/library-routing"],
      ["Subtitle Providers", "/indexers/subtitle-providers"],
      ["Media Servers", "/indexers/media-servers"],
      ["Companion Tools", "/indexers/companion-tools"]
    ]);
    expect(labels("Discover Media")).toEqual([["Import Lists", "/settings/lists"]]);
    expect(configurationNavAreas.every((area) => area.items.every((item) => item.end === false || (item as { end: boolean }).end === true))).toBe(true);
  });

  it("matches each area's pages and no others", () => {
    const area = (name: string) => configurationNavAreas.find((item) => item.label === name)!;
    expect(area("Quality & Release").match("/settings/profiles")).toBe(true);
    expect(area("Quality & Release").match("/system")).toBe(false);
    expect(area("Find & Download").match("/indexers/history")).toBe(true);
    expect(area("Automation & Recovery").match("/search-cycles/blocklist")).toBe(true);
  });

  it("has Preferences and System as the two installation areas", () => {
    expect(maintenanceNavItems.map((area) => [area.label, area.icon, area.to, area.tabsInToolbar])).toEqual([
      ["Preferences", "setup", "/settings/general", true],
      ["System", "system", "/system", true]
    ]);
    expect(maintenanceNavItems[0].items.map((item) => item.label)).toEqual(["General", "Interface", "Notifications", "Migration", "Account"]);
    expect(maintenanceNavItems[1].items.map((item) => item.label)).toEqual(["Health", "Audit", "Security", "Backups", "Recycle bin", "Updates", "API Access", "Help & Guides"]);
  });

  it("refuses to start when an area it names is missing from the configuration areas", async () => {
    vi.resetModules();
    vi.doMock("../../lib/configuration-areas", () => ({ CONFIGURATION_AREAS: [] }));
    await expect(import("./settings-shell")).rejects.toThrow("Unknown configuration area: media-management");
  });
});

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/settings" element={<SettingsWorkspaceLayout />}>
          <Route index element={<p>overview page</p>} />
          <Route path="general" element={<SettingsShell description="never shown"><p>general page</p></SettingsShell>} />
          <Route path="unknown" element={<p>unknown page</p>} />
        </Route>
        <Route path="/config" element={<ConfigurationWorkspaceLayout />}>
          <Route index element={<p>config page</p>} />
        </Route>
      </Routes>
    </MemoryRouter>
  );
}

describe("the settings workspace", () => {
  it("wraps the overview in the settings shell, with the glossary button", () => {
    renderAt("/settings");
    expect(screen.getByText("overview page")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Open glossary" })).toBeInTheDocument();
  });

  it("gives a page that carries its own toolbar no shell around it", () => {
    renderAt("/settings/general");
    expect(screen.getByText("general page")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open glossary" })).toBeNull();
    expect(screen.queryByText("never shown")).toBeNull();
  });

  it("treats a settings page it has no name for like a page that carries its own toolbar", () => {
    renderAt("/settings/unknown");
    expect(screen.getByText("unknown page")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open glossary" })).toBeNull();
  });

  it("passes the configuration workspace's page straight through", () => {
    renderAt("/config");
    expect(screen.getByText("config page")).toBeInTheDocument();
  });
});

describe("the settings shell", () => {
  it("says what the page is for, and opens and closes the glossary", async () => {
    const user = userEvent.setup();
    render(<MemoryRouter><SettingsShell description="Guided configuration.">page body</SettingsShell></MemoryRouter>);
    expect(screen.getByText("Guided configuration.")).toBeInTheDocument();
    expect(screen.getByText("page body")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).toBeNull();
    const open = screen.getByRole("button", { name: "Open glossary" });
    expect(open).toHaveAttribute("title", "Open glossary");
    await user.click(open);
    expect(screen.getByRole("dialog", { name: "Glossary" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await user.click(screen.getAllByRole("button").find((button) => button !== open && button.querySelector("svg"))!);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("leaves out the description when there is none", () => {
    const { container } = render(<MemoryRouter><SettingsShell>page body</SettingsShell></MemoryRouter>);
    expect(container.querySelector("p")).toBeNull();
    expect(screen.getByText("page body")).toBeInTheDocument();
  });
});

describe("the system shell", () => {
  it("shows the page's content as it is", () => {
    render(<SystemShell title="Health">system content</SystemShell>);
    expect(screen.getByText("system content")).toBeInTheDocument();
  });
});

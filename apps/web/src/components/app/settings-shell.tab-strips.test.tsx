import { cleanup, render, screen } from "@testing-library/react";
import { matchRoutes, MemoryRouter } from "react-router-dom";
import { router } from "../../router";
import { afterEach, describe, expect, it } from "vitest";
import { librarySetupNavItems, systemSettingsNavItems } from "./settings-shell";
import { PageToolbar } from "../ui/page-toolbar";

/**
 * The two settings tab strips (#1329): Media Management (Library & Storage ... Metadata, Subtitles, Subtitle Wanted, Tags,
 * Auto-tagging) and Maintain Deluno (General, Interface, Notifications, Migration, Account). Each tab is a link to its own page,
 * that page is a route of the app, and on that page only that tab is the open one.
 */

afterEach(() => cleanup());

const strips = [
  { name: "Media Management", items: librarySetupNavItems, labels: ["Library & Storage", "Media Naming", "Import Policy", "Processing Workflow", "Final Destinations", "Metadata", "Subtitles", "Subtitle Wanted", "Tags", "Auto-tagging"] },
  { name: "Maintain Deluno", items: systemSettingsNavItems, labels: ["General", "Interface", "Notifications", "Migration", "Account"] }
];

describe.each(strips)("the $name tab strip", ({ items, labels }) => {
  it("lists its tabs in order", () => {
    expect(items.map((item) => item.label)).toEqual(labels);
  });

  it("points every tab at a page the app has", () => {
    for (const item of items) {
      const matches = matchRoutes(router.routes, item.to) ?? [];
      expect(matches.length, item.to).toBeGreaterThan(0);
      expect(matches.at(-1)!.route.path, item.to).not.toBe("*");
    }
  });

  it.each(items.map((item) => [item.label, item.to] as const))("on the %s page that tab alone is the open one and each tab links to its own page", (label, to) => {
    render(<MemoryRouter initialEntries={[to]}><PageToolbar tabs={items.map((item) => ({ ...item, icon: undefined }))} /></MemoryRouter>);

    for (const item of items) {
      const link = screen.getByRole("link", { name: item.label });
      expect(link.getAttribute("href")).toBe(item.to);
      expect(link.className.includes("text-(--toolbar-accent)")).toBe(item.label === label);
    }
  });
});

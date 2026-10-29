import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { configurationNavAreas } from "../app/settings-shell";
import { CONFIGURATION_AREAS } from "../../lib/configuration-areas";
import { PageToolbar } from "./page-toolbar";

// #1329: every configuration area that defines an explainer shows it under the page toolbar, whichever of its tabs is open.

const pathsOf = (areaId: string) => {
  const area = CONFIGURATION_AREAS.find((item) => item.id === areaId)!;
  const fromNav = configurationNavAreas.filter((nav) => area.match(nav.to)).flatMap((nav) => [nav.to, ...nav.items.map((item) => item.to)]);
  const own: Record<string, string[]> = { preferences: ["/settings/general", "/settings/ui", "/settings/notifications", "/settings/migration"], system: ["/system", "/system/audit", "/system/backups"] };
  return Array.from(new Set([...fromNav, ...(own[areaId] ?? [])].filter((path) => area.match(path))));
};

describe("the explainer under the page toolbar (#1329)", () => {
  it.each(CONFIGURATION_AREAS.map((area) => [area.id] as const))("area %s shows its own How this works on each of its tabs, and no other area's", async (areaId) => {
    const area = CONFIGURATION_AREAS.find((item) => item.id === areaId)!;
    const paths = pathsOf(areaId);
    expect(paths.length).toBeGreaterThan(0);

    for (const path of paths) {
      const view = render(<MemoryRouter initialEntries={[path]}><PageToolbar tabs={[{ to: path, label: "Tab" }]} /></MemoryRouter>);
      const toggle = await screen.findByRole("button", { name: "How this works" });
      await userEvent.setup().click(toggle);
      expect(screen.getByText(area.explainer.lead), path).toBeVisible();
      for (const other of CONFIGURATION_AREAS.filter((item) => item.id !== areaId)) {
        expect(screen.queryByText(other.explainer.lead), `${path} must not show ${other.id}`).toBeNull();
      }
      view.unmount();
      window.localStorage.clear();
    }
  });

  it("a page outside every configuration area gets no explainer", () => {
    render(<MemoryRouter initialEntries={["/movies"]}><PageToolbar tabs={[{ to: "/movies", label: "Movies" }]} /></MemoryRouter>);

    expect(screen.queryByRole("button", { name: "How this works" })).toBeNull();
  });
});

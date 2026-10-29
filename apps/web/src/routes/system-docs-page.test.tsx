import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { SystemDocsPage } from "./system-docs-page";
import { CLIENT_PRESETS } from "./connections/presets";

describe("System > Help & Guides", () => {
  it("opens the operating-model guide with its workflow sections and the link back into guided setup", () => {
    render(
      <MemoryRouter initialEntries={["/system/docs"]}>
        <SystemDocsPage />
      </MemoryRouter>
    );

    expect(screen.getByText("Deluno Operating Model")).toBeInTheDocument();
    expect(screen.getByText(/plain-English guide for how Deluno should work/)).toBeInTheDocument();
    expect(screen.getByText(/start over from the beginning/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open" })).toHaveAttribute("href", "/setup-guide");
  });

  it("is reachable at /system/docs: the route table serves this page there, and the System menu links to it", () => {
    const here = path.dirname(fileURLToPath(import.meta.url));
    const routerSource = readFileSync(path.join(here, "..", "router.tsx"), "utf8");
    const docsRoute = /path: "docs",\s*lazy: withSkeleton\(async \(\) => \{\s*const module = await import\("\.\/routes\/system-docs-page"\);\s*return \{ Component: module\.SystemDocsPage \};/;
    expect(routerSource).toMatch(docsRoute);
    // Under the System route, not at the top.
    expect(routerSource.slice(0, routerSource.search(docsRoute)).lastIndexOf('path: "system"')).toBeGreaterThan(-1);
    const shellSource = readFileSync(path.join(here, "..", "components", "app", "settings-shell.tsx"), "utf8");
    expect(shellSource).toContain('{ to: "/system/docs", label: "Help & Guides"');
  });

  function renderGuide() {
    return render(
      <MemoryRouter initialEntries={["/system/docs"]}>
        <SystemDocsPage />
      </MemoryRouter>
    );
  }

  it("shows the eight Operating Model cards, the Start over row, and the seven numbered steps of the Media Lifecycle", () => {
    renderGuide();

    for (const title of ["Beginner Setup", "Routing and Tags", "External Download Clients", "Release Decisions", "Refine Before Import", "Import Hygiene", "Metadata", "API Access"]) {
      expect(screen.getByText(title)).toBeInTheDocument();
    }
    expect(screen.getByText(/start over from the beginning/i)).toBeInTheDocument();
    expect(screen.getByText("Guided setup")).toBeInTheDocument();
    expect(screen.getByText("Media Lifecycle")).toBeInTheDocument();
    for (const number of ["01", "02", "03", "04", "05", "06", "07"]) expect(screen.getByText(number)).toBeInTheDocument();
    expect(screen.queryByText("08")).not.toBeInTheDocument();
  });

  it("the Guided setup Open link goes to /setup-guide", () => {
    renderGuide();
    expect(screen.getAllByRole("link", { name: "Open" })).toHaveLength(1);
    expect(screen.getByRole("link", { name: "Open" })).toHaveAttribute("href", "/setup-guide");
  });

  it("claims only what the product does: every download client it names is one Deluno can add, legacy scoring is provenance only, ffprobe is used when available, hardlinks are called single-copy links", () => {
    renderGuide();

    const clients = screen.getByText(/Use qBittorrent, SABnzbd/).textContent ?? "";
    const named = ["qBittorrent", "SABnzbd", "NZBGet", "Deluge", "Transmission", "uTorrent"];
    for (const name of named) {
      expect(clients).toContain(name);
      expect(CLIENT_PRESETS.map((preset) => preset.label)).toContain(name);
    }
    expect(screen.getByText(/Any legacy scoring values are provenance only/)).toBeInTheDocument();
    expect(screen.getByText(/validate with ffprobe when available/)).toBeInTheDocument();
    expect(screen.getByText(/creates single-copy links \(hardlinks\) or moves/)).toBeInTheDocument();
  });
});

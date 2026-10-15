import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { SystemDocsPage } from "./system-docs-page";

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
});

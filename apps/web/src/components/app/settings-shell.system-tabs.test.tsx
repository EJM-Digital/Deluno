import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { SystemWorkspaceLayout } from "./settings-shell";

/** The System tab bar (#1329): clicking a tab goes to its page and no other. */

function Where() {
  return <output aria-label="where">{useLocation().pathname}</output>;
}

function renderBar(start = "/system") {
  return render(
    <MemoryRouter initialEntries={[start]}>
      <Routes>
        <Route path="/system" element={<SystemWorkspaceLayout />}>
          <Route index element={<Where />} />
          <Route path="audit" element={<Where />} />
          <Route path="security" element={<Where />} />
          <Route path="backups" element={<Where />} />
          <Route path="recycle-bin" element={<Where />} />
          <Route path="updates" element={<Where />} />
          <Route path="api" element={<Where />} />
          <Route path="docs" element={<Where />} />
        </Route>
      </Routes>
    </MemoryRouter>
  );
}

describe("System tab bar", () => {
  it("lists Health, Audit, Security, Backups, Recycle bin, Updates, API Access and Help & Guides, and each tab opens its own page", () => {
    renderBar();

    const expected: Array<[string, string]> = [
      ["Audit", "/system/audit"],
      ["Security", "/system/security"],
      ["Backups", "/system/backups"],
      ["Recycle bin", "/system/recycle-bin"],
      ["Updates", "/system/updates"],
      ["API Access", "/system/api"],
      ["Help & Guides", "/system/docs"],
      ["Health", "/system"]
    ];
    for (const [name, path] of expected) {
      fireEvent.click(screen.getByRole("link", { name: new RegExp(`^${name}$`, "i") }));
      expect(screen.getByLabelText("where").textContent).toBe(path);
    }
  });

  it("marks the tab of the page it is on as the current one and no other", () => {
    renderBar("/system/recycle-bin");

    expect(screen.getByRole("link", { name: /^Recycle bin$/i })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: /^Health$/i })).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("link", { name: /^Security$/i })).not.toHaveAttribute("aria-current");
  });

  it.each(["/system", "/system/audit", "/system/security", "/system/backups", "/system/recycle-bin", "/system/updates", "/system/api", "/system/docs"])(
    "shows the System 'How this works' explainer under the tabs on %s",
    (path) => {
      renderBar(path);
      expect(screen.getByRole("button", { name: "How this works" })).toBeInTheDocument();
    }
  );
});

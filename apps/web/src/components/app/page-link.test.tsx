import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { PageLink } from "./page-link";
import { automationNavItems } from "./settings-shell";

vi.mock("../../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

/** #1138: a sentence that names another page names it as a link to that page. */
describe("page references are links", () => {
  it("Transfers goes to the queue and Failed Downloads to its automation tab", () => {
    render(
      <MemoryRouter>
        <PageLink page="transfers" />
        <PageLink page="failedDownloads" />
        <PageLink page="downloadHealth" />
      </MemoryRouter>
    );
    expect(screen.getByRole("link", { name: "Transfers" }).getAttribute("href")).toBe("/queue");
    expect(screen.getByRole("link", { name: /Failed Downloads/ }).getAttribute("href")).toBe("/search-cycles/failed-downloads");
    expect(screen.getByRole("link", { name: /Download Health/ }).getAttribute("href")).toBe("/search-cycles/download-health");
    for (const href of ["/search-cycles/failed-downloads", "/search-cycles/download-health"]) {
      expect(automationNavItems.some((item) => item.to === href)).toBe(true);
    }
  });
});

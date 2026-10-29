import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * #1431: every Preferences setting is one of four controls: a read-only box, an editable box, a dropdown or a toggle.
 * This walks the General and Interface pages and fails on any other control standing in for a setting.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/display-preferences", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/display-preferences")>()),
  useDisplayPreferences: () => ({ preferences: {}, replaceFromSettings: vi.fn() })
}));
vi.mock("../lib/color-mode", () => ({ useColorMode: () => ({ colorMode: "standard", setColorMode: vi.fn() }), isColorMode: () => true }));
vi.mock("next-themes", () => ({ useTheme: () => ({ theme: "system", setTheme: vi.fn() }) }));

const loaderData = { libraries: [], qualityProfiles: [], settings: emptyPlatformSettingsSnapshot };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsGeneralPage } = await import("./settings-general-page");
const { SettingsUiPage } = await import("./settings-ui-page");

function answerEverything() {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("log-level")) return new Response(JSON.stringify({ level: "Information" }), { status: 200 });
    if (url.includes("lan-access")) return new Response(JSON.stringify({ state: "open", port: 5099, message: "" }), { status: 200 });
    if (url.includes("update-check")) return new Response(JSON.stringify({ isEnabled: false, intervalHours: 24, lastCheckedUtc: null, lastUpdatedUtc: null, status: "idle", error: null, updatedTrackerIds: [] }), { status: 200 });
    return new Response("{}", { status: 200 });
  });
}

/** The controls of the page that are settings (buttons that act, like Save and Test, are not settings). */
function settingControls(container: HTMLElement) {
  return Array.from(container.querySelectorAll<HTMLElement>("input, select, textarea, [role=switch], [role=radio], [role=radiogroup], [role=checkbox], [role=slider], [role=tablist]"))
    .filter((node) => !node.closest("nav, [role=navigation]"));
}

function kind(node: HTMLElement) {
  if (node.matches("select")) return "dropdown";
  if (node.matches("[role=switch]")) return "toggle";
  if (node instanceof HTMLInputElement && ["text", "number", "url", "search", ""].includes(node.type)) return node.readOnly ? "read-only box" : "editable box";
  return `other (${node.tagName.toLowerCase()} ${node.getAttribute("role") ?? ""})`;
}

describe.each([
  ["General", SettingsGeneralPage],
  ["Interface", SettingsUiPage]
])("the %s Preferences page (#1431)", (_name, Page) => {
  it("uses only a read-only box, an editable box, a dropdown or a toggle for its settings", async () => {
    answerEverything();
    const { container } = render(<MemoryRouter><Page /></MemoryRouter>);
    await waitFor(() => expect(container.querySelectorAll("label").length).toBeGreaterThan(2));
    await new Promise((resolve) => setTimeout(resolve, 50));

    const kinds = settingControls(container).map(kind);
    expect(kinds.length).toBeGreaterThan(2);
    expect(kinds.filter((item) => item.startsWith("other"))).toEqual([]);
    // No segmented controls, no list-table of Setting/Value rows.
    expect(container.querySelector("[role=radiogroup], [role=table], [role=grid]")).toBeNull();
  });
});

describe("the General page's Port (#1431)", () => {
  it("is a read-only box holding the port the server reports", async () => {
    answerEverything();
    render(<MemoryRouter><SettingsGeneralPage /></MemoryRouter>);
    const port = await screen.findByLabelText("Port");
    await waitFor(() => expect(port).toHaveValue("5099"));
    expect(port).toHaveAttribute("readonly");
  });
});

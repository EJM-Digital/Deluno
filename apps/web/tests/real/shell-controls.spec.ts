import { expect, test } from "@playwright/test";
import { api, openScreen, PHONE, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * The shell's own controls on the real server, nothing stubbed (measure 4, issue 1270): the command palette, the keyboard
 * shortcut list, the notifications bell, the glossary, the folded tabs of a narrow header, and the phone's More sheet.
 */

test.describe("Command palette", () => {
  test("Ctrl+K opens it, typing narrows it, choosing an entry goes to the screen, and the screen is then a recent", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    await page.evaluate(() => localStorage.removeItem("deluno-cmd-recents"));
    await page.keyboard.press("Control+k");
    const palette = page.getByRole("dialog", { name: "Command palette" });
    await expect(palette).toBeVisible();
    const box = palette.getByPlaceholder("Jump to area, run action…");
    await expect(box).toBeFocused();

    await box.fill("zzzz-nothing-matches-this");
    await expect(palette.getByText("No matches.")).toBeVisible();

    await box.fill("schedule");
    await palette.getByRole("option", { name: /^Schedule/ }).click();
    await expect(palette).toHaveCount(0);
    await expect(page).toHaveURL(/\/calendar/);

    await page.keyboard.press("Control+k");
    await expect(palette.getByText("Recents", { exact: true })).toBeVisible();
    await expect(palette.getByRole("option", { name: /Schedule\s*\/calendar/ })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(palette).toHaveCount(0);
  });

  test("a Preferences action runs from the palette: the theme flips, is saved on the server, and flips back the same way", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    const toggle = page.getByRole("button", { name: /^Switch to (dark|light) mode$/ });
    // The starting theme is what the server has stored (an earlier spec may have left either); the screen takes it up after the
    // settings arrive, so wait until the toggle agrees before flipping.
    const stored = (await api<{ uiTheme: string }>(page, "GET", "/api/settings")).body.uiTheme;
    if (stored === "dark" || stored === "light") {
      await expect(toggle).toHaveAttribute("aria-label", stored === "dark" ? "Switch to light mode" : "Switch to dark mode");
    }
    const startedDark = (await toggle.getAttribute("aria-label")) === "Switch to light mode";
    const flip = async () => {
      const requests = recordRequests(page);
      await page.keyboard.press("Control+k");
      const palette = page.getByRole("dialog", { name: "Command palette" });
      await palette.getByPlaceholder("Jump to area, run action…").fill("mode");
      await palette.getByRole("option", { name: /^Switch to (dark|light) mode$/ }).click();
      await expect(palette).toHaveCount(0);
      requests.stop();
      return requests.find("PATCH", /^\/api\/settings$/);
    };
    try {
      const saved = await flip();
      expect(saved).toHaveLength(1);
      expect(saved[0].body).toMatchObject({ uiTheme: startedDark ? "light" : "dark" });
      await expect(toggle).toHaveAttribute("aria-label", startedDark ? "Switch to dark mode" : "Switch to light mode");
      expect((await api<{ uiTheme: string }>(page, "GET", "/api/settings")).body.uiTheme).toBe(startedDark ? "light" : "dark");
    } finally {
      const back = await flip();
      expect(back[0]?.body).toMatchObject({ uiTheme: startedDark ? "dark" : "light" });
    }
    await expect(toggle).toHaveAttribute("aria-label", startedDark ? "Switch to light mode" : "Switch to dark mode");
  });
});

test.describe("Keyboard shortcuts", () => {
  test("? lists the shortcuts, Close shuts the list, and g then a letter goes to a screen", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    await page.keyboard.press("Shift+Slash");
    const list = page.getByRole("dialog", { name: "Keyboard shortcuts" });
    await expect(list).toBeVisible();
    expect((await list.innerText()).length).toBeGreaterThan(40);
    await list.getByRole("button", { name: "Close", exact: true }).click();
    await expect(list).toHaveCount(0);

    await page.keyboard.press("g");
    await page.keyboard.press("a");
    await expect(page).toHaveURL(/\/activity/);
  });
});

test.describe("Notifications bell", () => {
  test("opens a panel with Needs you, Show all activity goes to Activity, and Escape gives focus back", async ({ page }) => {
    await openScreen(page, "/");
    await untilQuiet(page);
    const bell = page.getByRole("button", { name: /^Notifications/ });
    await bell.click();
    const panel = page.getByRole("dialog").filter({ hasText: "Show all activity" });
    await expect(panel).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(panel).toHaveCount(0);
    await expect(bell).toBeFocused();

    await bell.click();
    await panel.getByRole("link", { name: /Show all activity/ }).or(panel.getByRole("button", { name: /Show all activity/ })).first().click();
    await expect(page).toHaveURL(/\/activity/);
    await expect(panel).toHaveCount(0);
  });
});

test.describe("Glossary", () => {
  test("the help button on a settings screen opens the glossary and Close shuts it", async ({ page }) => {
    await openScreen(page, "/settings");
    await untilQuiet(page);
    await page.getByRole("button", { name: "Open glossary", exact: true }).click();
    const glossary = page.getByRole("dialog").filter({ has: page.locator("#glossary-title") });
    await expect(glossary).toBeVisible();
    await expect(glossary.getByText("Quality Profile", { exact: true }).first()).toBeVisible();
    await glossary.getByRole("button", { name: "Close", exact: true }).last().click();
    await expect(glossary).toHaveCount(0);
  });
});

test.describe("Header tabs", () => {
  test("a header too narrow for its tabs folds the rest into More: a choice opens that screen, Escape and the arrow key work by keyboard", async ({ page }) => {
    await openScreen(page, "/indexers/indexers");
    await page.setViewportSize({ width: 1100, height: 900 });
    await untilQuiet(page);
    const more = page.getByRole("button", { name: "More", exact: true });
    await expect(more).toBeVisible();
    await expect(page.getByRole("link", { name: "Download Clients", exact: true })).toHaveCount(0);

    await more.click();
    const menu = page.getByRole("menu", { name: "More sections" });
    await expect(menu).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(menu).toHaveCount(0);
    await expect(more).toBeFocused();

    await page.keyboard.press("ArrowDown");
    await expect(menu).toBeVisible();
    await menu.getByRole("menuitem", { name: "Download Clients", exact: true }).click();
    await expect(page).toHaveURL(/\/indexers\/download-clients/);
    await expect(menu).toHaveCount(0);
  });
});

test.describe("More on a phone", () => {
  test("More destinations opens the sheet, a destination goes there, and Sign out signs out", async ({ page }) => {
    await openScreen(page, "/");
    await page.setViewportSize(PHONE);
    await untilQuiet(page);
    const more = page.getByRole("button", { name: "More destinations", exact: true });
    await more.click();
    const nav = page.getByRole("navigation", { name: "Additional destinations" });
    await expect(nav).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(nav).toHaveCount(0);

    // Every area's own destinations are the tabs in its toolbar, so no area here has an arrow of its own.
    await more.click();
    await expect(nav.getByRole("button", { name: /^(Expand|Collapse) / })).toHaveCount(0);
    await nav.getByRole("link", { name: /Activity/ }).first().click();
    await expect(page).toHaveURL(/\/activity/);

    await more.click();
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    await expect(page).toHaveURL(/\/login/);
    expect(await page.evaluate(() => localStorage.getItem("deluno-auth-token"))).toBeNull();
  });
});

test.describe("Confirm dialogs", () => {
  test("the unsaved-changes question has three ways out: Cancel (Escape) stays, Discard and continue leaves without saving, and nothing was written", async ({ page }) => {
    await openScreen(page, "/settings/general");
    await untilQuiet(page);
    const field = page.getByRole("textbox", { name: "Helper address" });
    const before = await field.inputValue();
    await field.fill("http://127.0.0.1:7777");
    const prompt = page.getByRole("dialog", { name: "Unsaved changes" });

    await page.getByRole("link", { name: "Interface", exact: true }).first().click();
    await expect(prompt).toBeVisible();
    await expect(prompt.getByRole("button", { name: "Cancel", exact: true })).toBeVisible();
    await prompt.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(prompt).toHaveCount(0);
    await expect(page).toHaveURL(/\/settings\/general/);
    await expect(field).toHaveValue("http://127.0.0.1:7777");

    const requests = recordRequests(page);
    await page.getByRole("link", { name: "Interface", exact: true }).first().click();
    await prompt.getByRole("button", { name: "Discard and continue", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\/ui/);
    requests.stop();
    expect(requests.seen.filter((r) => r.method !== "GET" && /^\/api\/settings/.test(r.path)), "discarding sends no save").toEqual([]);
    await page.goto("/settings/general");
    await expect(page.getByRole("textbox", { name: "Helper address" })).toHaveValue(before);
  });
});

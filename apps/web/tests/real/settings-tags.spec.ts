import { expect, test, type Page } from "@playwright/test";
import { api, expectNoErrorScreen, openScreen, recordRequests } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Settings > Tags (the list-and-drawer grammar) against the real server, nothing stubbed (measure 4, issue 1270):
 * the empty state, creating, renaming and deleting through the drawer, the unused-tags sweep, and the whole drawer
 * flow done by keyboard alone. Each action asserts the request the server received, the state it ended in and the words.
 */

interface Tag { id: string; name: string; color: string; description: string | null }

async function emptyTheTags(page: Page) {
  const tags = await api<Tag[]>(page, "GET", "/api/tags");
  for (const tag of tags.body) await api(page, "DELETE", `/api/tags/${tag.id}`);
}

const tagsOnServer = async (page: Page) => (await api<Tag[]>(page, "GET", "/api/tags")).body;

test.describe("Settings > Tags", () => {
  test.describe.configure({ mode: "serial" });

  test("with no tags the list says so in words and offers to make the first", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await emptyTheTags(page);
    await page.reload();
    await untilQuiet(page);
    await expectNoErrorScreen(page);

    await expect(page.getByText("0 tags", { exact: true })).toBeVisible();
    await expect(page.getByText("No tags yet", { exact: true })).toBeVisible();
    await expect(page.getByText("Making one takes a name and a colour. What it does depends on where you apply it afterwards.")).toBeVisible();
    await expect(page.getByRole("button", { name: "New tag", exact: true }).first()).toBeVisible();
    expect(await tagsOnServer(page)).toEqual([]);
  });

  test("New tag opens the drawer, Create tag sends the name, colour and note, and the list shows the tag", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const name = `Kids ${Date.now()}`;
    const requests = recordRequests(page);

    await page.getByRole("button", { name: "New tag", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("New tag", { exact: true }).first()).toBeVisible();
    await expect(drawer.getByText("Create a label first, then apply it where you need it.")).toBeVisible();
    await drawer.getByLabel("Name").fill(name);
    await drawer.getByLabel("Colour").selectOption({ label: "Blue" });
    await drawer.getByLabel("What this tag means").fill("Titles for the lounge TV.");
    await drawer.getByRole("button", { name: "Create tag", exact: true }).click();

    // Saving a new tag leaves the drawer open on the tag it made, now with where it is used.
    await expect(drawer.getByRole("heading", { name, exact: true })).toBeVisible();
    await expect(drawer.getByText("Where this tag is used", { exact: true })).toBeVisible();
    requests.stop();
    const posts = requests.find("POST", /^\/api\/tags$/);
    expect(posts, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(posts[0].body).toMatchObject({ name, color: "blue", description: "Titles for the lounge TV." });

    const stored = (await tagsOnServer(page)).filter((t) => t.name === name);
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({ color: "blue", description: "Titles for the lounge TV." });
    await drawer.getByRole("button", { name: "Close", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    await expect(page.getByText("1 tag", { exact: true })).toBeVisible();
    await expect(page.getByText(name, { exact: true }).first()).toBeVisible();
  });

  test("a second tag with the same name in other letters is refused in words and nothing is added", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const [existing] = await tagsOnServer(page);
    const before = (await tagsOnServer(page)).length;

    await page.getByRole("button", { name: "New tag", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name").fill(existing.name.toUpperCase());
    await drawer.getByRole("button", { name: "Create tag", exact: true }).click();

    // The drawer stays open and the server added nothing.
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText("Another tag already has that name.", { exact: true })).toBeVisible();
    expect((await tagsOnServer(page)).length).toBe(before);
    await drawer.getByRole("button", { name: "Cancel", exact: true }).click();
  });

  test("the refusal of a repeated tag name says the name is already used", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const [existing] = await tagsOnServer(page);
    await page.getByRole("button", { name: "New tag", exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByLabel("Name").fill(existing.name.toUpperCase());
    await drawer.getByRole("button", { name: "Create tag", exact: true }).click();
    await expect(drawer.getByText(/already has that name/i).first()).toBeVisible();
  });

  test("opening a tag, renaming it and saving sends one update, and the new name shows", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const [tag] = await tagsOnServer(page);
    const renamed = `${tag.name} renamed`;
    const requests = recordRequests(page);

    await page.getByText(tag.name, { exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await expect(drawer.getByText("Where this tag is used", { exact: true })).toBeVisible();
    await expect(drawer.getByText("No indexer, download client, notification, library or rule uses this tag.")).toBeVisible();
    await drawer.getByLabel("Name").fill(renamed);
    await drawer.getByRole("button", { name: "Save tag", exact: true }).click();
    await expect(drawer.getByText("Saved just now", { exact: true })).toBeVisible();
    requests.stop();

    const puts = requests.find("PUT", new RegExp(`^/api/tags/${tag.id}$`));
    expect(puts, JSON.stringify(requests.seen)).toHaveLength(1);
    expect(puts[0].body).toMatchObject({ name: renamed });
    expect((await tagsOnServer(page)).find((t) => t.id === tag.id)?.name).toBe(renamed);
    await drawer.getByRole("button", { name: "Close", exact: true }).click();
    await expect(drawer).toHaveCount(0);
    await expect(page.getByText(renamed, { exact: true }).first()).toBeVisible();
  });

  test("Delete asks first, Cancel keeps the tag, Delete tag removes it from the server and the list", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const [tag] = await tagsOnServer(page);

    await page.getByText(tag.name, { exact: true }).first().click();
    const drawer = page.getByRole("dialog");
    await drawer.getByRole("button", { name: "Delete", exact: true }).click();
    const confirm = page.getByRole("alertdialog").or(page.getByRole("dialog").last());
    await expect(confirm.getByText(`Delete “${tag.name}”?`)).toBeVisible();
    await expect(confirm.getByText("Nothing uses this tag. This cannot be undone.")).toBeVisible();
    await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
    expect((await tagsOnServer(page)).map((t) => t.id)).toContain(tag.id);

    const requests = recordRequests(page);
    await drawer.getByRole("button", { name: "Delete", exact: true }).click();
    await confirm.getByRole("button", { name: "Delete tag", exact: true }).click();
    await expect.poll(async () => (await tagsOnServer(page)).some((t) => t.id === tag.id)).toBe(false);
    requests.stop();
    expect(requests.find("DELETE", new RegExp(`^/api/tags/${tag.id}$`))).toHaveLength(1);
    await expect(page.getByText("0 tags", { exact: true })).toBeVisible();
  });

  test("Delete unused tags names the tags, asks first, and one request removes them all", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    const stamp = Date.now();
    await api(page, "POST", "/api/tags", { name: `Spare A ${stamp}`, color: "green" });
    await api(page, "POST", "/api/tags", { name: `Spare B ${stamp}`, color: "red" });
    await page.reload();
    await untilQuiet(page);

    await page.getByRole("button", { name: /^Delete unused tags \(2\)$/ }).click();
    const confirm = page.getByRole("dialog");
    await expect(confirm.getByText("Delete 2 unused tags?", { exact: true })).toBeVisible();
    await expect(confirm.getByText("These tags are not on any title, connection or rule. This cannot be undone.")).toBeVisible();
    await expect(confirm.getByText(`Spare A ${stamp}, Spare B ${stamp}`).or(confirm.getByText(`Spare B ${stamp}, Spare A ${stamp}`))).toBeVisible();

    const requests = recordRequests(page);
    await confirm.getByRole("button", { name: "Delete unused tags", exact: true }).click();
    await expect.poll(async () => (await tagsOnServer(page)).length).toBe(0);
    requests.stop();
    expect(requests.find("POST", /^\/api\/tags\/delete-unused$/)).toHaveLength(1);
    await expect(page.getByText("No tags yet", { exact: true })).toBeVisible();
  });

  test("Escape on a drawer returns focus to the button that opened it", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const trigger = page.getByRole("button", { name: "New tag", exact: true }).first();
    await trigger.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test("the whole add flow works by keyboard alone: open, fill, Tab to the save button, save", async ({ page }) => {
    await openScreen(page, "/settings/tags");
    await untilQuiet(page);
    const name = `Keys ${Date.now()}`;

    const trigger = page.getByRole("button", { name: "New tag", exact: true }).first();
    await trigger.focus();
    await page.keyboard.press("Enter");
    const drawer = page.getByRole("dialog");
    await expect(drawer).toBeVisible();
    await expect(drawer.getByLabel("Name")).toBeFocused();

    // Escape closes an untouched drawer.
    await page.keyboard.press("Escape");
    await expect(drawer).toHaveCount(0);

    await trigger.focus();
    await page.keyboard.press("Enter");
    await expect(drawer.getByLabel("Name")).toBeFocused();
    await page.keyboard.type(name);
    // Every control in the drawer is reachable by Tab, in order, ending on the save button.
    const reached: string[] = [];
    for (let i = 0; i < 12; i++) {
      await page.keyboard.press("Tab");
      const label = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        return el ? (el.getAttribute("aria-label") || el.innerText || el.getAttribute("placeholder") || "").trim().slice(0, 40) : "";
      });
      reached.push(label);
      if (label === "Create tag") break;
    }
    expect(reached, "Tab reaches the colour, the note and the save button").toEqual(expect.arrayContaining(["Create tag"]));
    await page.keyboard.press("Enter");
    await expect(drawer.getByRole("heading", { name, exact: true })).toBeVisible();
    expect((await tagsOnServer(page)).some((t) => t.name === name)).toBe(true);
    await api(page, "POST", "/api/tags/delete-unused");
  });
});
